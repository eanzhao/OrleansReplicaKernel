using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationMessage(
    Guid RequestId,
    Guid RequestChainId,
    Guid AttemptId,
    int AttemptSequence,
    DateTimeOffset CreatedUtc,
    string SourceNodeName,
    GrainAddress Target,
    IInvokable Invokable,
    InvocationSourceKind SourceKind = InvocationSourceKind.ClusterNode,
    InvocationIdentity? Identity = null,
    TransactionInfo? Transaction = null,
    string? TraceParent = null,
    string? TraceState = null,
    TimeSpan? TimeToLive = null);
