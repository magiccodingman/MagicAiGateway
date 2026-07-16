using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharedMagic.Security;

namespace SharedMagic.Tests;

public sealed class GatewayClientSecurityTests
{
    [Theory]
    [InlineData("GET", "/v1/models", false, GatewayOperation.ListModels)]
    [InlineData("POST", "/v1/chat/completions", false, GatewayOperation.CreateInference)]
    [InlineData("POST", "/v1/embeddings", false, GatewayOperation.CreateEmbedding)]
    [InlineData("POST", "/tokenize", false, GatewayOperation.Tokenize)]
    [InlineData("POST", "/detokenize", false, GatewayOperation.Detokenize)]
    [InlineData("POST", "/v1/chat/completions", true, GatewayOperation.GatewayProtocol)]
    public void ResolvesStableGatewayOperations(
        string method,
        string path,
        bool hasGatewayEnvelope,
        GatewayOperation expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        var operation = new GatewayOperationResolver().Resolve(
            context.Request,
            hasGatewayEnvelope);

        Assert.Equal(expected, operation);
    }

    [Fact]
    public async Task AnonymousModePreservesCurrentClientBehavior()
    {
        await using var provider = CreateServices(GatewayAccessMode.Anonymous);
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await authorization.AuthorizeAsync(
            principal,
            new GatewayAuthorizationResource(
                GatewayOperation.CreateInference,
                "Qwen36-27B",
                "/v1/chat/completions"),
            GatewayPolicies.InferenceCreate);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticatedModeRequiresClientSecurityDomain()
    {
        await using var provider = CreateServices(GatewayAccessMode.Authenticated);
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var client = new ClaimsPrincipal(new ClaimsIdentity(
            [GatewayClientIdentity.CreateSecurityDomainClaim()],
            "test-client"));

        var anonymousResult = await authorization.AuthorizeAsync(
            anonymous,
            new GatewayAuthorizationResource(
                GatewayOperation.ListModels,
                null,
                "/v1/models"),
            GatewayPolicies.ModelsRead);
        var clientResult = await authorization.AuthorizeAsync(
            client,
            new GatewayAuthorizationResource(
                GatewayOperation.ListModels,
                null,
                "/v1/models"),
            GatewayPolicies.ModelsRead);

        Assert.False(anonymousResult.Succeeded);
        Assert.True(clientResult.Succeeded);
    }

    [Fact]
    public async Task FabricIdentityCannotSatisfyClientPolicy()
    {
        await using var provider = CreateServices(GatewayAccessMode.Authenticated);
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var fabricIdentity = new ClaimsIdentity(
            [new Claim(FabricAuthenticationDefaults.PeerRoleClaim, "node")],
            FabricAuthenticationDefaults.Scheme);

        var result = await authorization.AuthorizeAsync(
            new ClaimsPrincipal(fabricIdentity),
            new GatewayAuthorizationResource(
                GatewayOperation.CreateInference,
                "Qwen36-27B",
                "/v1/chat/completions"),
            GatewayPolicies.InferenceCreate);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task LocalAnonymousModeAllowsPrivateNetworkCaller()
    {
        await using var provider = CreateServices(GatewayAccessMode.LocalAnonymous);
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.25");
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(
            new ClaimsPrincipal(new ClaimsIdentity()),
            new GatewayAuthorizationResource(
                GatewayOperation.CreateInference,
                "Qwen36-27B",
                "/v1/chat/completions"),
            GatewayPolicies.InferenceCreate);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task OperationPolicyDoesNotAuthorizeDifferentOperation()
    {
        await using var provider = CreateServices(GatewayAccessMode.Anonymous);
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(
            new ClaimsPrincipal(new ClaimsIdentity()),
            new GatewayAuthorizationResource(
                GatewayOperation.CreateEmbedding,
                "embedding-model",
                "/v1/embeddings"),
            GatewayPolicies.InferenceCreate);

        Assert.False(result.Succeeded);
    }

    private static ServiceProvider CreateServices(GatewayAccessMode mode)
    {
        var configuration = new ConfigurationManager
        {
            [$"{GatewayAccessOptions.SectionName}:Mode"] = mode.ToString()
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMagicGatewayClientSecurity(configuration);
        return services.BuildServiceProvider();
    }
}
