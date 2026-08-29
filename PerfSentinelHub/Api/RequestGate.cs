namespace PerfSentinelHub.Api;

/// <summary>
/// Bounds how many requests of one kind run at once. Refusal is TryEnter
/// answering false, which callers turn into a 503 with Retry-After: the work
/// behind each gate buffers real memory, so waiting in line would only move
/// the pressure inside.
/// </summary>
public abstract class RequestGate(int maxConcurrent) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(maxConcurrent, maxConcurrent);

    public bool TryEnter() => _gate.Wait(0);

    public void Exit() => _gate.Release();

    // One managed field and no finalizer, so there is no second Dispose to
    // route through and no subclass has ever had anything to add to it.
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
