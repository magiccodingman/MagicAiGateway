using MagicAiGateway.DB.API.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MagicAiGateway.DB.API.Database;

public sealed class DatabaseStartupService(
    IPostgresProvisioner provisioner,
    IDbContextFactory<MagicAiGateway.DB.MagicAiGatewayDbContext> contextFactory,
    ResolvedDatabaseConnection connection,
    IOptions<DatabaseSchemaOptions> schemaOptions,
    Security.SecurityBootstrapper securityBootstrapper,
    DatabaseReadinessState readiness,
    ILogger<DatabaseStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await provisioner.StartAsync(cancellationToken).ConfigureAwait(false);
            await WaitForPostgresAsync(cancellationToken).ConfigureAwait(false);

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var creator = context.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!connection.CreateDatabaseIfMissing)
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL database '{connection.DatabaseName}' does not exist. Because a database name was explicitly configured, MagicAiGateway will not create it automatically.");
                }

                logger.LogInformation("Creating PostgreSQL database {DatabaseName}.", connection.DatabaseName);
                await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
            }

            if (schemaOptions.Value.AutoMigrate)
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            await securityBootstrapper.InitializeAsync(cancellationToken).ConfigureAwait(false);
            readiness.Ready();
            logger.LogInformation("MagicAiGateway database initialization completed.");
        }
        catch (Exception exception)
        {
            readiness.Failed(exception);
            logger.LogCritical(exception, "MagicAiGateway database initialization failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => provisioner.StopAsync(cancellationToken);

    private async Task WaitForPostgresAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
                {
                    Database = connection.AdminDatabase,
                    Timeout = 5,
                    CommandTimeout = 5
                };
                await using var probe = new NpgsqlConnection(builder.ConnectionString);
                await probe.OpenAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("PostgreSQL did not become reachable during startup.", lastFailure);
    }
}
