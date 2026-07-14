using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Contracts;
using SharedMagic.Routing;

namespace MagicAiNode;

public sealed record BackendRouteTarget(BackendOptions Options, bool IsHealthy, IReadOnlyList<ModelDescriptor> Models) : IRouteTarget
{
    public string Id => Options.Id;
    public string BaseUri => Options.BaseUri.TrimEnd('/');
}

public sealed class BackendCatalog
{
    private readonly ConcurrentDictionary<string, BackendSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BackendOptions> _options;
    private readonly IRequestScheduler<BackendRouteTarget> _scheduler;
    private readonly object _routeSync = new();
    private HashSet<string> _knownModels = new(StringComparer.Ordinal);

    public BackendCatalog(IOptions<NodeOptions> options, IRequestScheduler<BackendRouteTarget> scheduler)
    {
        _options = options.Value.Backends.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        _scheduler = scheduler;
    }

    public IReadOnlyList<BackendSnapshot> GetSnapshots() => _snapshots.Values.OrderBy(static x => x.Name).ToArray();

    public void Update(BackendOptions options, BackendSnapshot snapshot)
    {
        _snapshots[options.Id] = snapshot;
        RebuildRoutes();
    }

    public BackendRouteTarget? FindForModel(string model) => GetTargets(model).FirstOrDefault();

    public BackendOptions GetOptions(string id) => _options[id];

    private BackendRouteTarget[] GetTargets(string model) => _snapshots.Values
        .Where(x => x.Healthy && x.Models.Any(m => string.Equals(m.Id, model, StringComparison.Ordinal)))
        .Select(x => new BackendRouteTarget(_options[x.Id], true, x.Models))
        .ToArray();

    private void RebuildRoutes()
    {
        lock (_routeSync)
        {
            var models = _snapshots.Values
                .SelectMany(static x => x.Models)
                .Select(static x => x.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var model in _knownModels.Union(models, StringComparer.Ordinal))
            {
                _scheduler.ReplaceTargets(model, GetTargets(model));
            }
            _knownModels = models;
        }
    }
}

public interface IAiBackendAdapter
{
    AiBackendKind Kind { get; }
    Task<BackendSnapshot> ProbeAsync(BackendOptions options, CancellationToken cancellationToken);
    Task<TokenizerDescriptor?> GetTokenizerAsync(BackendOptions options, string model, CancellationToken cancellationToken);
    Task<TokenizeResponse?> TokenizeAsync(BackendOptions options, TokenizeRequest request, CancellationToken cancellationToken);
}

public abstract class AiBackendAdapterBase
{
    protected static HttpClient CreateClient(BackendOptions options)
    {
        var handler = new HttpClientHandler();
        if (options.AllowInvalidServerCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        var client = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUri.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return client;
    }

    protected static IReadOnlyList<ModelDescriptor> ParseModels(JsonElement root, string backendId)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray()
            .Where(static x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(x => new ModelDescriptor(x.GetProperty("id").GetString()!,
                x.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null,
                backendId))
            .ToArray();
    }
}

public sealed class VllmBackendAdapter : AiBackendAdapterBase, IAiBackendAdapter
{
    public AiBackendKind Kind => AiBackendKind.Vllm;

    public async Task<BackendSnapshot> ProbeAsync(BackendOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(options);
            using var response = await client.GetAsync("v1/models", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return new(options.Id, options.Name, options.Kind, options.BaseUri, true, DateTimeOffset.UtcNow, ParseModels(document.RootElement, options.Id));
        }
        catch (Exception exception)
        {
            return new(options.Id, options.Name, options.Kind, options.BaseUri, false, DateTimeOffset.UtcNow, [], exception.Message);
        }
    }

    public async Task<TokenizerDescriptor?> GetTokenizerAsync(BackendOptions options, string model, CancellationToken cancellationToken)
    {
        using var client = CreateClient(options);
        using var response = await client.GetAsync($"tokenizer_info?model={Uri.EscapeDataString(model)}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement.Clone();
        var path = root.TryGetProperty("tokenizer", out var tokenizer) && tokenizer.TryGetProperty("name_or_path", out var nameOrPath) ? nameOrPath.GetString() : null;
        var template = root.TryGetProperty("chat_template", out var chatTemplate) ? chatTemplate.GetString() : null;
        return new(model, "vllm", path, template, root, true);
    }

    public async Task<TokenizeResponse?> TokenizeAsync(BackendOptions options, TokenizeRequest request, CancellationToken cancellationToken)
    {
        using var client = CreateClient(options);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { model = request.Model, prompt = request.Input, add_special_tokens = request.AddSpecialTokens });
        using var response = await client.PostAsync("tokenize", new ByteArrayContent(payload) { Headers = { ContentType = new("application/json") } }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var tokens = document.RootElement.GetProperty("tokens").EnumerateArray().Select(static x => x.GetInt32()).ToArray();
        return new(request.Model, tokens, tokens.Length);
    }
}

public sealed class LlamaCppBackendAdapter : AiBackendAdapterBase, IAiBackendAdapter
{
    public AiBackendKind Kind => AiBackendKind.LlamaCpp;

    public async Task<BackendSnapshot> ProbeAsync(BackendOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(options);
            using var response = await client.GetAsync("v1/models", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return new(options.Id, options.Name, options.Kind, options.BaseUri, true, DateTimeOffset.UtcNow, ParseModels(document.RootElement, options.Id));
        }
        catch (Exception exception)
        {
            return new(options.Id, options.Name, options.Kind, options.BaseUri, false, DateTimeOffset.UtcNow, [], exception.Message);
        }
    }

    public async Task<TokenizerDescriptor?> GetTokenizerAsync(BackendOptions options, string model, CancellationToken cancellationToken)
    {
        using var client = CreateClient(options);
        using var response = await client.GetAsync("props", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement.Clone();
        var path = root.TryGetProperty("model_path", out var modelPath) ? modelPath.GetString() : null;
        var template = root.TryGetProperty("chat_template", out var chatTemplate) ? chatTemplate.GetString() : null;
        return new(model, "llama.cpp", path, template, root, true);
    }

    public async Task<TokenizeResponse?> TokenizeAsync(BackendOptions options, TokenizeRequest request, CancellationToken cancellationToken)
    {
        using var client = CreateClient(options);
        var content = request.Input.ValueKind == JsonValueKind.String ? request.Input.GetString() : request.Input.GetRawText();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content, add_special = request.AddSpecialTokens });
        using var response = await client.PostAsync("tokenize", new ByteArrayContent(payload) { Headers = { ContentType = new("application/json") } }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var tokens = document.RootElement.GetProperty("tokens").EnumerateArray().Select(static x => x.GetInt32()).ToArray();
        return new(request.Model, tokens, tokens.Length);
    }
}

public sealed class BackendMonitorService(
    IOptions<NodeOptions> nodeOptions,
    IEnumerable<IAiBackendAdapter> adapters,
    BackendCatalog catalog,
    ILogger<BackendMonitorService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var adapterMap = adapters.ToDictionary(static x => x.Kind);
        var tasks = nodeOptions.Value.Backends.Where(static x => x.Enabled).Select(options => MonitorAsync(options, adapterMap[options.Kind], stoppingToken));
        return Task.WhenAll(tasks);
    }

    private async Task MonitorAsync(BackendOptions options, IAiBackendAdapter adapter, CancellationToken cancellationToken)
    {
        var healthy = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await adapter.ProbeAsync(options, cancellationToken).ConfigureAwait(false);
            catalog.Update(options, snapshot);
            if (snapshot.Healthy != healthy)
            {
                logger.LogInformation("Backend {Backend} is now {Status} with {ModelCount} model(s).", options.Name, snapshot.Healthy ? "healthy" : "offline", snapshot.Models.Count);
                healthy = snapshot.Healthy;
            }
            var seconds = snapshot.Healthy ? nodeOptions.Value.HealthyPollSeconds : nodeOptions.Value.OfflinePollSeconds;
            var jitter = Random.Shared.NextDouble() * Math.Max(1, seconds * 0.2);
            await Task.Delay(TimeSpan.FromSeconds(seconds + jitter), cancellationToken).ConfigureAwait(false);
        }
    }
}
