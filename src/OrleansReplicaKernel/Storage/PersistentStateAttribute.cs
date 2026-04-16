namespace OrleansReplicaKernel.Storage;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class PersistentStateAttribute : Attribute
{
    public PersistentStateAttribute()
    {
    }

    public PersistentStateAttribute(string stateName)
    {
        StateName = stateName;
    }

    public PersistentStateAttribute(string stateName, string storageName)
        : this(stateName)
    {
        StorageName = storageName;
    }

    public string? StateName { get; }

    public string? StorageName { get; }
}
