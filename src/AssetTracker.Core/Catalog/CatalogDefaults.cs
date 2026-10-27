using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Catalog;

/// <summary>
/// The asset types and fields every installation starts with (see <see cref="BuiltIn"/>). SQL Server gets them from
/// the migration that introduced the catalog (keep the two in step); databases created straight from the model
/// (SQLite tests and demo mode, SQL Server in CI) get them from <see cref="EnsureAsync"/>. They are ordinary rows
/// afterwards, so this never touches a database that already has asset types.
/// </summary>
public static class CatalogDefaults
{
    /// <summary>Inserts the default types and fields when the database has no asset types. Returns whether it did.</summary>
    public static async Task<bool> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.AssetTypes.AnyAsync(ct)) return false;

        // Rows are inserted one at a time and take the identity values 1, 2, 3 ... of an empty table (explicit ids would
        // need IDENTITY_INSERT on SQL Server), which is what the BuiltIn constants promise.
        async Task AddType(int expectedId, string name, InventoryMode mode, bool standardFields, WorkspotRole role = WorkspotRole.None)
        {
            var type = new AssetType
            {
                Name = name, Mode = mode, WorkspotRole = role,
                UsesModel = standardFields, UsesDescription = standardFields, UsesSerialNumber = standardFields,
                UsesCondition = standardFields,
            };
            db.AssetTypes.Add(type);
            await db.SaveChangesAsync(ct);
            if (type.Id != expectedId)
                throw new InvalidOperationException($"Default type {name} got id {type.Id}, expected {expectedId}: the table was not empty.");
        }

        async Task AddField(int expectedId, int typeId, string name, FieldKind kind, int order, bool required = false,
            bool unique = false, bool sensitive = false, int max = 1, string? options = null)
        {
            var field = new AssetFieldDefinition
            {
                TypeId = typeId, Name = name, Kind = kind, SortOrder = order, IsRequired = required,
                IsUnique = unique, IsSensitive = sensitive, MaxValues = max, Options = options,
            };
            db.FieldDefinitions.Add(field);
            await db.SaveChangesAsync(ct);
            if (field.Id != expectedId)
                throw new InvalidOperationException($"Default field {name} got id {field.Id}, expected {expectedId}: the table was not empty.");
        }

        await AddType(BuiltIn.Desktop, "Desktop", InventoryMode.Tagged, true, WorkspotRole.Desktop);
        await AddType(BuiltIn.Laptop, "Laptop", InventoryMode.Tagged, true);
        await AddType(BuiltIn.Phone, "Phone", InventoryMode.Tagged, true);
        await AddType(BuiltIn.PhoneNumber, "Phone number", InventoryMode.Tagged, false);
        await AddType(BuiltIn.Peripheral, "Peripheral", InventoryMode.Pooled, false);
        await AddType(BuiltIn.Webcam, "Webcam", InventoryMode.Pooled, false);
        await AddType(BuiltIn.Headset, "Headset", InventoryMode.Pooled, false);
        await AddType(BuiltIn.Screen, "Screen", InventoryMode.Tagged, true, WorkspotRole.Screen);

        // A dual-SIM phone has two IMEIs.
        await AddField(BuiltIn.PhoneImeiField, BuiltIn.Phone, "IMEI", FieldKind.Imei, 10, unique: true, max: 2);
        await AddField(BuiltIn.PhonePhoneNumberField, BuiltIn.Phone, "Phone number", FieldKind.PhoneNumber, 20, unique: true);
        await AddField(BuiltIn.LinePhoneNumberField, BuiltIn.PhoneNumber, "Phone number", FieldKind.PhoneNumber, 10,
            required: true, unique: true);
        await AddField(BuiltIn.LineProviderField, BuiltIn.PhoneNumber, "Provider", FieldKind.Text, 20);
        await AddField(BuiltIn.LineSimTypeField, BuiltIn.PhoneNumber, "SIM type", FieldKind.Choice, 30, options: "Physical SIM\neSIM");
        await AddField(BuiltIn.LineSimStatusField, BuiltIn.PhoneNumber, "SIM status", FieldKind.Choice, 40,
            options: "Active\nInactive\nSuspended\nBlocked\nLost or stolen");
        await AddField(BuiltIn.LineSimNumberField, BuiltIn.PhoneNumber, "SIM number", FieldKind.Text, 50, unique: true);
        await AddField(BuiltIn.LinePucField, BuiltIn.PhoneNumber, "PUC", FieldKind.Text, 60, sensitive: true);
        return true;
    }
}
