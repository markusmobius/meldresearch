using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MeldResearch.BrowserProtocol;

namespace MeldResearch.Tests;

public sealed class WireProtocolTests
{
    [Fact]
    public async Task NativeMessagesRoundTripWithFragmentedReads()
    {
        var expected = new BridgeMessage("error", Error: "synthetic-\u03b1");
        using var encoded = new MemoryStream();
        await WireProtocol.WriteNativeAsync(encoded, WireProtocol.Encode(expected), TestContextToken);
        await WireProtocol.WriteNativeAsync(encoded, WireProtocol.Encode(new("complete", "second")), TestContextToken);
        using var fragmented = new FragmentedStream(encoded.ToArray());
        Assert.Equal(expected, WireProtocol.Decode((await WireProtocol.ReadNativeAsync(fragmented, TestContextToken))!));
        Assert.Equal("second", WireProtocol.Decode((await WireProtocol.ReadNativeAsync(fragmented, TestContextToken))!).Id);
        Assert.Null(await WireProtocol.ReadNativeAsync(fragmented, TestContextToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WireProtocol.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task RejectsInvalidNativeMessageLengths(int length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        using var input = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.ReadNativeAsync(input, TestContextToken));
    }

    [Fact]
    public async Task AcceptsExactlyTheMaximumNativeFrame()
    {
        using var encoded = new MemoryStream();
        var payload = new byte[WireProtocol.MaxMessageBytes];
        await WireProtocol.WriteNativeAsync(encoded, payload, TestContextToken);
        encoded.Position = 0;
        Assert.Equal(payload, await WireProtocol.ReadNativeAsync(encoded, TestContextToken));
    }

    [Theory]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 4, 0, 0, 0, 1, 2 })]
    public async Task RejectsTruncatedPrefixOrPayload(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<EndOfStreamException>(() => WireProtocol.ReadNativeAsync(stream, TestContextToken));
    }

    [Theory]
    [InlineData("""{"type":"start","type":"complete"}""")]
    [InlineData("""{"type":"start","unexpected":"value"}""")]
    public void RejectsAmbiguousJson(string json) =>
        Assert.Throws<JsonException>(() => WireProtocol.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void RejectsMissingMessageType() =>
        Assert.Throws<InvalidDataException>(() => WireProtocol.Decode("{}"u8.ToArray()));

    private static CancellationToken TestContextToken => CancellationToken.None;

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
