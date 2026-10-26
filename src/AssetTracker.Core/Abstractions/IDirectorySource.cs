namespace AssetTracker.Core.Abstractions;

/// <summary>A user entry as read from the directory, before mapping.</summary>
public sealed record DirectoryEntry(
    string? DirectoryId,
    string? Sid,
    string? Username,
    string? DisplayName,
    string? JobTitle,
    string? Email,
    string? SiteValue,
    bool Enabled);

/// <summary>Read-only directory adapter. Production: paged AD DirectorySearcher. Tests: in-memory fake.</summary>
public interface IDirectorySource
{
    /// <summary>Returns the full in-scope user set. Throws on query failure; must page through all results.</summary>
    Task<IReadOnlyList<DirectoryEntry>> FetchAllUsersAsync(CancellationToken ct);
}
