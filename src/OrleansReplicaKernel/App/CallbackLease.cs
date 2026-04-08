using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.App;

public sealed class CallbackLease<THandle> : IAsyncDisposable
{
    private readonly Func<GrainId, ValueTask> _release;
    private int _disposed;

    public CallbackLease(
        GrainId grainId,
        THandle handle,
        Func<GrainId, ValueTask> release)
    {
        GrainId = grainId;
        Handle = handle;
        _release = release;
    }

    public GrainId GrainId { get; }

    public THandle Handle { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _release(GrainId);
    }
}
