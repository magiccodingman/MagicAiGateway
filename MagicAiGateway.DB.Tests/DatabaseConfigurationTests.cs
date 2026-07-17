using MagicAiGateway.DB.API.Configuration;
using MagicAiGateway.DB.API.Database;
using Npgsql;

namespace MagicAiGateway.DB.Tests;

public sealed class DatabaseConfigurationTests
{
    [Fact]
    public void MissingDatabaseUsesDefaultAndAllowsCreation()
    {
        var resolved = DatabaseConnectionStringFactory.Create(new DatabaseConnectionOptions
        {
            Host = "localhost",
            Username = "gateway",
            Password = "not-a-real-secret",
            Database = null
        });

        Assert.Equal(DatabaseConnectionStringFactory.DefaultDatabaseName, resolved.DatabaseName);
        Assert.True(resolved.CreateDatabaseIfMissing);
    }

    [Fact]
    public void ExplicitDatabaseMustAlreadyExist()
    {
        var resolved = DatabaseConnectionStringFactory.Create(new DatabaseConnectionOptions
        {
            Host = "localhost",
            Username = "gateway",
            Password = "not-a-real-secret",
            Database = "existing_gateway"
        });

        Assert.Equal("existing_gateway", resolved.DatabaseName);
        Assert.False(resolved.CreateDatabaseIfMissing);
    }

    [Fact]
    public void ConnectionStringWithoutDatabaseReceivesDefault()
    {
        var resolved = DatabaseConnectionStringFactory.Create(new DatabaseConnectionOptions
        {
            ConnectionString = "Host=localhost;Username=gateway;Password=secret"
        });
        var parsed = new NpgsqlConnectionStringBuilder(resolved.ConnectionString);

        Assert.Equal(DatabaseConnectionStringFactory.DefaultDatabaseName, parsed.Database);
        Assert.True(resolved.CreateDatabaseIfMissing);
    }
}
