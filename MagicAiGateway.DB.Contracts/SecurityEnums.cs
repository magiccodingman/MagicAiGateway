namespace MagicAiGateway.DB.Contracts;

/// <summary>Stable application identities used by service credentials and endpoint authorization.</summary>
public enum MagicApplication
{
    Unknown = 0,
    PrimaryApi = 1,
    Web = 2,
    Mcp = 3,
    DatabaseApi = 4
}

/// <summary>Roles shipped by the platform. Database-defined roles may be added later.</summary>
public enum BuiltInRole
{
    Administrator = 1
}

public enum SecurityPrincipalType
{
    User = 1,
    ApiKey = 2
}

public static class BuiltInRoleHierarchy
{
    public static IReadOnlySet<BuiltInRole> Expand(IEnumerable<BuiltInRole> assignedRoles)
    {
        ArgumentNullException.ThrowIfNull(assignedRoles);
        var expanded = assignedRoles.ToHashSet();

        // Administrator currently stands alone and therefore implies every built-in role.
        // Keep this explicit instead of relying on enum ordering so non-linear roles can be added safely.
        if (expanded.Contains(BuiltInRole.Administrator))
        {
            foreach (var role in Enum.GetValues<BuiltInRole>()) expanded.Add(role);
        }

        return expanded;
    }
}
