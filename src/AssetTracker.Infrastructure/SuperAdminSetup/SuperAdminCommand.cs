using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Infrastructure.SuperAdminSetup;

/// <summary>Who made a setup request: the owner of the request file, never anything written inside it.</summary>
public interface IRequestFileOwner
{
    string GetOwner(string path);
}

/// <summary>Production (Windows): the file's ACL owner as <c>DOMAIN\user</c> (the SID when it cannot be translated).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRequestFileOwner : IRequestFileOwner
{
    public string GetOwner(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        try
        {
            return security.GetOwner(typeof(NTAccount))?.Value ?? throw new InvalidOperationException("The request file has no owner.");
        }
        catch (IdentityNotMappedException)
        {
            return security.GetOwner(typeof(SecurityIdentifier))?.Value ?? throw new InvalidOperationException("The request file has no owner.");
        }
    }
}

/// <summary>
/// TEST SEAM / DEVELOPMENT ONLY: the non-Windows fallback. .NET has no API for a file's owner outside Windows, so this
/// asks <c>stat</c> (GNU <c>-c %U</c>, else BSD <c>-f %Su</c>). Production runs on Windows and uses
/// <see cref="WindowsRequestFileOwner"/>; tests replace <see cref="IRequestFileOwner"/> with a fake.
/// </summary>
public sealed class UnixRequestFileOwner : IRequestFileOwner
{
    public string GetOwner(string path) =>
        Stat(["-c", "%U", path]) ?? Stat(["-f", "%Su", path])
        ?? throw new InvalidOperationException("Could not read the owner of the request file.");

    private static string? Stat(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The setup command's <see cref="ICurrentUser"/>: the Windows account that owns the request file, so the audit log
/// names the IT admin who ran <c>Set-SuperAdmin.ps1</c> instead of the gMSA or <c>system:worker</c>. No roles.
/// </summary>
public sealed class SetupRequester : ICurrentUser
{
    public string Name { get; set; } = "system:superadmin-setup";
    public string? Sid => null;
    public AppRoles Roles => AppRoles.None;
}

public static class SuperAdminCommandServiceCollectionExtensions
{
    /// <summary>Registers the worker's <c>superadmin</c> command (instead of the background-job identity).</summary>
    public static IServiceCollection AddSuperAdminSetupCommand(this IServiceCollection services)
    {
        services.AddScoped<SetupRequester>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<SetupRequester>());
        services.AddScoped<SuperAdminSetupService>();
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IRequestFileOwner, WindowsRequestFileOwner>();
        else
            services.AddSingleton<IRequestFileOwner, UnixRequestFileOwner>();
        services.AddSingleton<SuperAdminCommand>();
        return services;
    }
}

/// <summary>Console streams of the command (real console in the worker, strings in tests).</summary>
public sealed record CommandConsole(TextReader In, bool InputRedirected, TextWriter Out, TextWriter Error)
{
    /// <summary>The process console. Redirected input is read as UTF-8 (the script writes the password as UTF-8 bytes),
    /// whatever the console code page is.</summary>
    public static CommandConsole System() => Console.IsInputRedirected
        ? new(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)), true, Console.Out, Console.Error)
        : new(Console.In, false, Console.Out, Console.Error);
}

/// <summary>
/// <c>AssetTracker.Worker.exe superadmin &lt;verb&gt;</c>. The worker branches here after <c>Build()</c> and never starts
/// its background jobs.
/// <list type="bullet">
/// <item><c>hash</c>: reads a password from standard input (one line), or asks twice without echo when run in a
/// console; refuses passwords shorter than 16 characters and the demo password; prints the hash. No database.</item>
/// <item><c>apply &lt;file&gt;</c>: reads the request file (<c>{"action": "...", "hash": "..."}</c>), takes the requester
/// from the file's owner, applies the action as that requester (audited), writes <c>&lt;name&gt;.result.json</c> next to
/// it and deletes the request.</item>
/// </list>
/// Exit codes: 0 done, 1 failed, 2 refused password or wrong usage.
/// </summary>
public sealed class SuperAdminCommand(IServiceScopeFactory scopes, IRequestFileOwner owners, IClock clock)
{
    public const int Ok = 0, Failed = 1, Usage = 2;
    private const int MaxRequestBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record Request(string? Action, string? Hash);

    public sealed record AccountState(bool Exists, bool Enabled, bool HasPassword, bool Locked, DateTimeOffset? LockedUntil,
        int FailedCount, DateTimeOffset? PasswordChangedAt, DateTimeOffset? LastSignInAt, string? LastSignInIp);

    public sealed record Result(bool Ok, string? Action, string? Requester, string Message, AccountState? Account,
        DateTimeOffset CompletedAt);

    public static string ResultPathFor(string requestPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!,
            Path.GetFileNameWithoutExtension(requestPath) + ".result.json");

    public async Task<int> RunAsync(IReadOnlyList<string> args, CommandConsole console, CancellationToken ct = default)
    {
        switch (args.Count > 0 ? args[0].ToLowerInvariant() : "")
        {
            case "hash" when args.Count == 1:
                return Hash(console);
            case "apply" when args.Count == 2:
                return await ApplyAsync(args[1], console, ct);
            default:
                await console.Error.WriteLineAsync(
                    "Usage: AssetTracker.Worker.exe superadmin hash            (password on standard input, or asked twice)\n" +
                    "       AssetTracker.Worker.exe superadmin apply <file>    (request file written by deploy\\Set-SuperAdmin.ps1)");
                return Usage;
        }
    }

    private static int Hash(CommandConsole console)
    {
        string? password;
        if (console.InputRedirected)
        {
            password = console.In.ReadLine();
        }
        else
        {
            password = ReadSecret("New super admin password: ", console);
            var again = ReadSecret("Type it again: ", console);
            if (password != again)
            {
                console.Error.WriteLine("The passwords do not match.");
                return Usage;
            }
        }
        if (SuperAdmin.PasswordProblem(password) is { } problem)
        {
            console.Error.WriteLine(problem);
            return Usage;
        }
        console.Out.WriteLine(SuperAdmin.HashPassword(password!));
        return Ok;
    }

    /// <summary>Reads a line from the console without echoing it.</summary>
    private static string ReadSecret(string prompt, CommandConsole console)
    {
        console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        console.Error.WriteLine();
        return sb.ToString();
    }

    private async Task<int> ApplyAsync(string file, CommandConsole console, CancellationToken ct)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path))
        {
            await console.Error.WriteLineAsync($"Request file not found: {path}");
            return Failed;
        }
        var resultPath = ResultPathFor(path);
        string? requester = null;
        string? actionName = null;
        Result result;
        try
        {
            requester = owners.GetOwner(path);
            if (new FileInfo(path).Length > MaxRequestBytes) throw new DomainException("superadmin.request", "The request file is too large.");
            var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(path, ct), Json)
                          ?? throw new DomainException("superadmin.request", "The request file is empty.");
            actionName = request.Action;
            if (request.Action is null || !SuperAdminSetupActions.ByName.TryGetValue(request.Action, out var action))
                throw new DomainException("superadmin.request",
                    $"Unknown action '{request.Action}'. Use one of: {string.Join(", ", SuperAdminSetupActions.ByName.Keys)}.");

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<SetupRequester>().Name = requester;
            var status = await scope.ServiceProvider.GetRequiredService<SuperAdminSetupService>()
                .ApplyAsync(action, request.Hash, ct);
            result = new Result(true, SuperAdminSetupActions.Name(action), requester, Describe(action, status), State(status),
                clock.UtcNow);
        }
        catch (Exception ex) when (ex is DomainException or JsonException)
        {
            result = new Result(false, actionName, requester, ex.Message, null, clock.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new Result(false, actionName, requester, $"The command failed: {ex.GetType().Name}: {ex.Message}", null,
                clock.UtcNow);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { /* reported below through the result; the script removes the folder's files */ }
            catch (UnauthorizedAccessException) { }
        }

        var tmp = resultPath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(result, Json), ct);
        File.Move(tmp, resultPath, overwrite: true);
        await (result.Ok ? console.Out : console.Error).WriteLineAsync(result.Message);
        return result.Ok ? Ok : Failed;
    }

    private AccountState State(SuperAdminStatus s) => new(s.Exists, s.IsEnabled, s.HasPassword, s.IsLockedAt(clock.UtcNow),
        s.LockedUntil, s.FailedCount, s.PasswordChangedAt, s.LastSignInAt, s.LastSignInIp);

    private string Describe(SuperAdminSetupAction action, SuperAdminStatus s) => action switch
    {
        SuperAdminSetupAction.SetPassword or SuperAdminSetupAction.GeneratePassword =>
            "The new password is set and the account is on. Any open super admin session has ended.",
        SuperAdminSetupAction.Unlock => "The account is unlocked.",
        SuperAdminSetupAction.Disable => "The account is off. Sign-in is refused until a new password is set.",
        _ => !s.Exists ? "The super admin account has not been set up yet (no password)."
            : $"The account is {(s.IsEnabled ? "on" : "off")}" +
              $"{(s.IsLockedAt(clock.UtcNow) ? $", locked until {s.LockedUntil:yyyy-MM-dd HH:mm} UTC" : ", not locked")}." +
              $" Password set: {s.PasswordChangedAt?.ToString("yyyy-MM-dd HH:mm") ?? "never"} UTC." +
              $" Last sign-in: {(s.LastSignInAt is { } at ? $"{at:yyyy-MM-dd HH:mm} UTC from {s.LastSignInIp}" : "never")}.",
    };
}
