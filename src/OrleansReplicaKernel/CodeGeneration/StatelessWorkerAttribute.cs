namespace OrleansReplicaKernel.CodeGeneration;

/// <summary>
/// Marks a grain implementation as a stateless worker grain that may use multiple local activations.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StatelessWorkerAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StatelessWorkerAttribute"/> class.
    /// </summary>
    public StatelessWorkerAttribute()
        : this(maxLocalWorkers: 0)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StatelessWorkerAttribute"/> class with an explicit local worker limit.
    /// </summary>
    /// <param name="maxLocalWorkers">The maximum number of local worker activations to create for a grain key on one node.</param>
    public StatelessWorkerAttribute(int maxLocalWorkers)
    {
        if (maxLocalWorkers < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLocalWorkers),
                "Stateless worker local worker limit must be non-negative.");
        }

        MaxLocalWorkers = maxLocalWorkers > 0
            ? maxLocalWorkers
            : Environment.ProcessorCount;
    }

    /// <summary>
    /// Gets the maximum number of local worker activations to create for a grain key on one node.
    /// </summary>
    public int MaxLocalWorkers { get; }
}
