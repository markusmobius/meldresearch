using MeldResearch.BrowserProtocol;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeldResearch.RemoteBackend.Data;

// Personal free text (names, email addresses, device names) is stored as ASP.NET Data Protection ciphertext;
// credentials and one-time sign-in codes are stored only as SHA-256 hashes.
internal sealed class AccountEntity
{
    public Guid Id { get; set; }
    public required string Subject { get; set; }
    public required string ProtectedName { get; set; }
    public required string ProtectedEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public AccountEntity? Account { get; set; }
    public required string TokenHash { get; set; }
    public required string ProtectedName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// A one-time code from a browser sign-in for a local installation (loopback redirect with PKCE). The code reaches only
/// the installation's own loopback callback; redeeming it also requires the verifier whose hash is the challenge.
/// </summary>
internal sealed class SignInCodeEntity
{
    public required string CodeHash { get; set; }
    public Guid AccountId { get; set; }
    public AccountEntity? Account { get; set; }
    public required string ProtectedName { get; set; }
    public required string Challenge { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class RemoteDbContext(DbContextOptions<RemoteDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<SignInCodeEntity> SignInCodes => Set<SignInCodeEntity>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // SQLite cannot compare DateTimeOffset values in SQL; UTC Unix milliseconds keep expiry queries server-side.
    protected override void ConfigureConventions(ModelConfigurationBuilder configuration) =>
        configuration.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AccountEntity>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(item => item.Id);
            account.Property(item => item.Subject).HasMaxLength(300);
            account.HasIndex(item => item.Subject).IsUnique();
        });
        model.Entity<DeviceEntity>(device =>
        {
            device.ToTable("devices");
            device.HasKey(item => item.Id);
            device.Property(item => item.TokenHash).HasMaxLength(64);
            device.HasIndex(item => item.TokenHash).IsUnique();
            device.HasIndex(item => item.ExpiresAt);
            device.HasOne(item => item.Account).WithMany().HasForeignKey(item => item.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SignInCodeEntity>(code =>
        {
            code.ToTable("sign_in_codes");
            code.HasKey(item => item.CodeHash);
            code.Property(item => item.CodeHash).HasMaxLength(64);
            code.Property(item => item.Challenge).HasMaxLength(64);
            code.HasIndex(item => item.ExpiresAt);
            code.HasOne(item => item.Account).WithMany().HasForeignKey(item => item.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}

internal sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value));

internal static class RemoteDatabase
{
    public const string FileName = "remote.db";

    public static string ConnectionString(string dataDirectory) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dataDirectory, FileName),
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        DefaultTimeout = 30
    }.ToString();

    public static async Task InitializeAsync(IDbContextFactory<RemoteDbContext> databases, string dataDirectory, CancellationToken ct)
    {
        string path = Path.Combine(dataDirectory, FileName);
        // SQLite applies the main file's permissions to its -wal/-shm files, so create it owner-only first.
        if (!File.Exists(path)) RuntimeFiles.CreatePrivateFile(path).Dispose();
        await using var database = await databases.CreateDbContextAsync(ct);
        await database.Database.MigrateAsync(ct);
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}

internal sealed class RemoteDbContextDesignFactory : IDesignTimeDbContextFactory<RemoteDbContext>
{
    public RemoteDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RemoteDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
