using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Inventory;

/// <summary>An active custom field of an asset's type with the values the asset has for it (possibly none).</summary>
public sealed record FieldDisplay(AssetFieldDefinition Field, IReadOnlyList<string> Values);

public static class AssetFieldViews
{
    /// <summary>
    /// The asset's custom fields in form order, one entry per active field of its type (values empty when not filled
    /// in). Needs <c>Type.Fields</c> and <c>Values</c> loaded; sensitive values are filtered out when the asset was
    /// loaded for a principal who may not see them.
    /// </summary>
    public static IReadOnlyList<FieldDisplay> FieldDisplays(this IndividualAsset asset)
    {
        var byField = asset.Values.GroupBy(v => v.FieldId).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Position).Select(v => v.Value).ToList());
        var shown = (asset.Type?.Fields ?? []).Where(f => f.IsActive).OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
            .Select(f => new FieldDisplay(f, byField.TryGetValue(f.Id, out var vs) ? vs : []));
        return shown.ToList();
    }

    /// <summary>Values of every field of the given kinds (e.g. phone numbers, IMEIs), in field and position order. Needs <c>Values.Field</c>.</summary>
    public static IReadOnlyList<string> ValuesOfKind(this IndividualAsset asset, params FieldKind[] kinds) =>
        asset.Values.Where(v => v.Field is not null && kinds.Contains(v.Field.Kind))
            .OrderBy(v => v.Field!.SortOrder).ThenBy(v => v.FieldId).ThenBy(v => v.Position).Select(v => v.Value).ToList();
}
