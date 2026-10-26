using AssetTracker.Core.Directory;

namespace AssetTracker.Core.Lending;

/// <summary>
/// Whether the local people snapshot is stale. Staleness never blocks lending (mvp-plan §1, §5): checkout records it
/// on the loan event (<see cref="Domain.LoanEvent.DirectoryStale"/>) and in the audit details, and the UI warns IT.
/// Seam for tests; the default implementation delegates to <see cref="DirectorySyncService.GetStatusAsync"/>.
/// </summary>
public interface IDirectoryFreshness
{
    Task<bool> IsStaleAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IDirectoryFreshness"/>: stale when no sync ever succeeded or the latest successful sync is older
/// than <see cref="DirectorySyncOptions.StaleAfter"/> (config <c>DirectorySync:StaleAfter</c>) — one definition shared
/// with the IT sync-status banner.
/// </summary>
public class SyncRunDirectoryFreshness(DirectorySyncService sync) : IDirectoryFreshness
{
    public async Task<bool> IsStaleAsync(CancellationToken ct = default) => (await sync.GetStatusAsync(ct)).IsStale;
}
