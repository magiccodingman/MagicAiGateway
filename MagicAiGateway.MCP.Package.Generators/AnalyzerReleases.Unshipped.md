; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
MAGICMCP001 | MagicAiGateway.MCP.Package | Error | A package requires exactly one configuration method.
MAGICMCP002 | MagicAiGateway.MCP.Package | Error | Multiple package configuration methods are not allowed.
MAGICMCP003 | MagicAiGateway.MCP.Package | Error | Package configuration methods must use the supported signature.
MAGICMCP004 | MagicAiGateway.MCP.Package | Error | MCP tool names must be unique within a package.
MAGICMCP101 | MagicAiGateway.MCP.Package | Error | MCP tool types must derive from MagicMcpToolController.
MAGICMCP102 | MagicAiGateway.MCP.Package | Error | MagicMcpToolController types must opt into MCP discovery.
MAGICMCP103 | MagicAiGateway.MCP.Package | Error | MCP tool controllers must be concrete classes.
MAGICMCP104 | MagicAiGateway.MCP.Package | Error | MCP tool methods must belong to valid Magic tool controllers.
MAGICMCP105 | MagicAiGateway.MCP.Package | Error | MCP controller tool methods must be instance methods.
MAGICMCP106 | MagicAiGateway.MCP.Package | Error | MCP tool controllers cannot be registered as singletons.
MAGICMCP107 | MagicAiGateway.MCP.Package | Error | MCP tool controllers must contain at least one MCP tool method.
