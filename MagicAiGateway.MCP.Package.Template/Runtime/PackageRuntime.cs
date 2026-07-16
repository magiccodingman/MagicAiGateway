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

        _ = MonitorInstanceAsync(instanceId, instance);
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

    private static async Task MonitorInstanceAsync(Guid instanceId, PackageInstance instance)
    {
        try
        {
            await instance.Completion.ConfigureAwait(false);
        }
        catch
        {
            // The instance retains the terminal exception long enough for an active
            // receive call to report it. The monitor's job is lifecycle cleanup.
        }

        // Explicit stop/shutdown removes the entry before completing the server task.
        // If the entry still exists here, the MCP server ended on its own and this
        // monitor owns cleanup.
        if (!Instances.TryRemove(instanceId, out PackageInstance? completedInstance))
        {
            return;
        }

        try
        {
            await completedInstance.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // No native caller exists on this path to receive a status code. The
            // instance is already evicted and all cleanup has been attempted.
        }
    }
}
