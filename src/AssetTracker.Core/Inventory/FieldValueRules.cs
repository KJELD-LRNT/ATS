using System.Globalization;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Inventory;

/// <summary>Validation and canonical forms for custom field values, by <see cref="FieldKind"/>.</summary>
public static class FieldValueRules
{
    public const int ValueMaxLength = 500;
    public const int UniqueValueMaxLength = 400;
    public const int MaxValuesLimit = 5;
    public const int MaxOptions = 50;

    /// <summary>Canonical form of a submitted value, or null when blank. Errors name the field.</summary>
    public static string? Normalize(AssetFieldDefinition field, string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        try
        {
            switch (field.Kind)
            {
                case FieldKind.PhoneNumber: return Normalization.PhoneNumber(s);
                case FieldKind.Imei: return Normalization.Imei(s);
                case FieldKind.Number:
                    if (!decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                        throw new DomainException("field.number", "must be a number.");
                    return n.ToString(CultureInfo.InvariantCulture);
                case FieldKind.Date:
                    if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        throw new DomainException("field.date", "must be a date (yyyy-MM-dd).");
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case FieldKind.Choice:
                    var option = OptionList(field).FirstOrDefault(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase));
                    return option ?? throw new DomainException("field.choice", $"'{s}' is not one of the choices.");
                default:
                    if (s.Length > ValueMaxLength)
                        throw new DomainException("field.length", $"may be at most {ValueMaxLength} characters.");
                    return s;
            }
        }
        catch (DomainException ex)
        {
            throw new DomainException(ex.Code, $"{field.Name}: {ex.Message}");
        }
    }

    /// <summary>Allowed values of a choice field.</summary>
    public static IReadOnlyList<string> OptionList(AssetFieldDefinition field) => ParseOptions(field.Options);

    public static IReadOnlyList<string> ParseOptions(string? options) =>
        (options ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The value the unique index sees. Phone numbers and IMEIs are unique across all fields of that kind; other
    /// unique fields only among themselves. Null for fields that are not unique.
    /// </summary>
    public static string? UniqueKey(AssetFieldDefinition field, string value)
    {
        if (!field.IsUnique) return null;
        if (value.Length > UniqueValueMaxLength)
            throw new DomainException("field.unique_length",
                $"{field.Name}: a unique value may be at most {UniqueValueMaxLength} characters.");
        return field.Kind is FieldKind.PhoneNumber or FieldKind.Imei
            ? $"{field.Kind}:{value}"
            : $"F{field.Id}:{value.ToUpperInvariant()}";
    }
}
