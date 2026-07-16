using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharedMagic.Security;

public enum GatewayAccessMode
{
    Anonymous,
    LocalAnonymous,
    Authenticated
}

public sealed class GatewayAccessOptions
{
    public const string SectionName = "GatewayAccess";

    public GatewayAccessMode Mode { get; set; } = GatewayAccessMode.Anonymous;
}

public static class GatewayClientAuthenticationDefaults
{
    public const string Scheme = "MagicGatewayClient";
    public const string SecurityDomainClaim = "magic_security_domain";
    public const string SecurityDomainValue = "client";
}

public static class GatewayPolicies
{
    public const string ClientAccess = "MagicGateway.Client.Access";
    public const string ModelsRead = "MagicGateway.Models.Read";
    public const string InferenceCreate = "MagicGateway.Inference.Create";
    public const string EmbeddingsCreate = "MagicGateway.Embeddings.Create";
    public const string Tokenize = "MagicGateway.Tokenize";
    public const string GatewayProtocolInvoke = "MagicGateway.Protocol.Invoke";
    public const string StatusRead = "MagicGateway.Status.Read";
    public const string Administration = "MagicGateway.Administration";

    public static string ForOperation(GatewayOperation operation) => operation switch
    {
        GatewayOperation.ListModels => ModelsRead,
        GatewayOperation.CreateInference => InferenceCreate,
        GatewayOperation.CreateEmbedding => EmbeddingsCreate,
        GatewayOperation.Tokenize or GatewayOperation.Detokenize => Tokenize,
        GatewayOperation.GatewayProtocol => GatewayProtocolInvoke,
        GatewayOperation.ReadStatus => StatusRead,
        GatewayOperation.Administration => Administration,
        _ => ClientAccess
    };
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class MagicGatewayAuthorizeAttribute : AuthorizeAttribute
{
    public MagicGatewayAuthorizeAttribute(string policy = GatewayPolicies.ClientAccess) => Policy = policy;
}

public enum GatewayOperation
{
    Unknown,
    ProxyRequest,
    ListModels,
    CreateInference,
    CreateEmbedding,
    Tokenize,
    Detokenize,
    GatewayProtocol,
    ReadStatus,
    Administration
}

public sealed record GatewayOperationMetadata(GatewayOperation Operation);

public sealed record GatewayAuthorizationResource(
    GatewayOperation Operation,
    string? Model,
    PathString Path);

public interface IGatewayOperationResolver
{
    GatewayOperation Resolve(HttpRequest request, bool hasGatewayProtocolEnvelope = false);
}

public sealed class GatewayOperationResolver : IGatewayOperationResolver
{
    public GatewayOperation Resolve(HttpRequest request, bool hasGatewayProtocolEnvelope = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (hasGatewayProtocolEnvelope)
        {
            return GatewayOperation.GatewayProtocol;
        }

        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        if (path.Length == 0) path = "/";

        if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayOperation.Administration;
        }

        if (path.Equals("/status", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/status/", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayOperation.ReadStatus;
        }

        if (path.Equals("/tokenize", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/v1/tokenize", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayOperation.Tokenize;
        }

        if (path.Equals("/detokenize", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/v1/detokenize", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayOperation.Detokenize;
        }

        if (path.StartsWith("/v1/models", StringComparison.OrdinalIgnoreCase) &&
            HttpMethods.IsGet(request.Method))
        {
            return GatewayOperation.ListModels;
        }

        if (path.Contains("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayOperation.CreateEmbedding;
        }

        if (path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/completions", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayOperation.CreateInference;
        }

        return GatewayOperation.ProxyRequest;
    }
}

public static class GatewayClientIdentity
{
    public static bool IsClientAuthenticated(ClaimsPrincipal principal) =>
        principal.Identities.Any(identity =>
            identity.IsAuthenticated &&
            identity.HasClaim(
                GatewayClientAuthenticationDefaults.SecurityDomainClaim,
                GatewayClientAuthenticationDefaults.SecurityDomainValue));

    public static Claim CreateSecurityDomainClaim() => new(
        GatewayClientAuthenticationDefaults.SecurityDomainClaim,
        GatewayClientAuthenticationDefaults.SecurityDomainValue);
}

public sealed class GatewayClientAuthenticationOptions : AuthenticationSchemeOptions;

public sealed class GatewayClientAuthenticationHandler(
    IOptionsMonitor<GatewayClientAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<GatewayClientAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

public sealed record GatewayClientAccessRequirement : IAuthorizationRequirement;

public sealed record GatewayOperationRequirement(GatewayOperation Operation) : IAuthorizationRequirement;

public sealed class GatewayClientAccessAuthorizationHandler(
    IOptionsMonitor<GatewayAccessOptions> options,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<GatewayClientAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayClientAccessRequirement requirement)
    {
        if (GatewayAccessEvaluator.IsAllowed(context.User, options.CurrentValue, httpContextAccessor.HttpContext))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class GatewayOperationAuthorizationHandler(
    IOptionsMonitor<GatewayAccessOptions> options,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<GatewayOperationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayOperationRequirement requirement)
    {
        if (context.Resource is GatewayAuthorizationResource resource &&
            resource.Operation != requirement.Operation)
        {
            return Task.CompletedTask;
        }

        if (GatewayAccessEvaluator.IsAllowed(context.User, options.CurrentValue, httpContextAccessor.HttpContext))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class GatewayAccessEvaluator
{
    public static bool IsAllowed(
        ClaimsPrincipal principal,
        GatewayAccessOptions options,
        HttpContext? httpContext)
    {
        if (options.Mode == GatewayAccessMode.Anonymous)
        {
            return true;
        }

        if (GatewayClientIdentity.IsClientAuthenticated(principal))
        {
            return true;
        }

        return options.Mode == GatewayAccessMode.LocalAnonymous &&
               IsLocalAddress(httpContext?.Connection.RemoteIpAddress);
    }

    private static bool IsLocalAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }
}

public static class GatewayClientSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddMagicGatewayClientSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<GatewayAccessOptions>(configuration.GetSection(GatewayAccessOptions.SectionName));
        services.AddHttpContextAccessor();
        services.AddAuthentication()
            .AddScheme<GatewayClientAuthenticationOptions, GatewayClientAuthenticationHandler>(
                GatewayClientAuthenticationDefaults.Scheme,
                _ => { });

        services.AddAuthorization(options =>
        {
            AddClientPolicy(options, GatewayPolicies.ClientAccess, new GatewayClientAccessRequirement());
            AddOperationPolicy(options, GatewayPolicies.ModelsRead, GatewayOperation.ListModels);
            AddOperationPolicy(options, GatewayPolicies.InferenceCreate, GatewayOperation.CreateInference);
            AddOperationPolicy(options, GatewayPolicies.EmbeddingsCreate, GatewayOperation.CreateEmbedding);
            AddOperationPolicy(options, GatewayPolicies.Tokenize, GatewayOperation.Tokenize);
            AddOperationPolicy(options, GatewayPolicies.GatewayProtocolInvoke, GatewayOperation.GatewayProtocol);
            AddOperationPolicy(options, GatewayPolicies.StatusRead, GatewayOperation.ReadStatus);
            AddOperationPolicy(options, GatewayPolicies.Administration, GatewayOperation.Administration);
        });

        services.AddSingleton<IAuthorizationHandler, GatewayClientAccessAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, GatewayOperationAuthorizationHandler>();
        services.AddSingleton<IGatewayOperationResolver, GatewayOperationResolver>();
        return services;
    }

    private static void AddClientPolicy(
        AuthorizationOptions options,
        string name,
        IAuthorizationRequirement requirement)
    {
        options.AddPolicy(name, policy =>
        {
            policy.AddAuthenticationSchemes(GatewayClientAuthenticationDefaults.Scheme);
            policy.Requirements.Add(requirement);
        });
    }

    private static void AddOperationPolicy(
        AuthorizationOptions options,
        string name,
        GatewayOperation operation) =>
        AddClientPolicy(options, name, new GatewayOperationRequirement(operation));
}
