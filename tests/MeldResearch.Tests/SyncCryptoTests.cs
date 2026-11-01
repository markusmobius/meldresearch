using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class SyncCryptoTests
{
    private static readonly Guid Account = Guid.NewGuid();

    [Fact]
    public void ThePassphraseOrTheRecoveryKeyUnlocksTheDataKeyAndNothingElseDoes()
    {
        byte[] dataKey = SyncCrypto.NewDataKey(), recovery = SyncCrypto.NewRecoveryKey();
        var file = SyncCrypto.CreateKeyFile(Account, dataKey, "correct horse battery", recovery, DateTimeOffset.UtcNow, iterations: 100_000);
        Assert.Equal(dataKey, SyncCrypto.UnlockWithPassphrase(file, Account, "correct horse battery"));
        Assert.Null(SyncCrypto.UnlockWithPassphrase(file, Account, "wrong horse battery"));
        Assert.Null(SyncCrypto.UnlockWithPassphrase(file, Guid.NewGuid(), "correct horse battery"));

        string written = SyncCrypto.FormatRecoveryKey(recovery);
        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", written);
        Assert.Equal(dataKey, SyncCrypto.UnlockWithRecoveryKey(file, Account, written.ToLowerInvariant().Replace("-", " ")));
        Assert.Null(SyncCrypto.UnlockWithRecoveryKey(file, Account, SyncCrypto.FormatRecoveryKey(SyncCrypto.NewRecoveryKey())));

        var changed = SyncCrypto.ChangePassphrase(file, Account, dataKey, "a new long passphrase");
        Assert.Null(SyncCrypto.UnlockWithPassphrase(changed, Account, "correct horse battery"));
        Assert.Equal(dataKey, SyncCrypto.UnlockWithPassphrase(changed, Account, "a new long passphrase"));
        Assert.Equal(dataKey, SyncCrypto.UnlockWithRecoveryKey(changed, Account, written));

        // An unlocked computer can show the recovery key again; nothing else can read it from the key file.
        Assert.Equal(recovery, SyncCrypto.OpenRecoveryKey(changed, Account, dataKey));
        Assert.Null(SyncCrypto.OpenRecoveryKey(changed, Account, SyncCrypto.NewDataKey()));
        Assert.Null(SyncCrypto.OpenRecoveryKey(changed, Guid.NewGuid(), dataKey));
        Assert.Null(SyncCrypto.OpenRecoveryKey(changed with { SealedRecoveryKey = null }, Account, dataKey));
        byte[] replacement = SyncCrypto.NewRecoveryKey();
        var replaced = SyncCrypto.ReplaceRecoveryKey(changed, Account, dataKey, replacement);
        Assert.Null(SyncCrypto.UnlockWithRecoveryKey(replaced, Account, written));
        Assert.Equal(dataKey, SyncCrypto.UnlockWithRecoveryKey(replaced, Account, SyncCrypto.FormatRecoveryKey(replacement)));
        Assert.Equal(replacement, SyncCrypto.OpenRecoveryKey(replaced, Account, dataKey));
        Assert.Equal(dataKey, SyncCrypto.UnlockWithPassphrase(replaced, Account, "a new long passphrase"));
        Assert.Equal("weak_passphrase", Assert.Throws<ResearchFailure>(() => SyncCrypto.CreateKeyFile(Account, dataKey, "short", null, DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void SealedBlobsOpenOnlyForTheSameKeyAccountAndNameAndHideTheirContent()
    {
        byte[] dataKey = SyncCrypto.NewDataKey();
        var value = new ProjectProfile("Secret title", "UNIQUE-SECRET-TEXT", ["c"], ["t"]);
        string name = SyncCrypto.ProjectBlob(dataKey, "0123456789abcdef01234567");
        Assert.True(SyncCrypto.IsBlobName(name));
        Assert.NotEqual(name, SyncCrypto.ProjectBlob(SyncCrypto.NewDataKey(), "0123456789abcdef01234567"));
        byte[] sealedValue = SyncCrypto.Seal(dataKey, Account, name, value);
        Assert.DoesNotContain("UNIQUE-SECRET", System.Text.Encoding.UTF8.GetString(sealedValue));
        Assert.Equal(value.Description, SyncCrypto.Open<ProjectProfile>(dataKey, Account, name, sealedValue).Description);
        Assert.Throws<ResearchFailure>(() => SyncCrypto.Open<ProjectProfile>(dataKey, Account, SyncCrypto.ProfileBlob, sealedValue));
        Assert.Throws<ResearchFailure>(() => SyncCrypto.Open<ProjectProfile>(dataKey, Guid.NewGuid(), name, sealedValue));
        Assert.Throws<ResearchFailure>(() => SyncCrypto.Open<ProjectProfile>(SyncCrypto.NewDataKey(), Account, name, sealedValue));
        sealedValue[^1] ^= 1;
        Assert.Throws<ResearchFailure>(() => SyncCrypto.Open<ProjectProfile>(dataKey, Account, name, sealedValue));
    }

    [Theory]
    [InlineData("key.json", true)]
    [InlineData("profile.bin", true)]
    [InlineData("p/0123456789abcdef0123456789abcdef.bin", true)]
    [InlineData("../other/key.json", false)]
    [InlineData("p/0123.bin", false)]
    [InlineData("P/0123456789ABCDEF0123456789ABCDEF.bin", false)]
    public void OnlyKnownBlobNamesAreAllowed(string name, bool allowed) => Assert.Equal(allowed, SyncCrypto.IsBlobName(name));
}
