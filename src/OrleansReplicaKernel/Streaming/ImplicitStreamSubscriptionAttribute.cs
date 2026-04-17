namespace OrleansReplicaKernel.Streaming;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ImplicitStreamSubscriptionAttribute : Attribute
{
    public ImplicitStreamSubscriptionAttribute(string providerName, string streamNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamNamespace);
        ProviderName = providerName;
        StreamNamespace = streamNamespace;
    }

    public string ProviderName { get; }

    public string StreamNamespace { get; }
}
