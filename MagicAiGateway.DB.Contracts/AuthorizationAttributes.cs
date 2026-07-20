namespace MagicAiGateway.DB.Contracts;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireMagicApplicationAttribute : Attribute
{
    public RequireMagicApplicationAttribute(params MagicApplication[] applications)
    {
        if (applications is null || applications.Length == 0)
        {
            throw new ArgumentException("At least one application is required.", nameof(applications));
        }

        if (applications.Any(static application => application == MagicApplication.Unknown))
        {
            throw new ArgumentException("Unknown is not a valid authorized application.", nameof(applications));
        }

        Applications = applications.Distinct().ToArray();
    }

    public IReadOnlyList<MagicApplication> Applications { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireMagicRoleAttribute : Attribute
{
    public RequireMagicRoleAttribute(params BuiltInRole[] roles)
    {
        if (roles is null || roles.Length == 0)
        {
            throw new ArgumentException("At least one role is required.", nameof(roles));
        }

        Roles = roles.Distinct().ToArray();
    }

    public IReadOnlyList<BuiltInRole> Roles { get; }
}
