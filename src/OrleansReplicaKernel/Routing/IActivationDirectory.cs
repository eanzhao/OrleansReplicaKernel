using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public interface IActivationDirectory : IAsyncDisposable
{
    ActivationEntry GetOrCreate(GrainAddress address);

    ValueTask<ActivationHandoffRecord?> PrepareHandoffAsync(GrainAddress address);

    void StageHandoffState(GrainAddress address, ActivationHandoffRecord handoffState);

    void Fence(GrainAddress address);

    ValueTask<bool> DeactivateAsync(
        GrainAddress address,
        ActivationDeactivationReason reason = ActivationDeactivationReason.Explicit);

    ValueTask<int> CollectIdleAsync(TimeSpan idleFor);

    ValueTask<int> DeactivateAllAsync(
        ActivationDeactivationReason reason = ActivationDeactivationReason.Shutdown);

    ActivationDirectoryCheckpoint ExportCheckpoint(string nodeName);
}
