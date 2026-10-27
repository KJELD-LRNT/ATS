namespace AssetTracker.Core.Auditing;

/// <summary>
/// Old names in the append-only audit log. Floor-plan teams were renamed to zones: rows written before that keep their
/// <c>team.*</c> actions and the <c>FloorTeam</c> entity type (the log is never rewritten), new rows use <c>zone.*</c>
/// and <c>FloorZone</c>. The audit page shows and filters both under the new name.
/// </summary>
public static class AuditNames
{
    private static readonly (string Now, string Was)[] ActionPrefixes = [("zone", "team")];
    private static readonly (string Now, string Was)[] EntityTypes = [("FloorZone", "FloorTeam")];

    /// <summary>The action as it is called now (<c>team.create</c> → <c>zone.create</c>); other actions unchanged.</summary>
    public static string CurrentAction(string action)
    {
        foreach (var (now, was) in ActionPrefixes)
            if (action.StartsWith(was + ".", StringComparison.Ordinal)) return now + action[was.Length..];
        return action;
    }

    /// <summary>The entity type as it is called now (<c>FloorTeam</c> → <c>FloorZone</c>); other types unchanged.</summary>
    public static string CurrentEntityType(string entityType)
    {
        foreach (var (now, was) in EntityTypes)
            if (entityType == was) return now;
        return entityType;
    }

    /// <summary>
    /// The action filter values to match: the value itself and, when it starts with a current name that had another
    /// name before (<c>zone</c>), the same value under the old name (<c>zone.create</c> also matches <c>team.create</c>,
    /// <c>zone*</c> also <c>team*</c>).
    /// </summary>
    public static IReadOnlyList<string> ActionAliases(string action)
    {
        var list = new List<string> { action };
        foreach (var (now, was) in ActionPrefixes)
            if (action.StartsWith(now, StringComparison.Ordinal)) list.Add(was + action[now.Length..]);
        return list;
    }

    /// <summary>The entity types to match for a filter value (<c>FloorZone</c> or <c>FloorTeam</c> → both).</summary>
    public static IReadOnlyList<string> EntityTypeAliases(string entityType)
    {
        var current = CurrentEntityType(entityType);
        var list = new List<string> { current };
        foreach (var (now, was) in EntityTypes)
            if (current == now) list.Add(was);
        return list;
    }
}
