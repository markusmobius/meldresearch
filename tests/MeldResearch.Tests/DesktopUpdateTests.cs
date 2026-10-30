using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.LocalBackend.Services.Updates;

namespace MeldResearch.Tests;

public sealed class DesktopUpdateTests : IDisposable
{
    private static readonly string Current = new('a', 64), Newer = new('b', 64);
    private static readonly DateTimeOffset CurrentDate = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TemporaryDirectory _temporary = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    private string Runtime => OperatingSystem.IsWindows() ? "win-x64" : "osx-arm64";
    private string Root => _temporary.Path;

    public DesktopUpdateTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, Current[..12], "local"));
        new InstallationPointer(Current[..12], null, []).Write(Root);
    }

    public void Dispose()
    {
        _key.Dispose();
        _temporary.Dispose();
    }

    [Fact]
    public void VerifiesSignedManifestsAndRejectsTamperingOrOtherKeys()
    {
        var envelope = Sign(Manifest(Newer, CurrentDate.AddDays(1), [1, 2, 3]));
        Assert.Equal(Newer, DesktopUpdates.Verify(envelope, PublicKey).Version);

        var tampered = envelope with { Manifest = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Manifest)).Replace(Newer, new string('c', 64)))) };
        Assert.Throws<InvalidDataException>(() => DesktopUpdates.Verify(tampered, PublicKey));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => DesktopUpdates.Verify(envelope, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.Throws<InvalidDataException>(() => DesktopUpdates.Verify(
            Sign(Manifest(Newer.ToUpperInvariant(), CurrentDate.AddDays(1), [1])), PublicKey));
        Assert.Throws<InvalidDataException>(() => DesktopUpdates.Verify(Sign(new
        {
            product = DesktopUpdates.Product, runtime = "linux-x64", version = Newer, versionDate = CurrentDate, length = 1, sha256 = new string('0', 64)
        }), PublicKey));
        Assert.Throws<InvalidDataException>(() => DesktopUpdates.ReadEnvelope(new byte[DesktopUpdates.MaxEnvelopeBytes + 1]));
    }

    [Fact]
    public async Task InstallsAVerifiedNewerReleaseSideBySideAndRepointsTheLauncher()
    {
        byte[] package = Package(Newer);
        var server = new Server(Sign(Manifest(Newer, CurrentDate.AddDays(1), package)), package);
        var registrar = new Registrar();
        var updater = Updater(server, registrar);

        Assert.True(await updater.CheckAsync(CancellationToken.None));

        string installed = Path.Combine(Root, Newer[..12]);
        Assert.True(File.Exists(Path.Combine(installed, "frontend", "index.html")));
        var pointer = InstallationPointer.Read(Root);
        Assert.Equal((Newer[..12], Current[..12]), (pointer.Current, pointer.Previous));
        Assert.Equal(File.ReadAllText(Path.Combine(installed, Launcher)), File.ReadAllText(Path.Combine(Root, Launcher)));
        Assert.Equal(Path.Combine(installed, "browser-host", "MeldResearch.BrowserHost" + Executable), Assert.Single(registrar.Registered));
        Assert.False(Directory.Exists(Path.Combine(Root, ".staging", Newer[..12])));
        Assert.Equal(["/api/updates/" + Runtime, $"/api/updates/{Runtime}/package/{Newer}"], server.Requests);
        Assert.Equal("ready", updater.Status.UpdateStatus);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("older")]
    [InlineData("rejected")]
    public async Task NeverInstallsTheSameAnOlderOrAPreviouslyFailedRelease(string scenario)
    {
        string version = scenario == "same" ? Current : Newer;
        var date = scenario == "older" ? CurrentDate : CurrentDate.AddDays(1);
        if (scenario == "rejected") new InstallationPointer(Current[..12], null, [Newer]).Write(Root);
        byte[] package = Package(version);
        var server = new Server(Sign(Manifest(version, date, package)), package);

        Assert.False(await Updater(server, new Registrar()).CheckAsync(CancellationToken.None));
        Assert.Single(server.Requests);
        Assert.Equal(Current[..12], InstallationPointer.Read(Root).Current);
        Assert.False(Directory.Exists(Path.Combine(Root, Newer[..12])));
    }

    [Fact]
    public async Task RejectsBadSignaturesAndAlteredPackagesWithoutChangingTheInstallation()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] package = Package(Newer);
        var forged = new Server(Sign(Manifest(Newer, CurrentDate.AddDays(1), package), attacker), package);
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(forged, new Registrar()).CheckAsync(CancellationToken.None));
        Assert.Single(forged.Requests);

        byte[] altered = (byte[])package.Clone();
        altered[^10] ^= 0xff;
        var swapped = new Server(Sign(Manifest(Newer, CurrentDate.AddDays(1), package)), altered);
        var registrar = new Registrar();
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(swapped, registrar).CheckAsync(CancellationToken.None));

        Assert.Equal(Current[..12], InstallationPointer.Read(Root).Current);
        Assert.False(Directory.Exists(Path.Combine(Root, Newer[..12])));
        Assert.Empty(registrar.Registered);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Root, ".staging")));
    }

    [Fact]
    public async Task FailedNativeHostRegistrationRestoresTheCurrentVersionAndKeepsThePointer()
    {
        byte[] package = Package(Newer);
        var server = new Server(Sign(Manifest(Newer, CurrentDate.AddDays(1), package)), package);
        var registrar = new Registrar { FailFor = Newer[..12] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater(server, registrar).CheckAsync(CancellationToken.None));
        Assert.Equal(Current[..12], InstallationPointer.Read(Root).Current);
        Assert.Contains(registrar.Registered, path => path.Contains(Current[..12], StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsAPackageForAnotherReleaseOrWithUnsafeEntries()
    {
        byte[] mislabeled = Package(new string('c', 64));
        var server = new Server(Sign(Manifest(Newer, CurrentDate.AddDays(1), mislabeled)), mislabeled);
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(server, new Registrar()).CheckAsync(CancellationToken.None));

        string archive = Path.Combine(Root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
        Assert.Throws<InvalidDataException>(() => DesktopUpdater.Extract(archive, Path.Combine(Root, "unsafe")));
        Assert.False(File.Exists(Path.Combine(Root, "outside.txt")));
    }

    [Fact]
    public void PruningKeepsOnlyTheCurrentAndPreviousReleases()
    {
        string previous = new string('9', 12), old = new string('8', 12);
        foreach (string folder in new[] { previous, old, ".staging", "0.3.0" })
            Directory.CreateDirectory(Path.Combine(Root, folder));
        new InstallationPointer(Current[..12], previous, []).Write(Root);
        DesktopUpdater.Prune(Root, Current[..12]);
        Assert.Equal(new[] { "0.3.0", previous, Current[..12] }.Order(),
            Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Order());
    }

    private DesktopUpdater Updater(Server server, Registrar registrar) => new(
        new UpdateOptions(true, null, Current, CurrentDate, Runtime, Root, PublicKey, new Uri("https://updates.example.invalid/"),
            true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
        new Clients(new HttpClient(server)), new IdleResearch(), new IdleBrowser(), new Lifetime(), registrar,
        TimeProvider.System, NullLogger<DesktopUpdater>.Instance);

    private object Manifest(string version, DateTimeOffset date, byte[] package) => new
    {
        product = DesktopUpdates.Product, runtime = Runtime, version, versionDate = date, length = package.LongLength,
        sha256 = Convert.ToHexStringLower(SHA256.HashData(package))
    };

    private SignedDesktopUpdate Sign(object manifest, ECDsa? key = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] signature = (key ?? _key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new(Convert.ToBase64String(bytes), Convert.ToBase64String(signature));
    }

    private byte[] Package(string version)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
            Add("installation.json", JsonSerializer.Serialize(new { Version = version, Runtime }));
            Add("local/MeldResearch.LocalBackend" + Executable, "synthetic");
            Add("browser-host/MeldResearch.BrowserHost" + Executable, "synthetic");
            Add("frontend/index.html", "<!doctype html>");
            Add(Launcher, "# synthetic launcher " + version);
        }
        return memory.ToArray();
    }

    private static string Executable => OperatingSystem.IsWindows() ? ".exe" : "";
    private static string Launcher => OperatingSystem.IsWindows() ? "Start-Meld.ps1" : "Start-Meld.command";

    private sealed class Server(SignedDesktopUpdate envelope, byte[] package) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            HttpContent content = request.RequestUri.AbsolutePath.Contains("/package/", StringComparison.Ordinal)
                ? new ByteArrayContent(package) : new StringContent(JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class Registrar : INativeHostRegistrar
    {
        public List<string> Registered { get; } = [];
        public string? FailFor { get; init; }
        public Task RegisterAsync(string browserHostExecutable, CancellationToken ct)
        {
            Registered.Add(browserHostExecutable);
            return FailFor is not null && browserHostExecutable.Contains(FailFor, StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("synthetic registration failure")) : Task.CompletedTask;
        }
    }

    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class IdleResearch : IResearchCoordinator
    {
        public bool Busy => false;
        public Task<ResearchStatusResponse> StatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountLinkResponse> BeginLinkAsync(string callbackUri, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteLinkAsync(string? code, string? state, CancellationToken ct) => throw new NotSupportedException();
        public Task UnlinkAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SetMonitoringAsync(string projectId, SetProjectMonitoringRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task ForgetAsync(string projectId, CancellationToken ct) => throw new NotSupportedException();
        public void RequestSync() { }
    }

    private sealed class IdleBrowser : IBrowserRelay
    {
        public BridgeStatusResponse Status => new(false, false, WireProtocol.Version);
        public Task ConnectAsync(System.Net.WebSockets.WebSocket socket, string localUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrowserResponse> FetchAsync(string operation, string? projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
