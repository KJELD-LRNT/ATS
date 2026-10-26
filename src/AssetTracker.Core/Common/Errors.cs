namespace AssetTracker.Core.Common;

/// <summary>Business rule violation that should be shown to the user (HTTP 400/409).</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A concurrent or timeline conflict (HTTP 409).</summary>
public class ConflictException(string code, string message) : DomainException(code, message);

/// <summary>The current principal may not perform this action or see this entity (HTTP 403/404).</summary>
public class AccessDeniedException(string message = "Access denied.") : Exception(message);
