namespace AssetTracker.Core.Keys;

public static class KeyAccessBoardExtensions
{
    public static bool Inherits(this KeyAccessBoard board, Guid master, Guid subordinate) =>
        KeyAccessService.Descendants(master, board.Masters).Contains(subordinate);
}
