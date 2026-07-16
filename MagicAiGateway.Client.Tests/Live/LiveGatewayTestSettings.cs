using MagicAiGateway.Client.Configuration;

namespace MagicAiGateway.Client.Tests.Live;

internal sealed record LiveGatewayTestSettings(
    Uri Endpoint,
    string ExpectedGatewayName,
    string? ApiKey,
    string? Model,
    bool RunInference,
    GatewayTrustMode TrustMode,
    TimeSpan Timeout)
{
    public static LiveGatewayTestSettings LoadOrSkip()
    {
        var endpointValue = Environment.GetEnvironmentVariable("MAGIC_AI_GATEWAY_TEST_ENDPOINT");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(endpointValue),
            "Set MAGIC_AI_GATEWAY_TEST_ENDPOINT to run live gateway tests.");

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                "MAGIC_AI_GATEWAY_TEST_ENDPOINT must be an absolute URI.");
        }

        var timeoutSeconds = ReadPositiveInteger("MAGIC_AI_GATEWAY_TEST_TIMEOUT_SECONDS", 120);
        return new LiveGatewayTestSettings(
            endpoint,
            Environment.GetEnvironmentVariable("MAGIC_AI_GATEWAY_TEST_EXPECTED_NAME") ?? "MagicAiGateway",
            Environment.GetEnvironmentVariable("MAGIC_AI_GATEWAY_TEST_API_KEY"),
            Environment.GetEnvironmentVariable("MAGIC_AI_GATEWAY_TEST_MODEL"),
            ReadBoolean("MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE"),
            ReadTrustMode(),
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    public void RequireInference()
    {
        Assert.SkipUnless(
            RunInference,
            "Set MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE=true to run live inference tests.");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(Model),
            "Set MAGIC_AI_GATEWAY_TEST_MODEL to run live inference tests.");
    }

    private static GatewayTrustMode ReadTrustMode()
    {
        var value = Environment.GetEnvironmentVariable("MAGIC_AI_GATEWAY_TEST_TRUST_MODE");
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "local-tofu" => GatewayTrustMode.SystemOrLocalTrustOnFirstUse,
            "system" => GatewayTrustMode.SystemOnly,
            "tofu" => GatewayTrustMode.TrustOnFirstUse,
            "insecure-development" => GatewayTrustMode.InsecureDevelopment,
            _ => throw new InvalidOperationException(
                "MAGIC_AI_GATEWAY_TEST_TRUST_MODE must be system, local-tofu, tofu, or insecure-development.")
        };
    }

    private static bool ReadBoolean(string name) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value;

    private static int ReadPositiveInteger(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (int.TryParse(raw, out var value) && value > 0) return value;
        throw new InvalidOperationException($"{name} must be a positive integer.");
    }
}
