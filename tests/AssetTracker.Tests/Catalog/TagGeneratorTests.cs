using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Catalog;

public class TagPatternTests
{
    [Theory]
    [InlineData("{SITE}D{000}OMA", "R", 1, "RD001OMA")]
    [InlineData("{SITE}D{000}OMA", "R", 42, "RD042OMA")]
    [InlineData("{SITE}D{000}OMA", "rtm", 1234, "RTMD1234OMA")] // more digits than the padding: no truncation
    [InlineData("{SITE}-LT-{0000}", "AMS", 7, "AMS-LT-0007")]
    [InlineData("pc{00}", null, 5, "PC05")] // upper-cased like every tag
    public void Renders_the_site_code_and_the_padded_number(string pattern, string? site, int number, string expected) =>
        Assert.Equal(expected, TagPattern.Render(pattern, site, number));

    [Fact]
    public void Blank_pattern_means_no_generator() =>
        Assert.Null(TagPattern.Validate("   "));

    [Theory]
    [InlineData("{SITE}D")]            // no number token
    [InlineData("D{000}{000}")]        // two number tokens
    [InlineData("D{0}{SITE}x y {000}")] // a space is not a literal character
    [InlineData("{SITE}{FOO}{000}")]   // unknown token
    [InlineData("{SITE}D{000")]        // unbalanced brace
    [InlineData("D{0000000000}")]      // more than 9 digits
    public void Rejects_invalid_patterns(string pattern) =>
        Assert.Throws<DomainException>(() => TagPattern.Validate(pattern));

    [Fact]
    public void Rejects_a_pattern_whose_tags_could_exceed_the_tag_length() =>
        Assert.Throws<DomainException>(() => TagPattern.Validate(new string('X', 60) + "{SITE}{000}"));

    [Theory]
    [InlineData("r", "R")]
    [InlineData("  ams ", "AMS")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Site_codes_are_trimmed_and_upper_cased(string? input, string? expected) =>
        Assert.Equal(expected, TagPattern.NormalizeSiteCode(input));

    [Theory]
    [InlineData("A B")]
    [InlineData("TOO-LONG-SITE-CODE-X")]
    [InlineData("R!")]
    public void Rejects_invalid_site_codes(string code) =>
        Assert.Throws<DomainException>(() => TagPattern.NormalizeSiteCode(code));
}

/// <summary>
/// The tag generator ("RD001OMA, RD002OMA ..."): numbers count up per type and site, skip tags that already exist,
/// and are only used up when the asset is saved. Each test makes its own types and sites (the class shares a database).
/// </summary>
public class TagGeneratorTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public TagGeneratorTests(AppFactory f) => _f = f;

    private Task<T> AsAdmin<T>(Func<CatalogService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.Admin, sp => action(sp.GetRequiredService<CatalogService>()));

    private Task<T> AsIt<T>(Func<AssetService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<AssetService>()));

    private async Task<Site> SiteAsync(string key, string? tagCode) =>
        await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, key);
            site.TagCode = tagCode;
            await db.SaveChangesAsync();
            return site;
        });

    private Task<AssetType> TypeAsync(string name, string? pattern) =>
        AsAdmin(c => c.CreateTypeAsync(new TypeInput(name, InventoryMode.Tagged, TagPattern: pattern)));

    private Task<string> NewTagAsync(AssetType type, Site site, string? tag = null) =>
        AsIt(async s => (await s.CreateAsync(new CreateAssetInput(tag, type.Id, site.Id))).Tag);

    private Task<TagPreview> PreviewAsync(AssetType type, Site site) =>
        _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<TagGenerator>().PreviewAsync(type.Id, site.Id));

    [Fact]
    public async Task Counts_up_per_type_and_per_site()
    {
        var rotterdam = await SiteAsync("TG1R", "TG1R");
        var amsterdam = await SiteAsync("TG1A", "TG1A");
        var desktop = await TypeAsync("TG1 Desktop", "{SITE}D{000}OMA");

        Assert.Equal("TG1RD001OMA", await NewTagAsync(desktop, rotterdam));
        Assert.Equal("TG1RD002OMA", await NewTagAsync(desktop, rotterdam));
        Assert.Equal("TG1RD003OMA", await NewTagAsync(desktop, rotterdam));
        // Another site has its own series.
        Assert.Equal("TG1AD001OMA", await NewTagAsync(desktop, amsterdam));
        Assert.Equal("TG1RD004OMA", await NewTagAsync(desktop, rotterdam));
    }

    [Fact]
    public async Task Numbers_are_counted_per_type_even_when_the_patterns_look_alike()
    {
        var site = await SiteAsync("TG2", "TG2");
        var a = await TypeAsync("TG2 A", "{SITE}-X{000}");
        var b = await TypeAsync("TG2 B", "{SITE}-X{000}");

        Assert.Equal("TG2-X001", await NewTagAsync(a, site));
        // B's own counter would also say 001, but that tag exists: the generator moves on to the next free number.
        Assert.Equal("TG2-X002", await NewTagAsync(b, site));
        Assert.Equal("TG2-X003", await NewTagAsync(a, site));
    }

    [Fact]
    public async Task Skips_tags_that_were_typed_by_hand()
    {
        var site = await SiteAsync("TG3", "TG3");
        var type = await TypeAsync("TG3 Laptop", "{SITE}L{000}");

        await NewTagAsync(type, site, "tg3l002"); // typed in the series, upper-cased on save
        Assert.Equal("TG3L001", await NewTagAsync(type, site));
        Assert.Equal("TG3L003", await NewTagAsync(type, site)); // 002 is taken
    }

    [Fact]
    public async Task A_preview_shows_the_next_tag_without_using_it_up()
    {
        var site = await SiteAsync("TG4", "TG4");
        var type = await TypeAsync("TG4 Desktop", "{SITE}D{000}");

        Assert.Equal("TG4D001", (await PreviewAsync(type, site)).Tag);
        Assert.Equal("TG4D001", (await PreviewAsync(type, site)).Tag);
        Assert.Equal("TG4D001", await NewTagAsync(type, site));
        Assert.Equal("TG4D002", (await PreviewAsync(type, site)).Tag);
    }

    [Fact]
    public async Task A_failed_create_does_not_use_up_a_number()
    {
        var site = await SiteAsync("TG5", "TG5");
        var type = await TypeAsync("TG5 Phone", "{SITE}P{000}");
        var imei = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("IMEI", FieldKind.Imei, IsRequired: true)));

        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput(null, type.Id, site.Id, Fields: Seed.Fields((imei.Id, ["not an imei"]))))));

        Assert.Equal("TG5P001", (await PreviewAsync(type, site)).Tag);
        var ok = await AsIt(s => s.CreateAsync(new CreateAssetInput(null, type.Id, site.Id,
            Fields: Seed.Fields((imei.Id, [AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(500)])))));
        Assert.Equal("TG5P001", ok.Tag);
    }

    [Fact]
    public async Task A_type_without_a_pattern_needs_a_typed_tag()
    {
        var site = await SiteAsync("TG6", "TG6");
        var type = await TypeAsync("TG6 Manual", null);

        var ex = await Assert.ThrowsAsync<DomainException>(() => NewTagAsync(type, site));
        Assert.Equal("asset.tag_required", ex.Code);
        Assert.Contains("type the asset tag", (await PreviewAsync(type, site)).Problem);
        Assert.Equal("MANUAL-1", await NewTagAsync(type, site, "manual-1"));
    }

    [Fact]
    public async Task A_pattern_with_a_site_code_needs_the_site_to_have_one()
    {
        var withoutCode = await SiteAsync("TG7", null);
        var type = await TypeAsync("TG7 Desktop", "{SITE}D{000}");

        var preview = await PreviewAsync(type, withoutCode);
        Assert.Null(preview.Tag);
        Assert.Contains("tag code", preview.Problem);
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewTagAsync(type, withoutCode));
        Assert.Equal("asset.tag_generation", ex.Code);

        // A pattern without {SITE} works for any site.
        var plain = await TypeAsync("TG7 Plain", "TG7PLAIN{000}");
        Assert.Equal("TG7PLAIN001", await NewTagAsync(plain, withoutCode));
    }

    [Fact]
    public async Task A_typed_tag_overrides_the_generator_and_does_not_move_the_counter()
    {
        var site = await SiteAsync("TG8", "TG8");
        var type = await TypeAsync("TG8 Desktop", "{SITE}D{000}");

        Assert.Equal("CUSTOM-1", await NewTagAsync(type, site, "custom-1"));
        Assert.Equal("TG8D001", await NewTagAsync(type, site));
    }

    [Fact]
    public async Task The_counter_is_kept_per_type_and_site_and_survives_a_changed_pattern()
    {
        var site = await SiteAsync("TG9", "TG9");
        var type = await TypeAsync("TG9 Desktop", "{SITE}D{000}");
        await NewTagAsync(type, site);
        await NewTagAsync(type, site);

        var counter = await _f.WithDbAsync(db => db.TagCounters.SingleAsync(c => c.TypeId == type.Id && c.SiteId == site.Id));
        Assert.Equal(2, counter.LastNumber);

        // A new pattern carries on counting instead of starting over (the old tags still exist).
        await AsAdmin(c => c.UpdateTypeAsync(type.Id, new TypeInput(type.Name, type.Mode, TagPattern: "{SITE}-DESK-{0000}")));
        Assert.Equal("TG9-DESK-0003", await NewTagAsync(type, site));
    }
}
