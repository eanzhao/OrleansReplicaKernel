namespace OrleansReplicaKernel.Invocation;

public interface IInvokable
{
    string InterfaceName { get; }

    string InterfaceCompatibilityFamily { get; }

    int InterfaceVersion { get; }

    string MethodName { get; }

    ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken);
}
