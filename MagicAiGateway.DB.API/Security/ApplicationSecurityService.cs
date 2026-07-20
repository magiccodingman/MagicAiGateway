using System.Data;
using MagicAiGateway.DB.Contracts;
using MagicAiGateway.DB.Entities;
using Microsoft.EntityFrameworkCore;

namespace MagicAiGateway.DB.API.Security;

public sealed class ApplicationSecurityService(
    IDbContextFactory<MagicAiGateway.DB.MagicAiGatewayDbContext> contextFactory,
    ApiKeySecretService secrets) : IApplicationAuthorizationEvaluator
{
    private static readonly MagicApplication[] DefaultCoreApplications =
    [
        MagicApplication.PrimaryApi,
        MagicApplication.Web,
        MagicApplication.Mcp
    ];

    public async Task<SecurityStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await GetConfigurationAsync(context, cancellationToken).ConfigureAwait(false);
        var activeKeys = await context.ApiKeys.CountAsync(
            key => key.RevokedAt == null && (key.ExpiresAt == null || key.ExpiresAt > DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return new SecurityStatusResponse(
            configuration.ApplicationSecurityEnabled,
            configuration.SecurityRevision,
            configuration.ActivatedAt,
            activeKeys);
    }

    public async Task<InitializeApplicationCredentialsResponse> InitializeApplicationsAsync(
        InitializeApplicationCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        var applications = (request.Applications is { Count: > 0 }
                ? request.Applications
                : DefaultCoreApplications)
            .Where(static application => application != MagicApplication.Unknown)
            .Distinct()
            .ToArray();
        if (applications.Length == 0) throw new InvalidOperationException("At least one application credential is required.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var configuration = await GetConfigurationAsync(context, cancellationToken).ConfigureAwait(false);
        if (configuration.ApplicationSecurityEnabled || await context.ApiKeys.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Application security has already been initialized.");
        }

        var administratorRole = await GetAdministratorRoleAsync(context, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var generated = new List<GeneratedApplicationCredential>(applications.Length);

        foreach (var application in applications)
        {
            var key = secrets.Generate();
            var principal = new SecurityPrincipalEntity
            {
                Id = Guid.NewGuid(),
                PrincipalType = SecurityPrincipalType.ApiKey,
                Name = $"{application} service credential",
                Description = request.Description ?? "Generated during application security initialization.",
                CreatedAt = now,
                SecurityRevision = configuration.SecurityRevision + 1
            };
            var apiKey = new ApiKeyEntity
            {
                Id = Guid.NewGuid(),
                PrincipalId = principal.Id,
                Principal = principal,
                Prefix = key.Prefix,
                SecretHash = key.Hash,
                CreatedAt = now
            };
            context.SecurityPrincipals.Add(principal);
            context.ApiKeys.Add(apiKey);
            context.ApiKeyApplications.Add(new ApiKeyApplicationEntity
            {
                ApiKeyId = apiKey.Id,
                Application = application,
                AssignedAt = now
            });
            context.PrincipalRoles.Add(new PrincipalRoleEntity
            {
                PrincipalId = principal.Id,
                RoleId = administratorRole.Id,
                AssignedAt = now
            });
            generated.Add(new GeneratedApplicationCredential(application, apiKey.Id, principal.Name, key.Secret));
        }

        configuration.ApplicationSecurityEnabled = true;
        configuration.ActivatedAt = now;
        configuration.SecurityRevision++;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InitializeApplicationCredentialsResponse(generated, configuration.SecurityRevision);
    }

    public async Task<CreateApiKeyResponse> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var configuration = await GetConfigurationAsync(context, cancellationToken).ConfigureAwait(false);
        if (!configuration.ApplicationSecurityEnabled)
        {
            throw new InvalidOperationException(
                "Use the application credential initialization endpoint before creating individual API keys.");
        }

        var roles = await context.Roles
            .Where(role => role.BuiltInRole != null && request.Roles.Contains(role.BuiltInRole.Value))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (roles.Length != request.Roles.Distinct().Count())
        {
            throw new InvalidOperationException("One or more requested built-in roles do not exist in the database.");
        }

        var now = DateTimeOffset.UtcNow;
        var generated = secrets.Generate();
        var principal = new SecurityPrincipalEntity
        {
            Id = Guid.NewGuid(),
            PrincipalType = SecurityPrincipalType.ApiKey,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedAt = now,
            SecurityRevision = configuration.SecurityRevision + 1
        };
        var apiKey = new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            PrincipalId = principal.Id,
            Principal = principal,
            Prefix = generated.Prefix,
            SecretHash = generated.Hash,
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt
        };
        context.SecurityPrincipals.Add(principal);
        context.ApiKeys.Add(apiKey);
        foreach (var application in request.Applications.Distinct())
        {
            context.ApiKeyApplications.Add(new ApiKeyApplicationEntity
            {
                ApiKeyId = apiKey.Id,
                Application = application,
                AssignedAt = now
            });
        }
        foreach (var role in roles)
        {
            context.PrincipalRoles.Add(new PrincipalRoleEntity
            {
                PrincipalId = principal.Id,
                RoleId = role.Id,
                AssignedAt = now
            });
        }

        configuration.SecurityRevision++;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreateApiKeyResponse(
            ToSummary(apiKey, request.Applications.Distinct().ToArray(), request.Roles.Distinct().ToArray()),
            generated.Secret);
    }

    public async Task<IReadOnlyList<ApiKeySummary>> GetApiKeysAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var keys = await context.ApiKeys
            .AsNoTracking()
            .Include(key => key.Principal)
            .ThenInclude(principal => principal.Roles)
            .ThenInclude(link => link.Role)
            .Include(key => key.Applications)
            .OrderBy(key => key.Principal.Name)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return keys.Select(key => ToSummary(
            key,
            key.Applications.Select(link => link.Application).ToArray(),
            key.Principal.Roles.Where(link => link.Role.BuiltInRole != null).Select(link => link.Role.BuiltInRole!.Value).ToArray())).ToArray();
    }

    public async Task<bool> RevokeApiKeyAsync(Guid apiKeyId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var key = await context.ApiKeys.Include(x => x.Principal).SingleOrDefaultAsync(x => x.Id == apiKeyId, cancellationToken).ConfigureAwait(false);
        if (key is null) return false;
        if (key.RevokedAt is null)
        {
            var configuration = await GetConfigurationAsync(context, cancellationToken).ConfigureAwait(false);
            key.RevokedAt = DateTimeOffset.UtcNow;
            key.Principal.SecurityRevision = configuration.SecurityRevision + 1;
            configuration.SecurityRevision++;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<ApplicationAuthorizationDecision> EvaluateAsync(
        ApplicationAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await GetConfigurationAsync(context, cancellationToken).ConfigureAwait(false);
        if (!configuration.ApplicationSecurityEnabled)
        {
            return new ApplicationAuthorizationDecision(
                false, false, true, request.ClaimedApplication, [], null,
                configuration.SecurityRevision, "Application security has not been activated.");
        }

        if (request.ClaimedApplication == MagicApplication.Unknown ||
            string.IsNullOrWhiteSpace(request.CandidateApiKey) ||
            !ApiKeySecretService.TryReadPrefix(request.CandidateApiKey, out var prefix))
        {
            return Denied(configuration, false, "A valid application API key is required.");
        }

        var key = await context.ApiKeys
            .Include(x => x.Principal)
            .ThenInclude(x => x.Roles)
            .ThenInclude(x => x.Role)
            .Include(x => x.Applications)
            .SingleOrDefaultAsync(x => x.Prefix == prefix, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (key is null ||
            key.Principal.DisabledAt is not null ||
            key.RevokedAt is not null ||
            key.ExpiresAt is { } expiresAt && expiresAt <= now ||
            !secrets.Verify(request.CandidateApiKey, key.SecretHash))
        {
            return Denied(configuration, false, "The application API key is invalid, expired, revoked, or disabled.");
        }

        var applications = key.Applications.Select(x => x.Application).ToHashSet();
        if (!applications.Contains(request.ClaimedApplication))
        {
            return Denied(configuration, true, "The API key is not assigned to the claimed application.", key.Principal.Id);
        }

        if (!request.AllowedApplications.Contains(request.ClaimedApplication))
        {
            return Denied(configuration, true, "The authenticated application is not allowed to call this endpoint.", key.Principal.Id);
        }

        var roles = BuiltInRoleHierarchy.Expand(key.Principal.Roles
            .Where(x => x.Role.BuiltInRole != null)
            .Select(x => x.Role.BuiltInRole!.Value));
        var requiredRoles = request.RequiredRoles?.Distinct().ToArray() ?? [];
        if (requiredRoles.Length > 0 && !requiredRoles.Any(roles.Contains))
        {
            return Denied(configuration, true, "The authenticated principal does not have an allowed role.", key.Principal.Id, roles);
        }

        key.LastUsedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ApplicationAuthorizationDecision(
            true,
            true,
            true,
            request.ClaimedApplication,
            roles.OrderBy(static role => role).ToArray(),
            key.Principal.Id,
            configuration.SecurityRevision,
            "Authorized.");
    }

    private static void ValidateCreateRequest(CreateApiKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("A key name is required.", nameof(request));
        if (request.Applications.Count == 0 || request.Applications.Contains(MagicApplication.Unknown))
            throw new ArgumentException("At least one valid application is required.", nameof(request));
        if (request.Roles.Count == 0) throw new ArgumentException("At least one role is required.", nameof(request));
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("Expiration must be in the future.", nameof(request));
    }

    private static ApplicationAuthorizationDecision Denied(
        SecurityConfigurationEntity configuration,
        bool authenticated,
        string reason,
        Guid? principalId = null,
        IEnumerable<BuiltInRole>? roles = null) =>
        new(true, authenticated, false, null, roles?.ToArray() ?? [], principalId, configuration.SecurityRevision, reason);

    private static ApiKeySummary ToSummary(
        ApiKeyEntity key,
        IReadOnlyList<MagicApplication> applications,
        IReadOnlyList<BuiltInRole> roles) =>
        new(
            key.Id,
            key.Principal.Name,
            key.Principal.Description,
            key.Prefix,
            applications,
            roles,
            key.CreatedAt,
            key.ExpiresAt,
            key.LastUsedAt,
            key.RevokedAt);

    private static async Task<SecurityConfigurationEntity> GetConfigurationAsync(
        MagicAiGateway.DB.MagicAiGatewayDbContext context,
        CancellationToken cancellationToken) =>
        await context.SecurityConfiguration.SingleAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);

    private static async Task<RoleEntity> GetAdministratorRoleAsync(
        MagicAiGateway.DB.MagicAiGatewayDbContext context,
        CancellationToken cancellationToken) =>
        await context.Roles.SingleAsync(role => role.BuiltInRole == BuiltInRole.Administrator, cancellationToken).ConfigureAwait(false);
}
