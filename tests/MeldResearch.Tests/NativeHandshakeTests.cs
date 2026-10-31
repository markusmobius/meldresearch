using MeldResearch.BrowserProtocol;

namespace MeldResearch.Tests;

public sealed class NativeHandshakeTests
{
    private const string Key = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Nonce = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";

    [Fact]
    public void AuthenticatesBothPeersWithoutReflectingAClientProof()
    {
        string client = NativeHandshake.CreateClientProof(Key, Nonce);
        string server = NativeHandshake.CreateServerProof(Key, Nonce);
        Assert.True(NativeHandshake.VerifyClientProof(Key, Nonce, client));
        Assert.True(NativeHandshake.VerifyServerProof(Key, Nonce, server));
        Assert.False(NativeHandshake.VerifyServerProof(Key, Nonce, client));
        Assert.False(NativeHandshake.VerifyClientProof(Key, Nonce, server));
        Assert.NotEqual(Key, client);
        Assert.NotEqual(client, server);
    }

    [Fact]
    public void ProofsAreBoundToTheKeyAndNonce()
    {
        string client = NativeHandshake.CreateClientProof(Key, Nonce);
        string server = NativeHandshake.CreateServerProof(Key, Nonce);
        Assert.False(NativeHandshake.VerifyClientProof(new string('A', 64), Nonce, client));
        Assert.False(NativeHandshake.VerifyServerProof(new string('A', 64), Nonce, server));
        Assert.False(NativeHandshake.VerifyClientProof(Key, new string('B', 64), client));
        Assert.False(NativeHandshake.VerifyServerProof(Key, new string('B', 64), server));
        Assert.True(NativeHandshake.VerifyServerProof(Key.ToLowerInvariant(), Nonce.ToLowerInvariant(), server.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF00")]
    public void RejectsMalformedProofsAndNonces(string? invalid)
    {
        Assert.False(NativeHandshake.VerifyClientProof(Key, Nonce, invalid));
        Assert.False(NativeHandshake.VerifyServerProof(Key, Nonce, invalid));
        Assert.False(NativeHandshake.VerifyClientProof(Key, invalid, NativeHandshake.CreateClientProof(Key, Nonce)));
        Assert.False(NativeHandshake.VerifyServerProof(Key, invalid, NativeHandshake.CreateServerProof(Key, Nonce)));
    }

    [Fact]
    public void BundledIdentityMatchesTheChromeDraft()
    {
        Assert.Equal("pejcljoimoeklelcfbfddnkimlkjgfhe", ExtensionIdentity.Id);
        Assert.Equal("0.5.2", ExtensionIdentity.Version);
        Assert.Equal(2, WireProtocol.Version);
    }
}
