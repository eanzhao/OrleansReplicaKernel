using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Runtime;

public interface IGrainExtensionContext
{
    GrainId GrainId { get; }

    ValueTask<IPersistentState<TState>> GetPersistentStateAsync<TState>(
        string stateName,
        string? storageName = null,
        CancellationToken cancellationToken = default);

    ValueTask<ITransactionalState<TState>> GetTransactionalStateAsync<TState>(
        string stateName,
        string? storageName = null,
        CancellationToken cancellationToken = default);
}
