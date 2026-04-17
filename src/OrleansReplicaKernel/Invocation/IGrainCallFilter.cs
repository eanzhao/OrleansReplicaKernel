using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

public interface IIncomingGrainCallContext
{
    GrainId GrainId { get; }

    IInvokable Request { get; }

    object? Result { get; set; }

    Task InvokeAsync();
}

public interface IIncomingGrainCallFilter
{
    Task InvokeAsync(IIncomingGrainCallContext context);
}

public interface IOutgoingGrainCallContext
{
    GrainId TargetGrainId { get; }

    IInvokable Request { get; }

    object? Result { get; set; }

    Task InvokeAsync();
}

public interface IOutgoingGrainCallFilter
{
    Task InvokeAsync(IOutgoingGrainCallContext context);
}
