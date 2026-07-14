using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedMagic.Configuration;

namespace SharedMagic.Contracts;

public sealed record ModelDescriptor(string Id, string? OwnedBy = null, string? BackendId = null);

public sealed record BackendSnapshot(
    string Id,
    string Name,
    AiBackendKind Kind,
    string BaseUri,
    bool Healthy,
    DateTimeOffset LastCheckedAt,
    IReadOnlyList<ModelDescriptor> Models,
    string? Error = null);

public sealed record NodeHeartbeat(
    Guid NodeId,
    string Name,
    string BaseUri,
    DateTimeOffset SentAt,
    IReadOnlyList<BackendSnapshot> Backends);

public sealed record NodeSnapshot(
    Guid NodeId,
    string Name,
    string BaseUri,
    bool Online,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<BackendSnapshot> Backends);

public sealed record PairingChallengeRequest(Guid NodeId, string ExpectedGatewayName);

public sealed record PairingChallengeResponse(
    Guid ChallengeId,
    string NonceBase64,
    Guid GatewayId,
    Guid ClusterId,
    string GatewayName,
    DateTimeOffset ExpiresAt);

public sealed record PairingRequest(
    Guid NodeId,
    string NodeName,
    string ExpectedGatewayName,
    string AdvertisedBaseUri,
    string CsrBase64,
    Guid? ChallengeId = null,
    string? EnrollmentProofBase64 = null);

public sealed record PairingResponse(
    string Status,
    Guid GatewayId,
    Guid ClusterId,
    string GatewayName,
    string? CertificateBase64 = null,
    string? RootCertificateBase64 = null,
    string? GatewayProofBase64 = null,
    string? Message = null);

public sealed record TokenizerDescriptor(
    string Model,
    string Provider,
    string? ModelPath,
    string? ChatTemplate,
    JsonElement? Raw,
    bool SupportsRemoteTokenize);

public sealed record TokenizeRequest(string Model, JsonElement Input, bool AddSpecialTokens = true);
public sealed record TokenizeResponse(string Model, IReadOnlyList<int> Tokens, int Count);

public sealed record OpenAiErrorBody(OpenAiError Error);
public sealed record OpenAiError(string Message, string Type, string? Param = null, string? Code = null);

public static class OpenAiErrors
{
    public static OpenAiErrorBody NotFound(string message, string? param = "model") =>
        new(new(message, "invalid_request_error", param, "model_not_found"));

    public static OpenAiErrorBody NotImplemented(string message) =>
        new(new(message, "not_implemented_error", null, "not_implemented"));

    public static OpenAiErrorBody Overloaded(string message) =>
        new(new(message, "server_error", null, "queue_full"));
}


public static class PairingProof
{
    public static byte[] Compute(
        string enrollmentToken,
        Guid challengeId,
        byte[] nonce,
        Guid nodeId,
        Guid gatewayId,
        Guid clusterId,
        string gatewayName)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(enrollmentToken));
        var canonical = string.Join("|",
            challengeId.ToString("D"),
            Convert.ToBase64String(nonce),
            nodeId.ToString("D"),
            gatewayId.ToString("D"),
            clusterId.ToString("D"),
            gatewayName);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }

    public static byte[] ComputeGatewayResponse(
        string enrollmentToken,
        Guid challengeId,
        Guid nodeId,
        Guid gatewayId,
        Guid clusterId,
        string gatewayName,
        byte[] issuedCertificate,
        byte[] rootCertificate)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(enrollmentToken));
        var canonical = string.Join("|",
            "gateway-response-v1",
            challengeId.ToString("D"),
            nodeId.ToString("D"),
            gatewayId.ToString("D"),
            clusterId.ToString("D"),
            gatewayName,
            Convert.ToHexString(SHA256.HashData(issuedCertificate)),
            Convert.ToHexString(SHA256.HashData(rootCertificate)));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }
}
