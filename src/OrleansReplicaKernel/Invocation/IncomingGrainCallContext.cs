using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

internal sealed class IncomingGrainCallContext : IIncomingGrainCallContext
{
    private readonly IReadOnlyList<IIncomingGrainCallFilter> _filters;
    private readonly object _target;
    private int _currentFilterIndex;

    public IncomingGrainCallContext(
        GrainId grainId,
        IInvokable request,
        object target,
        IReadOnlyList<IIncomingGrainCallFilter> filters)
    {
        GrainId = grainId;
        Request = request;
        _target = target;
        _filters = filters;
    }

    public GrainId GrainId { get; }

    public IInvokable Request { get; }

    public object? Result { get; set; }

    public async Task InvokeAsync()
    {
        if (_currentFilterIndex < _filters.Count)
        {
            var filter = _filters[_currentFilterIndex];
            _currentFilterIndex++;
            await filter.InvokeAsync(this);
        }
        else
        {
            Result = await Request.InvokeAsync(_target, CancellationToken.None);
        }
    }
}
