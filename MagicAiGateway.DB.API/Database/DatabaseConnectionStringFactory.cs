using MagicAiGateway.DB.API.Configuration;
using Npgsql;

namespace MagicAiGateway.DB.API.Database;

public sealed record ResolvedDatabaseConnection(
    string ConnectionString,
    string DatabaseName,
    string AdminDatabase,
    bool CreateDatabaseIfMissing);

public static class DatabaseConnectionStringFactory
{
    public const string DefaultDatabaseName = "magic_ai_gateway";

    public static ResolvedDatabaseConnection Create(DatabaseConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            var configured = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            var suppliedDatabase = !string.IsNullOrWhiteSpace(configured.Database);
            configured.Database = suppliedDatabase ? configured.Database : DefaultDatabaseName;
            configured.CommandTimeout = options.CommandTimeoutSeconds;
            return new ResolvedDatabaseConnection(
                configured.ConnectionString,
                configured.Database!,
                options.AdminDatabase,
                CreateDatabaseIfMissing: !suppliedDatabase);
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new InvalidOperationException("Database:Connection:Username is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                "Database:Connection:Password is required. Supply it through environment variables, user secrets, or another protected configuration provider.");
        }

        var databaseWasSupplied = !string.IsNullOrWhiteSpace(options.Database);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = databaseWasSupplied ? options.Database : DefaultDatabaseName,
            Username = options.Username,
            Password = options.Password,
            CommandTimeout = options.CommandTimeoutSeconds,
            Pooling = true,
            IncludeErrorDetail = false
        };

        return new ResolvedDatabaseConnection(
            builder.ConnectionString,
            builder.Database!,
            options.AdminDatabase,
            CreateDatabaseIfMissing: !databaseWasSupplied);
    }
}
