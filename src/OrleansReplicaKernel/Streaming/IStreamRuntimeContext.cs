namespace OrleansReplicaKernel.Streaming;

internal interface IStreamRuntimeContext
{
    IGrainStreamRuntime? GetStreamRuntime();
}
