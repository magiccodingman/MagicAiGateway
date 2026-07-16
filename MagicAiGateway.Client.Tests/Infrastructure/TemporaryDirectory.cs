namespace MagicAiGateway.Client.Tests.Infrastructure;

internal sealed class TemporaryDirectory : IDisposable
{
    private bool _disposed;

    public TemporaryDirectory(string purpose)
    {
        var invalidCharacters = System.IO.Path.GetInvalidFileNameChars();
        var safePurpose = string.Concat(purpose.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "magic-ai-gateway-client-tests",
            safePurpose,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide the assertion that produced the failure.
        }
    }
}
