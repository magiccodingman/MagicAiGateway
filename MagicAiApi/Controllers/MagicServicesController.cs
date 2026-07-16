using MagicAiGateway.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharedMagic.Execution;
using SharedMagic.Security;

namespace MagicAiApi.Controllers;

[ApiController]
[Authorize(Policy = GatewayPolicies.ClientAccess)]
public sealed class MagicServicesController(IMagicProtocolServiceRegistry services) : ControllerBase
{
    [HttpGet(MagicAiGatewayProtocol.ServicesPath)]
    public ActionResult<MagicServiceCatalog> GetServices() => Ok(new MagicServiceCatalog
    {
        Data = services.Services.ToArray()
    });

    [HttpGet(MagicAiGatewayProtocol.ServicesPath + "/{name}")]
    public ActionResult<MagicServiceDescriptor> GetService(string name, [FromQuery] int version = 1)
    {
        if (!services.TryGet(name, version, out var service))
        {
            return NotFound(new
            {
                error = new
                {
                    message = $"Magic service '{name}' version {version} is not installed.",
                    type = "not_found_error",
                    code = "service_not_found"
                }
            });
        }

        return Ok(service!.Descriptor);
    }
}
