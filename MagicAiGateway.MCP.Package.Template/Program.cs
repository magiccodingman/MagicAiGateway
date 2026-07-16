using MagicAiGateway.MCP.Package.Template.MCP;
using MagicAiGateway.MCP.Package.Template.Runtime;
using MagicAiGateway.MCP.Package.Template.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MagicAiGateway.MCP.Package.Template;

/// <summary>
/// Defines the services and MCP features created for every package instance.
/// This is the primary application-composition file package developers should edit.
/// </summary>
public static class Program
{
    internal static IHost BuildHost(PackageInstanceContext instanceContext)
    {
        ArgumentNullException.ThrowIfNull(instanceContext);

        // Use an empty Generic Host so the package does not accidentally inherit the
        // parent process's appsettings files, environment-variable conventions, or
        // console logging. Everything this instance needs is registered below.
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = PackageManifest.Name,
                EnvironmentName = Environments.Production,
                ContentRootPath = AppContext.BaseDirectory
            });

        // A package is loaded inside another process. Never use stdout/stderr as an
        // implicit protocol or logging channel. Add an explicit internal logger if
        // your package needs one.
        builder.Logging.ClearProviders();

        // The host may provide package-defined settings as UTF-8 JSON when it starts
        // an instance. Those settings are available through normal IConfiguration DI.
        if (!instanceContext.ConfigurationJson.IsEmpty)
        {
            builder.Configuration.AddJsonStream(
                new MemoryStream(instanceContext.ConfigurationJson.ToArray(), writable: false));
        }

        // Register normal C# application services here.
        builder.Services.AddSingleton(instanceContext);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ExampleState>();
        builder.Services.AddHostedService<ExampleBackgroundService>();

        // Generic tool registration is intentional: it preserves the required members
        // when this project is published as a NativeAOT shared library.
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = PackageManifest.Name,
                    Version = PackageManifest.Version
                };
                options.ServerInstructions =
                    "This package demonstrates dependency-injected MCP tools and per-instance background services.";
            })
            .WithTools<ExampleTools>();

        return builder.Build();
    }
}
