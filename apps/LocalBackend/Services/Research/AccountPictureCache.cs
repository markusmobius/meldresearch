namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Keeps the signed-in account's profile picture (or its absence) in memory for an hour, so the toolbar does not ask Meld's
/// server on every page load. Nothing is written to disk.
/// </summary>
internal sealed class AccountPictureCache(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private (Guid Account, AccountPicture? Picture, DateTimeOffset Until)? _entry;

    public async Task<AccountPicture?> GetAsync(Guid account, Func<Task<AccountPicture?>> load)
    {
        if (_entry is { } entry && entry.Account == account && entry.Until > clock.GetUtcNow()) return entry.Picture;
        var picture = await load();
        _entry = (account, picture, clock.GetUtcNow() + (picture is null ? TimeSpan.FromMinutes(5) : Lifetime));
        return picture;
    }
}
