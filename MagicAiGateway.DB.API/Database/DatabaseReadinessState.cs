namespace MagicAiGateway.DB.API.Database;

public sealed class DatabaseReadinessState
{
    private readonly object _sync = new();
    private bool _ready;
    private string? _error;

    public bool IsReady { get { lock (_sync) return _ready; } }
    public string? Error { get { lock (_sync) return _error; } }

    public void Ready()
    {
        lock (_sync)
        {
            _ready = true;
            _error = null;
        }
    }

    public void Failed(Exception exception)
    {
        lock (_sync)
        {
            _ready = false;
            _error = exception.Message;
        }
    }
}
