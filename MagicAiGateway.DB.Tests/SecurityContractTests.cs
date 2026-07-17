using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Tests;

public sealed class SecurityContractTests
{
    [Fact]
    public void AdministratorHierarchyExpandsExplicitly()
    {
        var roles = BuiltInRoleHierarchy.Expand([BuiltInRole.Administrator]);

        Assert.Contains(BuiltInRole.Administrator, roles);
        Assert.Equal(Enum.GetValues<BuiltInRole>().Length, roles.Count);
    }

    [Fact]
    public void ApplicationAttributeRejectsUnknown()
    {
        Assert.Throws<ArgumentException>(() =>
            new RequireMagicApplicationAttribute(MagicApplication.Unknown));
    }

    [Fact]
    public void ApplicationAttributeUsesOrSetWithoutDuplicates()
    {
        var attribute = new RequireMagicApplicationAttribute(
            MagicApplication.Web,
            MagicApplication.Mcp,
            MagicApplication.Web);

        Assert.Equal([MagicApplication.Web, MagicApplication.Mcp], attribute.Applications);
    }
}
