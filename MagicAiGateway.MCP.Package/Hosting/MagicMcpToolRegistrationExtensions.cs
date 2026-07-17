using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MagicAiGateway.MCP.Package;

internal static class MagicMcpToolRegistrationExtensions
{
    public static IMcpServerBuilder WithMagicMcpToolController<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods |
            DynamicallyAccessedMemberTypes.PublicConstructors)] TController>(
        this IMcpServerBuilder builder)
        where TController : MagicMcpToolController
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (MethodInfo method in typeof(TController).GetMethods(
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.Static |
                     BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            if (method.IsStatic)
            {
                throw new MagicMcpPackageConfigurationException(
                    $"MCP tool method '{typeof(TController).FullName}.{method.Name}' must be an instance method. " +
                    "Magic MCP tool controllers are activated once per invocation.");
            }

            builder.Services.AddSingleton((Func<IServiceProvider, McpServerTool>)(rootServices =>
                McpServerTool.Create(
                    method,
                    request =>
                    {
                        IServiceProvider requestServices = request.Services ?? rootServices;
                        TController controller =
                            ActivatorUtilities.CreateInstance<TController>(requestServices);

                        controller.Initialize(
                            requestServices.GetRequiredService<MagicMcpPackageInstanceContext>());

                        return controller;
                    },
                    new McpServerToolCreateOptions
                    {
                        Services = rootServices
                    })));
        }

        return builder;
    }
}
