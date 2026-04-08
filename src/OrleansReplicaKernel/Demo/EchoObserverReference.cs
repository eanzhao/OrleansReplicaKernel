using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Demo;

public sealed partial class EchoObserverReference : IObjectReference
{
    private readonly IInvocationRuntime _fallbackRuntime;
    private readonly GrainId _grainId;

    public EchoObserverReference(IInvocationRuntime fallbackRuntime, GrainId grainId)
    {
        _fallbackRuntime = fallbackRuntime;
        _grainId = grainId;
    }

    public GrainId GrainId => _grainId;
}
