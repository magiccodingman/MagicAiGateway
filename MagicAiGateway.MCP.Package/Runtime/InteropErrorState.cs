using System.Text;

namespace MagicAiGateway.MCP.Package.Runtime;

internal static class InteropErrorState
{
    [ThreadStatic]
    private static string? _lastError;

    public static void Clear() => _lastError = null;

    public static void Set(string message) => _lastError = message;

    public static void Set(Exception exception) =>
        _lastError = $"{exception.GetType().Name}: {exception.Message}";

    public static byte[] GetUtf8() => Encoding.UTF8.GetBytes(_lastError ?? string.Empty);
}
