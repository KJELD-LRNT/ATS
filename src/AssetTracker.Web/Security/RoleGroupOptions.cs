namespace AssetTracker.Web.Security;

/// <summary>
/// Maps AD security groups (by SID) to application roles. Values come from Stage 0; none are defaulted.
/// </summary>
public class RoleGroupOptions
{
    public List<string> ItGroupSids { get; set; } = [];
    public List<string> ManagerGroupSids { get; set; } = [];
    public List<string> AdminGroupSids { get; set; } = [];
}
