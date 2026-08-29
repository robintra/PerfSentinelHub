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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // The hook exists because the class is inheritable, not because a subclass
    // needs it today: neither of the two holds a field of its own. Without it a
    // third one could only hide this Dispose, and its own field would leak.
    // ReSharper disable once VirtualMemberNeverOverridden.Global
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
    }
}
