namespace MagicAiGateway.DB.API.Configuration;

public sealed class DatabaseApiOptions
{
    public const string SectionName = "DatabaseApi";
    public string Name { get; set; } = "MagicAiGateway.DB.API";
    public string GatewayName { get; set; } = "MagicAiGateway";
    public List<string> AdvertisedEndpoints { get; set; } = ["https://localhost:7643"];
    public List<string> StaticGateways { get; set; } = ["https://localhost:7443"];
    public int HeartbeatSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 20;
}

public sealed class DatabaseConnectionOptions
{
    public const string SectionName = "Database:Connection";
    public string? ConnectionString { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string? Database { get; set; }
    public string Username { get; set; } = "magicaigateway";
    public string? Password { get; set; }
    public string AdminDatabase { get; set; } = "postgres";
    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class DatabaseAutoDeployOptions
{
    public const string SectionName = "Database:AutoDeploy";
    public bool Enabled { get; set; }
    public string Image { get; set; } = "postgres:17-alpine";
    public string ContainerName { get; set; } = "magic-ai-gateway-postgres";
    public string VolumeName { get; set; } = "magic-ai-gateway-postgres-data";
    public int HostPort { get; set; } = 5432;
    public bool StopOnShutdown { get; set; } = true;
    public bool RemoveContainerOnShutdown { get; set; }
}

public sealed class DatabaseSchemaOptions
{
    public const string SectionName = "Database:SchemaManagement";
    public bool AutoMigrate { get; set; } = true;
}

public sealed class InitialAdministratorOptions
{
    public const string SectionName = "Security:InitialAdministrator";
    public string Username { get; set; } = "admin";
    public string? Password { get; set; } = "put_your_password_here";
}

public sealed class ApplicationSecurityOptions
{
    public const string SectionName = "Security:Applications";
    public string? ApiKeyPepper { get; set; }
    public string? BootstrapToken { get; set; }
}

public sealed class AdminRecoveryOptions
{
    public const string SectionName = "Security:AdminRecovery";
    public bool Enabled { get; set; }
    public string? OneTimeToken { get; set; }
    public string ListenUrl { get; set; } = "http://127.0.0.1:7764";
}
