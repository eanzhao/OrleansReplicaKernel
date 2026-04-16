using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Transactions;

internal sealed class TransactionInfoBinaryCodec : BinaryObjectCodec<TransactionInfo>
{
    public override string Alias => "orleans.transaction.info";

    protected override TransactionInfo ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        Guid? transactionId = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            if (fieldId == 1)
            {
                transactionId = serializer.Read<Guid>(payload);
            }
        }

        return new TransactionInfo(BinaryCodecRequired.Require(transactionId, nameof(TransactionInfo.TransactionId)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, TransactionInfo value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.TransactionId, serializer);
    }
}
