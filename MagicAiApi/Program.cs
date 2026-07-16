using MagicAiApi;
using MagicAiApi.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Discovery;
using SharedMagic.Execution;
using SharedMagic.Proxy;
using SharedMagic.Routing;
using SharedMagic.Security;

var builder = WebApplication.CreateBuilder(args);

var gatewayOptions = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new();
var securityOptions = builder.Configuration.GetSection(FabricSecurityOptions.SectionName).Get<FabricSecurityOptions>() ?? new();
var discoveryOptions = builder.Configuration.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>() ?? new();
var queueOptions = builder.Configuration.GetSection(QueueOptions.SectionName).Get<QueueOptions>() ?? new();
var stateDirectory = FabricStateFiles.ResolveDirectory(securityOptions.StateDirectory, builder.Environment.ContentRootPath);
var identity = FabricStateFiles.LoadOrCreateIdentity(stateDirectory, gatewayOptions.Name, "gateway");
var certificateAuthority = new GatewayCertificateAuthority(stateDirectory, identity, securityOptions);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(https =>
    {
        https.ServerCertificateSelector = (_, _) => certificateAuthority.ServerCertificate;
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.CheckCertificateRevocation = false;
        // The fabric uses its own private CA. Accept the certificate at the TLS layer,
        // then perform full chain and peer-identity validation in FabricAuthenticationHandler.
        https.ClientCertificateValidation = (_, _, _) => true;
    });
});

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));
builder.Services.Configure<FabricSecurityOptions>(builder.Configuration.GetSection(FabricSecurityOptions.SectionName));
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton(certificateAuthority);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(queueOptions);
builder.Services.AddSingleton<GatewayPairingRegistry>();
builder.Services.AddSingleton<PairingChallengeStore>();
builder.Services.AddSingleton<GatewayNodeRegistry>();
builder.Services.AddSingleton<IFabricPeerTrustProvider, GatewayPeerTrustProvider>();
builder.Services.AddSingleton<IRequestScheduler<GatewayNodeTarget>, LeastBusyRequestScheduler<GatewayNodeTarget>>();
builder.Services.AddSingleton<IMagicToolRegistry, EmptyMagicToolRegistry>();
builder.Services.AddSingleton<GatewayProxyInvoker>();
builder.Services.AddSingleton<GatewayNodeClient>();
builder.Services.AddHostedService<NodeLeaseMonitorService>();
builder.Services.AddHostedService<StaticNodeMonitorService>();

// Magic protocol host: one public service selection becomes a server-owned execution plan.
builder.Services.AddSingleton<IManagedToolRunService, UnavailableManagedToolRunService>();
builder.Services.AddSingleton<IMagicProtocolService, ManagedToolsProtocolService>();
builder.Services.AddSingleton<IMagicProtocolServiceRegistry, MagicProtocolServiceRegistry>();
builder.Services.AddSingleton<IGatewayRunManager, GatewayRunManager>();
builder.Services.AddSingleton<IMagicExecutionPlanExecutor, MagicExecutionPlanExecutor>();
builder.Services.AddSingleton<IGatewayCallerContextResolver, DefaultGatewayCallerContextResolver>();
builder.Services.AddSingleton<IGatewayApplicationResolver, DefaultGatewayApplicationResolver>();
builder.Services.AddSingleton<IGatewayAgentResolver, DefaultGatewayAgentResolver>();
builder.Services.AddSingleton<IGatewayServiceAuthorizationService, DefaultGatewayServiceAuthorizationService>();
builder.Services.AddSingleton<MagicProtocolHost>();

// Fabric peers and ordinary clients intentionally use separate authentication domains.
builder.Services.AddMagicFabricAuthentication();
builder.Services.AddMagicGatewayClientSecurity(builder.Configuration);

builder.Services.AddSignalR(options => options.StatefulReconnectBufferSize = 100_000);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpForwarder();

if (discoveryOptions.Enabled)
{
    builder.Services.AddSingleton(new MdnsAdvertisement(
        $"{gatewayOptions.Name}-{identity.InstanceId:N}",
        discoveryOptions.GatewayServiceType,
        checked((ushort)discoveryOptions.AdvertisedHttpsPort),
        new Dictionary<string, string>
        {
            ["name"] = gatewayOptions.Name,
            ["gatewayId"] = identity.InstanceId.ToString(),
            ["clusterId"] = identity.ClusterId.ToString(),
            ["protocolVersion"] = "1",
            ["tls"] = "true"
        }));
    builder.Services.AddHostedService<MdnsAdvertiserHostedService>();
}

var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<GatewayFabricHub>("/fabric/v1/hub", options => options.AllowStatefulReconnects = true)
    .RequireAuthorization(FabricAuthenticationDefaults.Policy);
GatewayProxyEndpoint.Map(app);
app.Run();

public partial class Program;
