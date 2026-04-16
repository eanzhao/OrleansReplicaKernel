namespace OrleansReplicaKernel.CodeGeneration;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CollectionAgeLimitAttribute : Attribute
{
    public CollectionAgeLimitAttribute(int milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Collection age limit must be non-negative.");
        }

        Milliseconds = milliseconds;
    }

    public int Milliseconds { get; }
}
