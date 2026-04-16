namespace OrleansReplicaKernel.CodeGeneration;

[AttributeUsage(AttributeTargets.Class)]
public sealed class MayInterleaveAttribute(string predicateMethodName) : Attribute
{
    public string PredicateMethodName { get; } = predicateMethodName;
}
