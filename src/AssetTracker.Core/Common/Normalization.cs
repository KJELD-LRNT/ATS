namespace AssetTracker.Core.Common;

/// <summary>
/// Canonical forms for identifiers that must be unique: phone numbers (E.164-style "+digits")
/// and IMEIs (15 digits with a valid Luhn check digit). Invalid input throws <see cref="DomainException"/>.
/// </summary>
public static class Normalization
{
    /// <summary>Normalizes a phone number to "+" followed by 8–15 digits, country code included.</summary>
    public static string PhoneNumber(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) throw new DomainException("phone.required", "Phone number is required.");

        var compact = new string(s.Where(c => !(char.IsWhiteSpace(c) || c is '-' or '(' or ')' or '.')).ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal)) compact = "+" + compact[2..];

        if (!compact.StartsWith('+'))
            throw new DomainException("phone.country_code",
                "Phone number must include a country code (start with + or 00).");

        var digits = compact[1..];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            throw new DomainException("phone.invalid", "Phone number may only contain digits after the country prefix.");
        if (digits[0] == '0')
            throw new DomainException("phone.country_code", "Phone number country code cannot start with 0.");
        if (digits.Length is < 8 or > 15)
            throw new DomainException("phone.length", "Phone number must have 8 to 15 digits including the country code.");

        return "+" + digits;
    }

    /// <summary>Like <see cref="PhoneNumber"/>, but blank input yields null.</summary>
    public static string? OptionalPhoneNumber(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : PhoneNumber(input);

    /// <summary>Normalizes an IMEI to exactly 15 digits (spaces/dashes removed) and validates its Luhn check digit.</summary>
    public static string Imei(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) throw new DomainException("imei.required", "IMEI is required.");

        var digits = new string(s.Where(c => !(char.IsWhiteSpace(c) || c is '-' or '/')).ToArray());
        if (digits.Length != 15 || !digits.All(char.IsAsciiDigit))
            throw new DomainException("imei.invalid", "IMEI must be exactly 15 digits.");
        if (!LuhnValid(digits))
            throw new DomainException("imei.checksum", "IMEI check digit is invalid.");
        return digits;
    }

    /// <summary>Like <see cref="Imei"/>, but blank input yields null.</summary>
    public static string? OptionalImei(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : Imei(input);

    /// <summary>Asset tags and pool SKUs are compared trimmed and upper-cased.</summary>
    public static string Code(string? input) => (input ?? "").Trim().ToUpperInvariant();

    /// <summary>Trims free text; blank becomes null.</summary>
    public static string? OptionalText(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    internal static bool LuhnValid(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
