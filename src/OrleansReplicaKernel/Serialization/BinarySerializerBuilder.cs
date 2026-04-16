using System.Reflection;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Serialization;

public sealed class BinarySerializerBuilder
{
    private readonly List<IBinaryCodec> _codecs = [];
    private readonly HashSet<Type> _codecTypes = [];

    public BinarySerializerBuilder()
    {
        AddDefaultCodecs();
    }

    public BinarySerializerBuilder AddCodec(IBinaryCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (_codecTypes.Add(codec.GetType()))
        {
            _codecs.Add(codec);
        }

        return this;
    }

    public BinarySerializerBuilder AddCodec<T>(IBinaryCodec<T> codec) => AddCodec((IBinaryCodec)codec);

    public BinarySerializerBuilder AddCodecsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes()
                     .Where(type =>
                         typeof(IBinaryCodec).IsAssignableFrom(type)
                         && !type.IsAbstract
                         && !type.ContainsGenericParameters))
        {
            if (Activator.CreateInstance(type, nonPublic: true) is IBinaryCodec codec)
            {
                AddCodec(codec);
            }
        }

        return this;
    }

    public BinarySerializer Build() => new(_codecs);

    private void AddDefaultCodecs()
    {
        AddCodec(new BooleanBinaryCodec());
        AddCodec(new ByteBinaryCodec());
        AddCodec(new Int32BinaryCodec());
        AddCodec(new Int64BinaryCodec());
        AddCodec(new StringBinaryCodec());
        AddCodec(new GuidBinaryCodec());
        AddCodec(new DateTimeOffsetBinaryCodec());
        AddCodec(new ExceptionBinaryCodec());
        AddCodec(new InvalidOperationExceptionBinaryCodec());
        AddCodec(new ActivationQuiescingExceptionBinaryCodec());
        AddCodec(new StaleGrainAddressExceptionBinaryCodec());
        AddCodec(new RemoteNodeUnavailableExceptionBinaryCodec());
        AddCodec(new ResponseDeliveryExceptionBinaryCodec());
        AddCodec(new GrainIdBinaryCodec());
        AddCodec(new GrainAddressBinaryCodec());
        AddCodec(new ObjectReferenceDataBinaryCodec());
        AddCodec(new TransactionInfoBinaryCodec());
        AddCodec(new InvocationMessageBinaryCodec());
        AddCodec(new InvocationResponseMessageBinaryCodec());
    }
}
