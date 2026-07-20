using MagicAiGateway.DB.API.Authorization;
using MagicAiGateway.DB.API.Configuration;
using MagicAiGateway.DB.API.Database;
using MagicAiGateway.DB.API.Fabric;
using MagicAiGateway.DB.API.Security;
using MagicAiGateway.DB.Contracts;
using MagicAiGateway.DB.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Security;

var builder = WebApplication.CreateBuilder(args);

var databaseApiOptions = builder.Configuration.GetSection(DatabaseApiOptions.SectionName).Get<DatabaseApiOptions>() ?? new();
var securityOptions = builder.Configuration.GetSection(FabricSecurityOptions.SectionName).Get<FabricSecurityOptions>() ?? new();
var connectionOptions = builder.Configuration.GetSection(DatabaseConnectionOptions.SectionName).Get<DatabaseConnectionOptions>() ?? new();
var resolvedConnection = DatabaseConnectionStringFactory.Create(connectionOptions);
var stateDirectory = FabricStateFiles.ResolveDirectory(securityOptions.StateDirectory, builder.Environment.ContentRootPath);
var identity = FabricStateFiles.LoadOrCreateIdentity(
    stateDirectory,
    databaseApiOptions.Name,
    "database-api",
    Guid.Empty);
var certificateStore = new NodeCertificateStore(stateDirectory, identity);
var pairingState = new DatabaseGatewayPairingStateStore(stateDirectory);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(https =>
    {
        https.ServerCertificateSelector = (_, _) => certificateStore.CurrentServerCertificate;
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.CheckCertificateRevocation = false;
        https.ClientCertificateValidation = (_, _, _) => true;
    });
});

builder.Services.Configure<DatabaseApiOptions>(builder.Configuration.GetSection(DatabaseApiOptions.SectionName));
builder.Services.Configure<DatabaseConnectionOptions>(builder.Configuration.GetSection(DatabaseConnectionOptions.SectionName));
builder.Services.Configure<DatabaseAutoDeployOptions>(builder.Configuration.GetSection(DatabaseAutoDeployOptions.SectionName));
builder.Services.Configure<DatabaseSchemaOptions>(builder.Configuration.GetSection(DatabaseSchemaOptions.SectionName));
builder.Services.Configure<InitialAdministratorOptions>(builder.Configuration.GetSection(InitialAdministratorOptions.SectionName));
builder.Services.Configure<ApplicationSecurityOptions>(builder.Configuration.GetSection(ApplicationSecurityOptions.SectionName));
builder.Services.Configure<AdminRecoveryOptions>(builder.Configuration.GetSection(AdminRecoveryOptions.SectionName));
builder.Services.Configure<FabricSecurityOptions>(builder.Configuration.GetSection(FabricSecurityOptions.SectionName));
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
builder.Services.AddSingleton(resolvedConnection);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(certificateStore);
builder.Services.AddSingleton(pairingState);
builder.Services.AddSingleton<DatabaseReadinessState>();
builder.Services.AddSingleton<IFabricPeerTrustProvider, DatabaseGatewayPeerTrustProvider>();

builder.Services.AddDbContextFactory<MagicAiGateway.DB.MagicAiGatewayDbContext>(options =>
    options.UseNpgsql(resolvedConnection.ConnectionString, npgsql =>
    {
        npgsql.MigrationsAssembly(typeof(MagicAiGateway.DB.MagicAiGatewayDbContext).Assembly.FullName);
        npgsql.UseAdminDatabase(resolvedConnection.AdminDatabase);
        npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
    }));

builder.Services.AddSingleton<IPostgresProvisioner, PostgresProvisioner>();
builder.Services.AddSingleton<IPasswordHasher<UserEntity>, PasswordHasher<UserEntity>>();
builder.Services.AddSingleton<ApiKeyPepperProvider>();
builder.Services.AddSingleton<ApiKeySecretService>();
builder.Services.AddSingleton<SecurityBootstrapper>();
builder.Services.AddSingleton<ApplicationSecurityService>();
builder.Services.AddSingleton<IApplicationAuthorizationEvaluator>(provider =>
    provider.GetRequiredService<ApplicationSecurityService>());
builder.Services.AddSingleton<UserSecurityService>();
builder.Services.AddSingleton<AdminRecoveryGate>();
builder.Services.AddSingleton<IHostedService, DatabaseStartupService>();
builder.Services.AddHostedService<DatabaseFabricRegistrationService>();

builder.Services.AddMagicFabricAuthentication();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<MagicApplicationAuthorizationMiddleware>();
app.MapControllers();
app.Run();

public partial class Program;
