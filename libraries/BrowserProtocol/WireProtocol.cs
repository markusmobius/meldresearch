using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeldResearch.BrowserProtocol;

public sealed record BridgeMessage(
    string Type,
    string? Id = null,
    string? Operation = null,
    string? ProjectId = null,
    int? Status = null,
    string? ContentType = null,
    string? Data = null,
    int? Sequence = null,
    string? Error = null,
    int? Version = null,
    string? LocalUrl = null);

public static class WireProtocol
{
    public const int Version = 2;
    public const int ChunkBytes = 64 * 1024;
    public const int MaxMessageBytes = 256 * 1024;
    public const long MaxHtmlBytes = 8 * 1024 * 1024;
    public const long MaxArchiveBytes = 100 * 1024 * 1024;
    public const string HostName = "org.meldresearch.overleaf";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };

    public static byte[] Encode(BridgeMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    public static BridgeMessage Decode(byte[] bytes)
    {
        var message = JsonSerializer.Deserialize<BridgeMessage>(bytes, Json);
        if (message is null || string.IsNullOrWhiteSpace(message.Type))
            throw new InvalidDataException("Missing bridge message type.");
        return message;
    }

    public static async Task<byte[]?> ReadNativeAsync(Stream input, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        int read = await input.ReadAsync(prefix, cancellationToken);
        if (read == 0)
            return null;

        await input.ReadExactlyAsync(prefix.AsMemory(read), cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is 0 or > MaxMessageBytes)
            throw new InvalidDataException("Native message exceeds protocol limits.");

        var payload = new byte[(int)length];
        await input.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    public static async Task WriteNativeAsync(Stream output, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaxMessageBytes)
            throw new InvalidDataException("Native message exceeds protocol limits.");

        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await output.WriteAsync(prefix, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]?> ReadSocketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                if (message.Length != 0)
                    throw new InvalidDataException("Truncated WebSocket message.");
                return null;
            }
            if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > MaxMessageBytes)
                throw new InvalidDataException("Invalid WebSocket message.");

            message.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage)
                return message.ToArray();
        }
    }
}
