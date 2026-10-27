using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Admin: one asset type's settings (standard fields, tag pattern, active) and its custom fields.</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class TypeEditModel(CatalogService catalog) : PageModel
{
    public class TypeForm
    {
        public string Name { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public bool UsesModel { get; set; }
        public bool UsesDescription { get; set; }
        public bool UsesSerialNumber { get; set; }
        public bool UsesCondition { get; set; }
        public string? TagPattern { get; set; }
    }

    public class FieldForm
    {
        public string Name { get; set; } = "";
        public FieldKind Kind { get; set; } = FieldKind.Text;
        public bool IsRequired { get; set; }
        public bool IsUnique { get; set; }
        public bool IsSensitive { get; set; }
        public int MaxValues { get; set; } = 1;
        public string? Options { get; set; }
        public bool IsActive { get; set; } = true;
    }

    [BindProperty] public TypeForm Input { get; set; } = new();
    [BindProperty] public FieldForm Field { get; set; } = new();

    public AssetType Type { get; private set; } = null!;

    /// <summary>Field being edited (from <c>?fieldId=</c>), or null for the add-field form.</summary>
    public AssetFieldDefinition? Editing { get; private set; }

    [TempData] public string? Message { get; set; }

    /// <summary>What the tag pattern would give for site code "R" and number 1, or null when empty or invalid.</summary>
    public string? PatternExample { get; private set; }

    public static IReadOnlyList<FieldKind> Kinds { get; } = Enum.GetValues<FieldKind>();

    public static string KindLabel(FieldKind k) => k switch
    {
        FieldKind.PhoneNumber => "Phone number",
        FieldKind.Imei => "IMEI",
        FieldKind.Choice => "Choice (dropdown)",
        _ => k.ToString(),
    };

    public async Task OnGetAsync(int id, int? fieldId, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Input = new TypeForm
        {
            Name = Type.Name, IsActive = Type.IsActive, UsesModel = Type.UsesModel, UsesDescription = Type.UsesDescription,
            UsesSerialNumber = Type.UsesSerialNumber, UsesCondition = Type.UsesCondition, TagPattern = Type.TagPattern,
        };
        if (fieldId is { } fid && Type.Fields.FirstOrDefault(f => f.Id == fid) is { } f)
        {
            Editing = f;
            Field = new FieldForm
            {
                Name = f.Name, Kind = f.Kind, IsRequired = f.IsRequired, IsUnique = f.IsUnique, IsSensitive = f.IsSensitive,
                MaxValues = f.MaxValues, Options = f.Options, IsActive = f.IsActive,
            };
        }
        SetExample(Type.TagPattern);
    }

    public Task<IActionResult> OnPostAsync(int id, CancellationToken ct) =>
        RunAsync(id, null, async () =>
        {
            var type = await catalog.UpdateTypeAsync(id, new TypeInput(Input.Name, (await catalog.GetTypeAsync(id, ct)).Mode,
                Input.UsesModel, Input.UsesDescription, Input.UsesSerialNumber, Input.UsesCondition, Input.TagPattern, Input.IsActive), ct);
            return $"Asset type {type.Name} saved.";
        }, ct);

    public Task<IActionResult> OnPostAddFieldAsync(int id, CancellationToken ct) =>
        RunAsync(id, null, async () =>
        {
            var field = await catalog.AddFieldAsync(id, ToInput(), ct);
            return $"Field {field.Name} added.";
        }, ct);

    public Task<IActionResult> OnPostUpdateFieldAsync(int id, int fieldId, CancellationToken ct) =>
        RunAsync(id, fieldId, async () =>
        {
            var field = await catalog.UpdateFieldAsync(fieldId, ToInput(), ct);
            return $"Field {field.Name} saved.";
        }, ct);

    public Task<IActionResult> OnPostMoveAsync(int id, int fieldId, bool up, CancellationToken ct) =>
        RunAsync(id, null, async () =>
        {
            await catalog.MoveFieldAsync(fieldId, up, ct);
            return "Field moved.";
        }, ct);

    private FieldInput ToInput() => new(Field.Name, Field.Kind, Field.IsRequired, Field.IsUnique, Field.IsSensitive,
        Field.MaxValues, Field.Options, Field.IsActive);

    private async Task<IActionResult> RunAsync(int id, int? editingFieldId, Func<Task<string>> action, CancellationToken ct)
    {
        try
        {
            Message = await action();
            return RedirectToPage(new { id });
        }
        catch (DomainException ex)
        {
            this.Show(ex);
        }
        await LoadAsync(id, ct);
        Editing = editingFieldId is { } fid ? Type.Fields.FirstOrDefault(f => f.Id == fid) : null;
        SetExample(Input.TagPattern);
        return Page();
    }

    private async Task LoadAsync(int id, CancellationToken ct) => Type = await catalog.GetTypeAsync(id, ct);

    private void SetExample(string? pattern)
    {
        try
        {
            var valid = TagPattern.Validate(pattern);
            PatternExample = valid is null ? null : TagPattern.Render(valid, "R", 1);
        }
        catch (DomainException)
        {
            PatternExample = null;
        }
    }
}
