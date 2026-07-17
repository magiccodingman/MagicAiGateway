using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MagicAiGateway.MCP.Package.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MagicMcpPackageAnalyzer : DiagnosticAnalyzer
{
    private const string ToolTypeAttributeName =
        "ModelContextProtocol.Server.McpServerToolTypeAttribute";
    private const string ToolMethodAttributeName =
        "ModelContextProtocol.Server.McpServerToolAttribute";
    private const string ControllerBaseTypeName =
        "MagicAiGateway.MCP.Package.MagicMcpToolController";

    private static readonly DiagnosticDescriptor ToolTypeRequiresController = new(
        "MAGICMCP101",
        "MCP tool types must be Magic tool controllers",
        "Type '{0}' is marked [McpServerToolType] but does not derive from MagicMcpToolController",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ControllerRequiresToolType = new(
        "MAGICMCP102",
        "Magic tool controllers must declare MCP discovery intent",
        "Type '{0}' derives from MagicMcpToolController but is not marked [McpServerToolType]",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ControllerMustBeConcrete = new(
        "MAGICMCP103",
        "MCP tool controllers must be concrete",
        "Tool controller '{0}' must be a non-abstract, non-static class",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ToolMethodRequiresController = new(
        "MAGICMCP104",
        "MCP tools must belong to a Magic tool controller",
        "Tool method '{0}' must belong to a [McpServerToolType] class derived from MagicMcpToolController",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ToolMethodMustBeInstance = new(
        "MAGICMCP105",
        "MCP controller tools must be instance methods",
        "Tool method '{0}' must be an instance method because its controller is activated per invocation",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ControllerCannotBeSingleton = new(
        "MAGICMCP106",
        "MCP tool controllers cannot be singleton services",
        "Tool controller '{0}' cannot be registered with AddSingleton; inject singleton state into the controller instead",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ControllerRequiresTools = new(
        "MAGICMCP107",
        "MCP tool controllers must expose a tool",
        "Tool controller '{0}' does not contain any methods marked [McpServerTool]",
        "MagicAiGateway.MCP.Package",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        ToolTypeRequiresController,
        ControllerRequiresToolType,
        ControllerMustBeConcrete,
        ToolMethodRequiresController,
        ToolMethodMustBeInstance,
        ControllerCannotBeSingleton,
        ControllerRequiresTools
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;
        if (type.ToDisplayString() == ControllerBaseTypeName)
        {
            return;
        }

        bool hasToolTypeAttribute = HasAttribute(type, ToolTypeAttributeName);
        bool derivesFromController = DerivesFrom(type, ControllerBaseTypeName);

        if (hasToolTypeAttribute && !derivesFromController)
        {
            Report(context, ToolTypeRequiresController, type, type.Name);
            return;
        }

        if (derivesFromController && !hasToolTypeAttribute)
        {
            Report(context, ControllerRequiresToolType, type, type.Name);
            return;
        }

        if (!hasToolTypeAttribute || !derivesFromController)
        {
            return;
        }

        if (type.IsAbstract || type.IsStatic)
        {
            Report(context, ControllerMustBeConcrete, type, type.Name);
            return;
        }

        bool hasToolMethod = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(method => HasAttribute(method, ToolMethodAttributeName));

        if (!hasToolMethod)
        {
            Report(context, ControllerRequiresTools, type, type.Name);
        }
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        IMethodSymbol method = (IMethodSymbol)context.Symbol;
        if (!HasAttribute(method, ToolMethodAttributeName))
        {
            return;
        }

        INamedTypeSymbol containingType = method.ContainingType;
        bool validController =
            HasAttribute(containingType, ToolTypeAttributeName) &&
            DerivesFrom(containingType, ControllerBaseTypeName);

        if (!validController)
        {
            Report(context, ToolMethodRequiresController, method, method.Name);
            return;
        }

        if (method.IsStatic)
        {
            Report(context, ToolMethodMustBeInstance, method, method.Name);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol target = invocation.TargetMethod;

        if (target.Name != "AddSingleton" ||
            target.ContainingNamespace.ToDisplayString() != "Microsoft.Extensions.DependencyInjection")
        {
            return;
        }

        foreach (ITypeSymbol typeArgument in target.TypeArguments)
        {
            if (typeArgument is INamedTypeSymbol namedType &&
                DerivesFrom(namedType, ControllerBaseTypeName))
            {
                ReportSingleton(context, invocation, namedType);
                return;
            }
        }

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (argument.Value is ITypeOfOperation typeOfOperation &&
                typeOfOperation.TypeOperand is INamedTypeSymbol reflectedType &&
                DerivesFrom(reflectedType, ControllerBaseTypeName))
            {
                ReportSingleton(context, invocation, reflectedType);
                return;
            }

            if (argument.Value.Type is INamedTypeSymbol argumentType &&
                DerivesFrom(argumentType, ControllerBaseTypeName))
            {
                ReportSingleton(context, invocation, argumentType);
                return;
            }
        }
    }

    private static void ReportSingleton(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        INamedTypeSymbol controllerType)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            ControllerCannotBeSingleton,
            invocation.Syntax.GetLocation(),
            controllerType.Name));
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static bool DerivesFrom(INamedTypeSymbol type, string expectedBaseTypeName)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == expectedBaseTypeName)
            {
                return true;
            }
        }

        return false;
    }

    private static void Report(
        SymbolAnalysisContext context,
        DiagnosticDescriptor descriptor,
        ISymbol symbol,
        params object[] messageArguments)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            symbol.Locations.FirstOrDefault(),
            messageArguments));
    }
}
