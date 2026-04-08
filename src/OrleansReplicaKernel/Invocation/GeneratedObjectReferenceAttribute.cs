namespace OrleansReplicaKernel.Invocation;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GeneratedObjectReferenceAttribute : Attribute
{
    public GeneratedObjectReferenceAttribute(Type interfaceType)
    {
        if (!interfaceType.IsInterface)
        {
            throw new ArgumentException(
                $"Generated object reference interface '{interfaceType.Name}' must be an interface.",
                nameof(interfaceType));
        }

        InterfaceType = interfaceType;
    }

    public Type InterfaceType { get; }
}
