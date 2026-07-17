namespace MagicAiGateway.DB.Contracts;

public sealed record ApiKeySummary(
    Guid Id,
    string Name,
    string? Description,
    string Prefix,
    IReadOnlyList<MagicApplication> Applications,
    IReadOnlyList<BuiltInRole> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record CreateApiKeyRequest(
    string Name,
    string? Description,
    IReadOnlyCollection<MagicApplication> Applications,
    IReadOnlyCollection<BuiltInRole> Roles,
    DateTimeOffset? ExpiresAt = null);

public sealed record CreateApiKeyResponse(ApiKeySummary ApiKey, string Secret);

public sealed record InitializeApplicationCredentialsRequest(
    IReadOnlyCollection<MagicApplication>? Applications = null,
    string? Description = null);

public sealed record GeneratedApplicationCredential(
    MagicApplication Application,
    Guid ApiKeyId,
    string Name,
    string Secret);

public sealed record InitializeApplicationCredentialsResponse(
    IReadOnlyList<GeneratedApplicationCredential> Credentials,
    long SecurityRevision);

public sealed record ApplicationAuthorizationRequest(
    string? CandidateApiKey,
    MagicApplication ClaimedApplication,
    IReadOnlyCollection<MagicApplication> AllowedApplications,
    IReadOnlyCollection<BuiltInRole>? RequiredRoles = null);

public sealed record ApplicationAuthorizationDecision(
    bool SecurityEnabled,
    bool Authenticated,
    bool Authorized,
    MagicApplication? Application,
    IReadOnlyList<BuiltInRole> Roles,
    Guid? PrincipalId,
    long SecurityRevision,
    string Reason);

public sealed record SecurityStatusResponse(
    bool ApplicationSecurityEnabled,
    long SecurityRevision,
    DateTimeOffset? ActivatedAt,
    int ActiveApiKeyCount);

public sealed record AdministratorBootstrapStatusResponse(
    bool AdministratorExists,
    bool MustChangePassword,
    bool ApplicationSecurityEnabled);

public sealed record UserLoginRequest(string Username, string Password);

public sealed record UserLoginResponse(
    bool Authenticated,
    Guid? UserId,
    string? Username,
    bool PasswordChangeRequired,
    IReadOnlyList<BuiltInRole> Roles,
    string? Error = null);

public sealed record ChangePasswordRequest(
    string Username,
    string CurrentPassword,
    string NewPassword);

public sealed record AdminPasswordRecoveryRequest(string NewTemporaryPassword);

public interface IApplicationAuthorizationEvaluator
{
    Task<ApplicationAuthorizationDecision> EvaluateAsync(
        ApplicationAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public static class MagicAuthorizationHeaders
{
    public const string Application = "X-Magic-Application";
    public const string BootstrapToken = "X-Magic-Bootstrap-Token";
    public const string RecoveryToken = "X-Magic-Recovery-Token";
}
