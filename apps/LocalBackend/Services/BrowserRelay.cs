using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;

namespace MeldResearch.LocalBackend.Services;

internal interface IBrowserRelay
{
    BridgeStatusResponse Status { get; }
    Task ConnectAsync(WebSocket socket, string localUrl, CancellationToken cancellationToken);
    Task<BrowserResponse> FetchAsync(string operation, string? projectId, CancellationToken cancellationToken);
}

internal sealed class BrowserRelay(ILogger<BrowserRelay> logger) : IBrowserRelay
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private WebSocket? _socket;
    private Transfer? _transfer;
    private string? _connectionVersion;

    public BridgeStatusResponse Status
    {
        get
        {
            lock (_gate)
                return new(_socket?.State == WebSocketState.Open, _transfer is not null, WireProtocol.Version, _connectionVersion);
        }
    }

    public async Task ConnectAsync(WebSocket socket, string localUrl, CancellationToken cancellationToken)
    {
        bool accepted;
        lock (_gate)
        {
            accepted = _socket is null;
            if (accepted)
            {
                _socket = socket;
                _connectionVersion = Guid.NewGuid().ToString("N");
            }
        }
        if (!accepted)
        {
            logger.LogWarning("Rejected a second browser connection.");
            await SendAsync(socket, new("hostError", Error: "connection_in_use"), cancellationToken);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Another browser is connected.", cancellationToken);
            return;
        }

        try
        {
            await SendAsync(socket, new("ready", Version: WireProtocol.Version, LocalUrl: localUrl), cancellationToken);
            logger.LogInformation("Browser bridge connected.");
            while (await WireProtocol.ReadSocketAsync(socket, cancellationToken) is { } bytes)
            {
                BridgeMessage message = WireProtocol.Decode(bytes);
                if (message.Type == "overleafChanged")
                {
                    lock (_gate)
                        _connectionVersion = Guid.NewGuid().ToString("N");
                    continue;
                }
                if (message.Type is not ("start" or "chunk" or "complete" or "error") ||
                    !Guid.TryParseExact(message.Id, "N", out _))
                    throw new InvalidDataException("Invalid browser response envelope.");

                Transfer? transfer;
                lock (_gate)
                    transfer = _transfer;
                if (transfer is null || transfer.Id != message.Id)
                {
                    logger.LogDebug("Discarded a late response for an inactive browser request.");
                    continue;
                }
                try
                {
                    await transfer.Messages.Writer.WriteAsync(message, cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    logger.LogDebug("Discarded a response after its request was cancelled.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Browser bridge stopped with the local backend.");
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or JsonException)
        {
            logger.LogWarning("Browser bridge disconnected ({ExceptionType}); payload omitted.", exception.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _connectionVersion = null;
                    _transfer?.Messages.Writer.TryComplete(
                        new ApiFailure(503, "browser_disconnected", "Reconnect the extension and keep the Overleaf tab open."));
                }
            }
            socket.Abort();
            logger.LogInformation("Browser bridge is offline.");
        }
    }

    public async Task<BrowserResponse> FetchAsync(string operation, string? projectId, CancellationToken cancellationToken)
    {
        // The extension serves one request at a time; later requests queue behind it rather than failing.
        await _operationGate.WaitAsync(cancellationToken);

        var transfer = new Transfer(Guid.NewGuid().ToString("N"));
        string? temporary = null;
        bool retained = false;
        WebSocket? socket = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            lock (_gate)
            {
                socket = _socket;
                if (socket?.State != WebSocketState.Open)
                    throw new ApiFailure(503, "browser_disconnected", "Connect the Meld Overleaf extension first.");
                _transfer = transfer;
            }

            string incoming = Path.Combine(RuntimeFiles.Root, "incoming");
            RuntimeFiles.CreatePrivateDirectory(incoming);
            temporary = Path.Combine(incoming, transfer.Id + ".partial");
            string contentType;
            await using (var destination = RuntimeFiles.CreatePrivateFile(temporary))
            {
                await SendAsync(socket, new("request", transfer.Id, operation, projectId), deadline.Token);
                contentType = await ReceiveAsync(socket, transfer, destination,
                    operation == "downloadProject" ? WireProtocol.MaxArchiveBytes : WireProtocol.MaxHtmlBytes, deadline.Token);
                await destination.FlushAsync(deadline.Token);
            }
            retained = true;
            return new BrowserResponse(temporary, contentType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiFailure(504, "browser_timeout", "The browser operation timed out. Check the Overleaf tab and try again.");
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException)
        {
            throw new ApiFailure(502, "invalid_browser_response", "The browser returned an invalid or incomplete transfer.");
        }
        catch (WebSocketException)
        {
            throw new ApiFailure(503, "browser_disconnected", "The browser connection was lost. Reconnect the extension.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_transfer, transfer))
                    _transfer = null;
            }
            transfer.Messages.Writer.TryComplete();
            if (!retained && socket?.State == WebSocketState.Open)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await SendAsync(socket, new("cancel", transfer.Id), stop.Token);
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                {
                    logger.LogWarning("Could not cancel the browser transfer ({ExceptionType}).", exception.GetType().Name);
                }
            }
            try
            {
                if (!retained && temporary is not null)
                    File.Delete(temporary);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private async Task<string> ReceiveAsync(WebSocket socket, Transfer transfer, Stream destination,
        long limit, CancellationToken cancellationToken)
    {
        bool started = false;
        string? contentType = null;
        long bytes = 0;
        int sequence = 0;
        await foreach (var message in transfer.Messages.Reader.ReadAllAsync(cancellationToken))
        {
            switch (message.Type)
            {
                case "start" when !started && message.Status == 200 && !string.IsNullOrWhiteSpace(message.ContentType):
                    started = true;
                    contentType = message.ContentType;
                    break;
                case "chunk" when started && message.Sequence == sequence && message.Data is not null:
                    if (message.Data.Length > ((WireProtocol.ChunkBytes + 2) / 3) * 4)
                        throw new InvalidDataException("Oversized chunk.");
                    byte[] chunk = Convert.FromBase64String(message.Data);
                    if (chunk.Length is 0 or > WireProtocol.ChunkBytes)
                        throw new InvalidDataException("Invalid chunk length.");
                    if (bytes + chunk.Length > limit)
                        throw new ApiFailure(413, "response_too_large", "The Overleaf response exceeds the prototype's size limit.");
                    await destination.WriteAsync(chunk, cancellationToken);
                    bytes += chunk.Length;
                    await SendAsync(socket, new("ack", transfer.Id, Sequence: sequence++), cancellationToken);
                    break;
                case "complete" when started && bytes > 0:
                    return contentType!;
                case "error":
                    throw BrowserFailures.FromCode(message.Error);
                default:
                    throw new InvalidDataException("Unexpected browser transfer sequence.");
            }
        }
        throw new ApiFailure(503, "browser_disconnected", "The browser transfer ended before it completed.");
    }

    private async Task SendAsync(WebSocket socket, BridgeMessage message, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(WireProtocol.Encode(message), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private sealed class Transfer(string id)
    {
        public string Id { get; } = id;
        public Channel<BridgeMessage> Messages { get; } = Channel.CreateBounded<BridgeMessage>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
    }
}
