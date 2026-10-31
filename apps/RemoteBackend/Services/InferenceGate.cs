using System.Collections.Concurrent;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

/// <summary>
/// Limits how many model requests one account has waiting or in flight (Project Meld's per-client limit), so one
/// installation importing several projects in parallel cannot crowd out other accounts. Further requests wait their turn.
/// </summary>
internal sealed class InferenceGate
{
    private readonly int _perAccount;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _accounts = new();
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);

    public InferenceGate(IConfiguration configuration)
    {
        _perAccount = configuration.GetValue("Foundry:MaxConcurrencyPerAccount", 3);
        if (_perAccount is < 1 or > 16) throw new InvalidOperationException("Foundry:MaxConcurrencyPerAccount must be between 1 and 16.");
    }

    public async Task<IDisposable> EnterAsync(Guid account, CancellationToken ct)
    {
        var gate = _accounts.GetOrAdd(account, _ => new SemaphoreSlim(_perAccount, _perAccount));
        if (!await gate.WaitAsync(Wait, ct))
            throw new ResearchFailure(429, "foundry_busy", "This account already has several model requests running. Meld retries shortly.");
        return new Slot(gate);
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }
}
