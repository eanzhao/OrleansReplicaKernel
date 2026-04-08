using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Demo;

// 这里保留的只是运行时句柄壳，真正的方法桩放在 `Demo/Generated` 下。
public sealed partial class EchoGrainReference
{
    private readonly IInvocationRuntime _runtime;
    private readonly GrainId _grainId;

    public EchoGrainReference(IInvocationRuntime runtime, GrainId grainId)
    {
        _runtime = runtime;
        _grainId = grainId;
    }
}
