using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationMessage(
    Guid RequestId,
    Guid RequestChainId,
    Guid AttemptId,
    int AttemptSequence,
    string SourceNodeName,
    GrainAddress Target,
    IInvokable Invokable,
    InvocationSourceKind SourceKind = InvocationSourceKind.ClusterNode,
    TransactionInfo? Transaction = null);
