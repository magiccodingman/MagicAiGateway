using System.Security.Cryptography.X509Certificates;
using MagicAiGateway.Client.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Security;

namespace MagicAiApi.Controllers;

[ApiController]
public sealed class GatewayInfoController(
    GatewayCertificateAuthority authority,
    IOptions<GatewayOptions> gatewayOptions) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet(MagicAiGatewayProtocol.GatewayInfoPath)]
    public ActionResult<GatewayInfo> GetInfo() => Ok(new GatewayInfo(
        gatewayOptions.Value.Name,
        authority.Identity.InstanceId,
        authority.Identity.ClusterId,
        MagicAiGatewayProtocol.CurrentVersion,
        MinimumClientProtocolVersion: 1,
        Convert.ToBase64String(authority.RootCertificate.Export(X509ContentType.Cert)),
        [
            "openai-proxy",
            "streaming",
            "gateway-protocol"
        ]));
}
