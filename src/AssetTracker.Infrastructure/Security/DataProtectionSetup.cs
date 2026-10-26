using AssetTracker.Core.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AssetTracker.Infrastructure.Security;

/// <summary><see cref="ISecretProtector"/> over ASP.NET Core Data Protection with a fixed purpose string.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    /// <summary>Purpose string; changing it makes every stored secret unreadable.</summary>
    public const string Purpose = "AssetTracker.Settings.Secret.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}

public static class DataProtectionSetup
{
    /// <summary>Application name shared by the web app and the worker, so both read the same key ring.</summary>
    public const string ApplicationName = "AssetTracker";

    /// <summary>Bootstrap configuration key (file only): folder holding the shared key ring.</summary>
    public const string KeyPathKey = "DataProtection:KeyPath";

    /// <summary>
    /// Data Protection for secrets stored in the database (and, in the web app, antiforgery/TempData cookies).
    /// The web app and the worker must decrypt each other's ciphertext, so both use the application name
    /// <see cref="ApplicationName"/> and persist the key ring to the same folder (<c>DataProtection:KeyPath</c>);
    /// on Windows the keys are encrypted at rest with machine-level DPAPI (both service accounts on the server can
    /// read them; a copy on another machine cannot). Production requires the key path. The Testing environment
    /// uses an in-memory key ring; Development falls back to the default per-user location.
    /// </summary>
    public static IServiceCollection AddAssetTrackerDataProtection(this IServiceCollection services,
        IConfiguration config, IHostEnvironment environment)
    {
        var dp = services.AddDataProtection().SetApplicationName(ApplicationName);
        var keyPath = config[KeyPathKey];
        if (environment.IsEnvironment("Testing"))
        {
            dp.UseEphemeralDataProtectionProvider();
        }
        else if (!string.IsNullOrWhiteSpace(keyPath))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
            if (OperatingSystem.IsWindows())
                dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
        else if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{KeyPathKey} is not configured. Set it to a folder shared by the web app and the worker " +
                "(see deploy/README.md); stored secrets cannot be decrypted otherwise.");
        }

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }
}
