namespace OrleansReplicaKernel.Streaming;

internal sealed record MemoryStreamProviderConfiguration(
    string ProviderName,
    int MaxBatchSize,
    TimeSpan DispatchInterval);
