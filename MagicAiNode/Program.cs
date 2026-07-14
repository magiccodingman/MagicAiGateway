using MagicAiNode;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using SharedMagic.Configuration;
using SharedMagic.Discovery;
using SharedMagic.Routing;
using SharedMagic.Security;

var builder = WebApplication.CreateBuilder(args);

var nodeOptions = builder.Configuration.GetSection(NodeOptions.SectionName).Get<NodeOptions>() ?? new();
var securityOptions = builder.Configuration.GetSection(FabricSecurityOptions.SectionName).Get<FabricSecurityOptions>() ?? new();
var discoveryOptions = builder.Configuration.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>() ?? new();
var queueOptions = builder.Configuration.GetSection(QueueOptions.SectionName).Get<QueueOptions>() ?? new();
var stateDirectory = FabricStateFiles.ResolveDirectory(securityOptions.StateDirectory, builder.Environment.ContentRootPath);
var identity = FabricStateFiles.LoadOrCreateIdentity(stateDirectory, nodeOptions.Name, "node", Guid.Empty);
var certificateStore = new NodeCertificateStore(stateDirectory, identity);
var pairingState = new NodePairingStateStore(stateDirectory);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(https =>
    {
        https.ServerCertificateSelector = (_, _) => certificateStore.CurrentServerCertificate;
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.CheckCertificateRevocation = false;
    });
});

builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(NodeOptions.SectionName));
builder.Services.Configure<FabricSecurityOptions>(builder.Configuration.GetSection(FabricSecurityOptions.SectionName));
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(certificateStore);
builder.Services.AddSingleton(pairingState);
builder.Services.AddSingleton(queueOptions);
builder.Services.AddSingleton<BackendCatalog>();
builder.Services.AddSingleton<IRequestScheduler<BackendRouteTarget>, LeastBusyRequestScheduler<BackendRouteTarget>>();
builder.Services.AddSingleton<BackendProxyInvokerPool>();
builder.Services.AddSingleton<IAiBackendAdapter, VllmBackendAdapter>();
builder.Services.AddSingleton<IAiBackendAdapter, LlamaCppBackendAdapter>();
builder.Services.AddSingleton<IFabricPeerTrustProvider, NodePeerTrustProvider>();
builder.Services.AddHostedService<BackendMonitorService>();
builder.Services.AddHostedService<GatewayConnectionService>();
builder.Services.AddHostedService<HttpGatewayHeartbeatService>();
builder.Services.AddMagicFabricAuthentication();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpForwarder();

if (discoveryOptions.Enabled)
{
    builder.Services.AddSingleton(new MdnsAdvertisement(
        $"{nodeOptions.Name}-{identity.InstanceId:N}",
        discoveryOptions.NodeServiceType,
        checked((ushort)discoveryOptions.AdvertisedHttpsPort),
        new Dictionary<string, string>
        {
            ["name"] = nodeOptions.Name,
            ["nodeId"] = identity.InstanceId.ToString(),
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
NodeProxyEndpoint.Map(app);
app.Run();

public partial class Program;
