using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Demo;

// 同样只保留壳，具体调用桩放到生成区。
public sealed partial class CounterGrainReference
{
    private readonly IInvocationRuntime _runtime;
    private readonly GrainId _grainId;

    public CounterGrainReference(IInvocationRuntime runtime, GrainId grainId)
    {
        _runtime = runtime;
        _grainId = grainId;
    }
}
