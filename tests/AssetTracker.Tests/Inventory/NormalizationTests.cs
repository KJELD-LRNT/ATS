using AssetTracker.Core.Common;

namespace AssetTracker.Tests.Inventory;

public class NormalizationTests
{
    [Theory]
    [InlineData("+31 20 123 4567", "+31201234567")]
    [InlineData("0031-20-123-4567", "+31201234567")]
    [InlineData(" +1 (555) 010.0199 ", "+15550100199")]
    [InlineData("+4915123456789", "+4915123456789")]
    public void Phone_numbers_are_normalized(string input, string expected) =>
        Assert.Equal(expected, Normalization.PhoneNumber(input));

    [Theory]
    [InlineData("020 123 4567")]      // no country code
    [InlineData("+0 20 123 4567")]    // country code cannot start with 0
    [InlineData("+31 20 12A 4567")]   // letters
    [InlineData("+3120123")]          // 7 digits
    [InlineData("+3120123456789012")] // 16 digits
    [InlineData("   ")]
    public void Invalid_phone_numbers_are_rejected(string input) =>
        Assert.ThrowsAny<DomainException>(() => Normalization.PhoneNumber(input));

    [Fact]
    public void Blank_optional_identifiers_become_null()
    {
        Assert.Null(Normalization.OptionalPhoneNumber("  "));
        Assert.Null(Normalization.OptionalImei(null));
    }

    [Theory]
    [InlineData("490154203237518", "490154203237518")]
    [InlineData("35-693803-564380-9", "356938035643809")]
    [InlineData(" 3500 0000 0000 014 ", "350000000000014")]
    public void Imeis_are_normalized(string input, string expected) =>
        Assert.Equal(expected, Normalization.Imei(input));

    [Theory]
    [InlineData("490154203237519")]  // bad check digit
    [InlineData("49015420323751")]   // 14 digits
    [InlineData("4901542032375180")] // 16 digits
    [InlineData("49015420323751X")]
    public void Invalid_imeis_are_rejected(string input) =>
        Assert.ThrowsAny<DomainException>(() => Normalization.Imei(input));

    [Fact]
    public void Codes_are_trimmed_and_upper_cased() => Assert.Equal("LT-0042", Normalization.Code("  lt-0042 "));
}
