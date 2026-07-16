namespace MagicAiGateway.MCP.Package.Template.Runtime;

internal enum MagicMcpStatus
{
    Success = 0,
    NoMessage = 1,
    InvalidArgument = 2,
    InstanceNotFound = 3,
    BufferTooSmall = 4,
    InstanceStopped = 5,
    InternalError = 100
}
