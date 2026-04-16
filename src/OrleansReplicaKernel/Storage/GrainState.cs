namespace OrleansReplicaKernel.Storage;

public sealed class GrainState<TState> : IGrainState<TState>
{
    public GrainState()
        : this(StateValueFactory.Create<TState>(), eTag: null, recordExists: false)
    {
    }

    public GrainState(TState state, string? eTag, bool recordExists)
    {
        State = state;
        ETag = eTag;
        RecordExists = recordExists;
    }

    public TState State { get; set; }

    public string? ETag { get; set; }

    public bool RecordExists { get; set; }
}

internal static class StateValueFactory
{
    public static TState Create<TState>()
    {
        if (typeof(TState) == typeof(string))
        {
            return (TState)(object)string.Empty;
        }

        try
        {
            return Activator.CreateInstance<TState>();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"State type '{typeof(TState).FullName}' must be a value type, string, or expose a public parameterless constructor.",
                exception);
        }
    }
}
