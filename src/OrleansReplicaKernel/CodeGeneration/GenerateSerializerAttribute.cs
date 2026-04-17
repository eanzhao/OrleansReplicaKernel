namespace OrleansReplicaKernel.CodeGeneration;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class GenerateSerializerAttribute : Attribute
{
}
