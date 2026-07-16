using System.Collections.Concurrent;
using System.Text;
using MagicAiGateway.Protocol;

namespace SharedMagic.Execution;

public sealed record GatewayCallerContext(
    string Subject,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Permissions,
    bool IsAnonymous);

public sealed record GatewayApplicationContext(
    string Name,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> AllowedServices,
    IReadOnlySet<string> AllowedAgents);

public sealed record GatewayAgentContext(
    string Name,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> AllowedTools);

public sealed record GatewayRunLimits
{
    public TimeSpan DefaultDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(30);
    public int MaximumContinuationRounds { get; init; } = 16;
    public int MaximumToolCalls { get; init; } = 64;
    public int MaximumJournalCharacters { get; init; } = 1_000_000;

    public TimeSpan ResolveDuration(int? requestedSeconds)
    {
        if (requestedSeconds is null) return DefaultDuration;
        if (requestedSeconds <= 0) throw new InvalidOperationException("Requested run timeout must be greater than zero.");
        var requested = TimeSpan.FromSeconds(requestedSeconds.Value);
        return requested <= MaximumDuration ? requested : MaximumDuration;
    }
}

public sealed class GatewayConversationTranscript
{
    private readonly object _sync = new();
    private readonly List<MagicChatMessage> _messages;

    public GatewayConversationTranscript(IEnumerable<MagicChatMessage> messages) =>
        _messages = [.. messages];

    public IReadOnlyList<MagicChatMessage> Messages
    {
        get
        {
            lock (_sync) return _messages.ToArray();
        }
    }

    public void Append(MagicChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync) _messages.Add(message);
    }

    public MagicChatCompletionRequest CreateContinuationRequest(
        MagicChatCompletionRequest original,
        bool includeMagicEnvelope = false)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original with
        {
            Messages = Messages,
            MagicAiGateway = includeMagicEnvelope ? original.MagicAiGateway : null
        };
    }
}

public sealed record GatewayJournalEvent(
    DateTimeOffset Timestamp,
    string Type,
    string? Text = null,
    object? Data = null);

public sealed class GatewayRunJournal
{
    private readonly object _sync = new();
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly List<GatewayJournalEvent> _events = [];
    private readonly int _maximumCharacters;

    public GatewayRunJournal(int maximumCharacters) =>
        _maximumCharacters = maximumCharacters > 0
            ? maximumCharacters
            : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

    public bool IsTruncated { get; private set; }

    public string Content
    {
        get { lock (_sync) return _content.ToString(); }
    }

    public string Reasoning
    {
        get { lock (_sync) return _reasoning.ToString(); }
    }

    public IReadOnlyList<GatewayJournalEvent> Events
    {
        get { lock (_sync) return _events.ToArray(); }
    }

    public void AppendContent(string text) => Append(_content, "content.delta", text);
    public void AppendReasoning(string text) => Append(_reasoning, "reasoning.delta", text);

    public void Record(string type, object? data = null)
    {
        lock (_sync) _events.Add(new(DateTimeOffset.UtcNow, type, Data: data));
    }

    private void Append(StringBuilder builder, string type, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_sync)
        {
            var remaining = _maximumCharacters - (_content.Length + _reasoning.Length);
            if (remaining <= 0)
            {
                IsTruncated = true;
                return;
            }

            var accepted = text.Length <= remaining ? text : text[..remaining];
            builder.Append(accepted);
            _events.Add(new(DateTimeOffset.UtcNow, type, accepted));
            if (accepted.Length != text.Length) IsTruncated = true;
        }
    }
}

public sealed class MagicUsageAccumulator
{
    private readonly object _sync = new();
    private readonly List<MagicModelCallUsage> _calls = [];

    public void Add(MagicTokenUsage usage, string? model = null, string accuracy = MagicUsageAccuracy.ProviderReported)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (_sync)
        {
            _calls.Add(new MagicModelCallUsage
            {
                Sequence = _calls.Count + 1,
                Model = model,
                Usage = usage,
                Accuracy = accuracy
            });
        }
    }

    public IReadOnlyList<MagicModelCallUsage> Calls
    {
        get { lock (_sync) return _calls.ToArray(); }
    }

    public MagicTokenUsage Total
    {
        get
        {
            lock (_sync)
            {
                return _calls.Aggregate(new MagicTokenUsage(), static (total, call) => total + call.Usage);
            }
        }
    }

    public string Accuracy
    {
        get
        {
            lock (_sync)
            {
                if (_calls.Count == 0) return MagicUsageAccuracy.Unavailable;
                var distinct = _calls.Select(static call => call.Accuracy).Distinct(StringComparer.Ordinal).ToArray();
                return distinct.Length == 1 ? distinct[0] : MagicUsageAccuracy.Partial;
            }
        }
    }
}

public sealed class GatewayRunContext
{
    public required Guid RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required MagicChatCompletionRequest OriginalRequest { get; init; }
    public required MagicAiGatewayEnvelope Protocol { get; init; }
    public required MagicServiceDescriptor Service { get; init; }
    public required GatewayCallerContext Caller { get; init; }
    public required GatewayApplicationContext Application { get; init; }
    public GatewayAgentContext? Agent { get; init; }
    public required GatewayRunLimits Limits { get; init; }
    public required GatewayConversationTranscript Transcript { get; init; }
    public required GatewayRunJournal Journal { get; init; }
    public MagicUsageAccumulator Usage { get; } = new();
    public ConcurrentDictionary<string, object> ServiceState { get; } = new(StringComparer.Ordinal);
    public string Status { get; set; } = MagicRunStatuses.Running;
    public int ToolCalls { get; set; }
    public int ContinuationRounds { get; set; }
}

public sealed record GatewayRunSnapshot(
    Guid RunId,
    string Service,
    string Application,
    string? Agent,
    DateTimeOffset StartedAt,
    string Status,
    int ModelCalls,
    int ToolCalls);
