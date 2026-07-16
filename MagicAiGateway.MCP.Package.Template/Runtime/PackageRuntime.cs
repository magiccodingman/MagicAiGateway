using System.Collections.Concurrent;

namespace MagicAiGateway.MCP.Package.Template.Runtime;

internal static class PackageRuntime
{
    private static readonly ConcurrentDictionary<Guid, PackageInstance> Instances = new();

    public static async Task<Guid> StartInstanceAsync(ReadOnlyMemory<byte> configurationJson)
    {
        Guid instanceId;
        do
        {
            instanceId = Guid.NewGuid();
        }
        while (Instances.ContainsKey(instanceId));

        PackageInstance instance = await PackageInstance
            .StartAsync(instanceId, configurationJson.ToArray())
            .ConfigureAwait(false);

        if (!Instances.TryAdd(instanceId, instance))
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Could not register the newly started package instance.");
        }

        return instanceId;
    }

    public static bool TryGetInstance(Guid instanceId, out PackageInstance? instance) =>
        Instances.TryGetValue(instanceId, out instance);

    public static Guid[] GetInstanceIds() =>
        Instances.Keys.OrderBy(static id => id).ToArray();

    public static async Task<bool> StopInstanceAsync(Guid instanceId)
    {
        if (!Instances.TryRemove(instanceId, out PackageInstance? instance))
        {
            return false;
        }

        await instance.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public static async Task ShutdownAsync()
    {
        List<Exception>? failures = null;

        foreach ((Guid instanceId, PackageInstance instance) in Instances.ToArray())
        {
            if (!Instances.TryRemove(instanceId, out _))
            {
                continue;
            }

            try
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more package instances failed to stop.", failures);
        }
    }
}
