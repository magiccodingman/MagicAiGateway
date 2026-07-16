using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MagicAiGateway.Protocol;

namespace SharedMagic.Execution;

public interface IGatewayRunOutput
{
    bool HasStarted { get; }

    ValueTask PublishAsync(
        MagicChatStreamUpdate update,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        MagicChatCompletionResponse response,
        CancellationToken cancellationToken);

    ValueTask FailAsync(
        MagicRunError error,
        MagicRunMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed record MagicServicePlanningContext(
    GatewayRunContext Run,
    IGatewayRunOutput Output);

public interface IMagicProtocolService
{
    MagicServiceDescriptor Descriptor { get; }
    Type OptionsType { get; }
    object DeserializeOptions(JsonElement? options);

    ValueTask ContributePlanAsync(
        MagicExecutionPlanBuilder builder,
        MagicServicePlanningContext context,
        object options,
        CancellationToken cancellationToken);
}

public abstract class MagicProtocolService<TOptions> : IMagicProtocolService
    where TOptions : class, new()
{
    protected MagicProtocolService(MagicServiceDescriptor descriptor)
    {
        Descriptor = descriptor with
        {
            OptionsSchema = descriptor.OptionsSchema.Count == 0
                ? MagicServiceSchemaBuilder.Build(typeof(TOptions))
                : descriptor.OptionsSchema
        };
    }

    public MagicServiceDescriptor Descriptor { get; }
    public Type OptionsType => typeof(TOptions);

    public object DeserializeOptions(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new TOptions();
        }

        if (options.Value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Service '{Descriptor.Name}' options must be a JSON object.");
        }

        return options.Value.Deserialize<TOptions>(MagicProtocolJson.Options)
               ?? throw new JsonException($"Service '{Descriptor.Name}' options could not be deserialized.");
    }

    public ValueTask ContributePlanAsync(
        MagicExecutionPlanBuilder builder,
        MagicServicePlanningContext context,
        object options,
        CancellationToken cancellationToken) =>
        ContributePlanAsync(builder, context, (TOptions)options, cancellationToken);

    protected abstract ValueTask ContributePlanAsync(
        MagicExecutionPlanBuilder builder,
        MagicServicePlanningContext context,
        TOptions options,
        CancellationToken cancellationToken);
}

public interface IMagicExecutionPlanContributor
{
    ValueTask ContributeAsync(
        MagicExecutionPlanBuilder builder,
        MagicServicePlanningContext context,
        CancellationToken cancellationToken);
}

public interface IMagicProtocolServiceRegistry
{
    IReadOnlyCollection<MagicServiceDescriptor> Services { get; }
    bool TryGet(string name, int version, out IMagicProtocolService? service);
    IMagicProtocolService GetRequired(string name, int version);
}

public sealed class MagicProtocolServiceRegistry : IMagicProtocolServiceRegistry
{
    private readonly IReadOnlyDictionary<(string Name, int Version), IMagicProtocolService> _services;

    public MagicProtocolServiceRegistry(IEnumerable<IMagicProtocolService> services)
    {
        var items = services.ToArray();
        var invalid = items.FirstOrDefault(static service =>
            string.IsNullOrWhiteSpace(service.Descriptor.Name) ||
            service.Descriptor.Version <= 0 ||
            service.Descriptor.DefaultRunTimeoutSeconds <= 0 ||
            service.Descriptor.MaximumRunTimeoutSeconds < service.Descriptor.DefaultRunTimeoutSeconds);
        if (invalid is not null)
        {
            throw new InvalidOperationException($"Magic service '{invalid.GetType().Name}' has an invalid descriptor.");
        }

        var duplicate = items
            .GroupBy(static service => (service.Descriptor.Name, service.Descriptor.Version), MagicServiceKeyComparer.Instance)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Magic service '{duplicate.Key.Name}' version {duplicate.Key.Version} is registered more than once.");
        }

        _services = items.ToDictionary(
            static service => (service.Descriptor.Name, service.Descriptor.Version),
            MagicServiceKeyComparer.Instance);
        Services = items
            .Select(static service => service.Descriptor)
            .OrderBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.Version)
            .ToArray();
    }

    public IReadOnlyCollection<MagicServiceDescriptor> Services { get; }

    public bool TryGet(string name, int version, out IMagicProtocolService? service) =>
        _services.TryGetValue((name, version), out service);

    public IMagicProtocolService GetRequired(string name, int version) =>
        TryGet(name, version, out var service)
            ? service!
            : throw new KeyNotFoundException($"Magic service '{name}' version {version} is not installed.");

    private sealed class MagicServiceKeyComparer : IEqualityComparer<(string Name, int Version)>
    {
        public static MagicServiceKeyComparer Instance { get; } = new();

        public bool Equals((string Name, int Version) x, (string Name, int Version) y) =>
            x.Version == y.Version && string.Equals(x.Name, y.Name, StringComparison.Ordinal);

        public int GetHashCode((string Name, int Version) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Name), obj.Version);
    }
}

public static class MagicServiceSchemaBuilder
{
    public static JsonObject Build(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                       ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            var schema = DescribeType(property.PropertyType);
            if (property.GetCustomAttribute<DescriptionAttribute>() is { } description)
            {
                schema["description"] = description.Description;
            }

            properties[name] = schema;
            if (property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null)
            {
                required.Add(name);
            }
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    private static JsonObject DescribeType(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) type = nullable;
        if (type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset)) return new() { ["type"] = "string" };
        if (type == typeof(bool)) return new() { ["type"] = "boolean" };
        if (type.IsEnum) return new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(type.GetEnumNames().Select(JsonValue.Create).ToArray())
        };
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)) return new() { ["type"] = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return new() { ["type"] = "number" };
        if (type.IsArray) return new() { ["type"] = "array", ["items"] = DescribeType(type.GetElementType()!) };
        return new() { ["type"] = "object" };
    }
}

public interface IGatewayCallerContextResolver
{
    ValueTask<GatewayCallerContext> ResolveAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

public interface IGatewayApplicationResolver
{
    ValueTask<GatewayApplicationContext> ResolveAsync(
        GatewayCallerContext caller,
        string? requestedApplication,
        CancellationToken cancellationToken);
}

public interface IGatewayAgentResolver
{
    ValueTask<GatewayAgentContext?> ResolveAsync(
        GatewayApplicationContext application,
        string? requestedAgent,
        CancellationToken cancellationToken);
}

public interface IGatewayServiceAuthorizationService
{
    ValueTask<bool> IsAllowedAsync(
        GatewayCallerContext caller,
        GatewayApplicationContext application,
        GatewayAgentContext? agent,
        MagicServiceDescriptor service,
        CancellationToken cancellationToken);
}

public sealed class DefaultGatewayCallerContextResolver : IGatewayCallerContextResolver
{
    public ValueTask<GatewayCallerContext> ResolveAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var authenticated = principal.Identity?.IsAuthenticated == true;
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.Identity?.Name
                      ?? "anonymous";
        var roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToHashSet(StringComparer.Ordinal);
        var permissions = principal.FindAll("scope")
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (!authenticated) permissions.Add("*");
        return ValueTask.FromResult(new GatewayCallerContext(subject, roles, permissions, !authenticated));
    }
}

public sealed class DefaultGatewayApplicationResolver : IGatewayApplicationResolver
{
    public ValueTask<GatewayApplicationContext> ResolveAsync(
        GatewayCallerContext caller,
        string? requestedApplication,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(requestedApplication)
            ? MagicAiGatewayProtocol.DefaultApplicationName
            : requestedApplication;
        return ValueTask.FromResult(new GatewayApplicationContext(
            name,
            caller.Roles,
            new HashSet<string>(["*"], StringComparer.Ordinal),
            new HashSet<string>(["*"], StringComparer.Ordinal)));
    }
}

public sealed class DefaultGatewayAgentResolver : IGatewayAgentResolver
{
    public ValueTask<GatewayAgentContext?> ResolveAsync(
        GatewayApplicationContext application,
        string? requestedAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedAgent)) return ValueTask.FromResult<GatewayAgentContext?>(null);
        return ValueTask.FromResult<GatewayAgentContext?>(new GatewayAgentContext(
            requestedAgent,
            application.Roles,
            new HashSet<string>(["*"], StringComparer.Ordinal)));
    }
}

public sealed class DefaultGatewayServiceAuthorizationService : IGatewayServiceAuthorizationService
{
    public ValueTask<bool> IsAllowedAsync(
        GatewayCallerContext caller,
        GatewayApplicationContext application,
        GatewayAgentContext? agent,
        MagicServiceDescriptor service,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            application.AllowedServices.Contains("*") ||
            application.AllowedServices.Contains(service.Name));
}
