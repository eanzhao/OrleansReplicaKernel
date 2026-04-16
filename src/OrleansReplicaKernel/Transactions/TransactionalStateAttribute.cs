namespace OrleansReplicaKernel.Transactions;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class TransactionalStateAttribute : Attribute
{
    public TransactionalStateAttribute()
    {
    }

    public TransactionalStateAttribute(string stateName)
    {
        StateName = stateName;
    }

    public TransactionalStateAttribute(string stateName, string storageName)
        : this(stateName)
    {
        StorageName = storageName;
    }

    public string? StateName { get; }

    public string? StorageName { get; }
}
