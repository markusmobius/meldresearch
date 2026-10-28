using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using MeldResearch.BrowserProtocol;
using MeldResearch.BrowserHost;

if (args is ["--register", var extensionId])
    return await HostRegistration.RegisterAsync(extensionId);
if (args is ["--register"])
    return await HostRegistration.RegisterAsync(ExtensionIdentity.Id);
if (args is ["--unregister"])
    return HostRegistration.Unregister();

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var socket = new ClientWebSocket();
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();
try
{
    var connection = await RuntimeFiles.ReadConnectionAsync(lifetime.Token);
    string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    socket.Options.CollectHttpResponseDetails = true;
    socket.Options.SetRequestHeader(NativeHandshake.NonceHeader, nonce);
    socket.Options.SetRequestHeader("Authorization",
        NativeHandshake.AuthorizationPrefix + NativeHandshake.CreateClientProof(connection.Token, nonce));
    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{connection.Port}/native"), lifetime.Token);
    var proofs = socket.HttpResponseHeaders?
        .Where(header => string.Equals(header.Key, NativeHandshake.ServerProofHeader, StringComparison.OrdinalIgnoreCase))
        .SelectMany(header => header.Value).ToArray();
    if (proofs is not { Length: 1 } || !NativeHandshake.VerifyServerProof(connection.Token, nonce, proofs[0]))
        throw new InvalidDataException("The local backend did not authenticate itself.");
    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);

    Task toBackend = ForwardToBackendAsync();
    Task toBrowser = ForwardToBrowserAsync();
    Task completed = await Task.WhenAny(toBackend, toBrowser);
    await lifetime.CancelAsync();
    socket.Abort();
    try
    {
        // Console stdin reads are not reliably cancellable on Windows. Process exit closes that pipe.
        await toBrowser;
    }
    catch (Exception exception) when (lifetime.IsCancellationRequested &&
        exception is OperationCanceledException or WebSocketException)
    {
    }
    await completed;
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
    WebSocketException or JsonException or InvalidDataException or OperationCanceledException or InvalidOperationException)
{
    await Console.Error.WriteLineAsync($"Meld Overleaf bridge stopped ({exception.GetType().Name}). No content or credentials logged.");
    try
    {
        await WireProtocol.WriteNativeAsync(output,
            WireProtocol.Encode(new BridgeMessage("hostError", Error: "backend_unavailable")), CancellationToken.None);
    }
    catch (IOException)
    {
        await Console.Error.WriteLineAsync("The browser messaging channel is closed.");
    }
    return 1;
}
finally
{
    await lifetime.CancelAsync();
    socket.Abort();
}

async Task ForwardToBackendAsync()
{
    while (await WireProtocol.ReadNativeAsync(input, lifetime.Token) is { } payload)
    {
        _ = WireProtocol.Decode(payload);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, lifetime.Token);
    }
}

async Task ForwardToBrowserAsync()
{
    while (await WireProtocol.ReadSocketAsync(socket, lifetime.Token) is { } payload)
        await WireProtocol.WriteNativeAsync(output, payload, lifetime.Token);
}
