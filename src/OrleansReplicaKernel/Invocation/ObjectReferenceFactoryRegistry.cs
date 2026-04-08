using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

public sealed class ObjectReferenceFactoryRegistry
{
    private readonly IReadOnlyDictionary<string, Func<IInvocationRuntime, GrainId, object>> _factoriesByInterfaceName;

    public ObjectReferenceFactoryRegistry(
        IReadOnlyDictionary<string, Func<IInvocationRuntime, GrainId, object>> factoriesByInterfaceName)
    {
        _factoriesByInterfaceName = factoriesByInterfaceName;
    }

    public TInterface Create<TInterface>(
        IInvocationRuntime runtime,
        GrainId grainId)
        where TInterface : class
    {
        var interfaceName = GetInterfaceName<TInterface>();
        if (!_factoriesByInterfaceName.TryGetValue(interfaceName, out var factory))
        {
            throw new InvalidOperationException(
                $"No object reference factory registered for '{interfaceName}'.");
        }

        return (TInterface)factory(runtime, grainId);
    }

    public TInterface Rehydrate<TInterface>(
        ObjectReferenceData data,
        IInvocationRuntime runtime)
        where TInterface : class
    {
        var expectedInterfaceName = GetInterfaceName<TInterface>();
        if (!string.Equals(data.InterfaceName, expectedInterfaceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Object reference interface mismatch. Expected '{expectedInterfaceName}', got '{data.InterfaceName}'.");
        }

        return Create<TInterface>(runtime, data.GrainId);
    }

    public bool HasFactory<TInterface>()
        where TInterface : class
        => _factoriesByInterfaceName.ContainsKey(GetInterfaceName<TInterface>());

    public static string GetInterfaceName<TInterface>()
        => typeof(TInterface).FullName ?? typeof(TInterface).Name;

    public static string GetInterfaceNameFromType(Type interfaceType)
        => interfaceType.FullName ?? interfaceType.Name;
}
