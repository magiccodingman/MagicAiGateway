using Docker.DotNet;
using Docker.DotNet.Models;
using MagicAiGateway.DB.API.Configuration;
using Microsoft.Extensions.Options;

namespace MagicAiGateway.DB.API.Database;

public sealed record PostgresProvisioningResult(bool Managed, string? ContainerId);

public interface IPostgresProvisioner
{
    Task<PostgresProvisioningResult> StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class PostgresProvisioner(
    IOptions<DatabaseAutoDeployOptions> autoDeployOptions,
    IOptions<DatabaseConnectionOptions> connectionOptions,
    ILogger<PostgresProvisioner> logger) : IPostgresProvisioner
{
    private const string ManagedLabel = "com.magicaigateway.managed";
    private const string ServiceLabel = "com.magicaigateway.service";
    private DockerClient? _client;
    private string? _ownedContainerId;

    public async Task<PostgresProvisioningResult> StartAsync(CancellationToken cancellationToken)
    {
        var options = autoDeployOptions.Value;
        if (!options.Enabled) return new PostgresProvisioningResult(false, null);

        var database = connectionOptions.Value;
        if (string.IsNullOrWhiteSpace(database.Password))
        {
            throw new InvalidOperationException(
                "Docker PostgreSQL deployment requires Database:Connection:Password. Keep it outside source control.");
        }

        _client = new DockerClientConfiguration().CreateClient();
        try
        {
            await _client.System.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Database auto-deployment is enabled, but the Docker daemon is unavailable. Disable Database:AutoDeploy:Enabled or grant this process Docker access.",
                exception);
        }

        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, cancellationToken).ConfigureAwait(false);
        var existing = containers.FirstOrDefault(container =>
            container.Names.Any(name => string.Equals(name.TrimStart('/'), options.ContainerName, StringComparison.Ordinal)));

        if (existing is not null)
        {
            if (!existing.Labels.TryGetValue(ManagedLabel, out var managed) || managed != "true" ||
                !existing.Labels.TryGetValue(ServiceLabel, out var service) || service != "database")
            {
                throw new InvalidOperationException(
                    $"A Docker container named '{options.ContainerName}' already exists but is not owned by MagicAiGateway.");
            }

            _ownedContainerId = existing.ID;
            if (!string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                await _client.Containers.StartContainerAsync(
                    existing.ID,
                    new ContainerStartParameters(),
                    cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Using managed PostgreSQL container {ContainerId}.", existing.ID);
            return new PostgresProvisioningResult(true, existing.ID);
        }

        var (image, tag) = SplitImage(options.Image);
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image, Tag = tag },
            null,
            new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message.ErrorMessage))
                {
                    logger.LogWarning("Docker image pull reported: {Error}", message.ErrorMessage);
                }
            }),
            cancellationToken).ConfigureAwait(false);

        var container = await _client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Name = options.ContainerName,
                Image = options.Image,
                Env =
                [
                    $"POSTGRES_USER={database.Username}",
                    $"POSTGRES_PASSWORD={database.Password}",
                    $"POSTGRES_DB={DatabaseConnectionStringFactory.DefaultDatabaseName}"
                ],
                Labels = new Dictionary<string, string>
                {
                    [ManagedLabel] = "true",
                    [ServiceLabel] = "database"
                },
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    ["5432/tcp"] = default
                },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["5432/tcp"] = [new PortBinding { HostPort = options.HostPort.ToString() }]
                    },
                    Mounts =
                    [
                        new Mount
                        {
                            Type = "volume",
                            Source = options.VolumeName,
                            Target = "/var/lib/postgresql/data"
                        }
                    ],
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
                }
            },
            cancellationToken).ConfigureAwait(false);

        _ownedContainerId = container.ID;
        var started = await _client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters(),
            cancellationToken).ConfigureAwait(false);
        if (!started) throw new InvalidOperationException("Docker created PostgreSQL but did not start the container.");

        logger.LogInformation(
            "Created managed PostgreSQL container {ContainerId} with persistent volume {VolumeName}.",
            container.ID,
            options.VolumeName);
        return new PostgresProvisioningResult(true, container.ID);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client is null || string.IsNullOrWhiteSpace(_ownedContainerId)) return;
        var options = autoDeployOptions.Value;
        if (!options.StopOnShutdown) return;

        try
        {
            await _client.Containers.StopContainerAsync(
                _ownedContainerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 20 },
                cancellationToken).ConfigureAwait(false);

            if (options.RemoveContainerOnShutdown)
            {
                await _client.Containers.RemoveContainerAsync(
                    _ownedContainerId,
                    new ContainerRemoveParameters { Force = false, RemoveVolumes = false },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to stop managed PostgreSQL container {ContainerId} cleanly.", _ownedContainerId);
        }
    }

    private static (string Image, string Tag) SplitImage(string configured)
    {
        var slash = configured.LastIndexOf('/');
        var colon = configured.LastIndexOf(':');
        return colon > slash
            ? (configured[..colon], configured[(colon + 1)..])
            : (configured, "latest");
    }
}
