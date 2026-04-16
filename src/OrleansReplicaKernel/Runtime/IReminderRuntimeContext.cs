using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Reminders;

namespace OrleansReplicaKernel.Runtime;

internal interface IReminderRuntimeContext
{
    IGrainReminderRegistry? GetReminderRegistry(GrainId grainId);
}
