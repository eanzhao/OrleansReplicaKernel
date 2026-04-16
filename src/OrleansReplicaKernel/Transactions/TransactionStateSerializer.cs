using System.Text.Json;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Transactions;

internal static class TransactionStateSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement Serialize<TState>(TState state)
        => JsonSerializer.SerializeToElement(state, JsonOptions);

    public static TState Deserialize<TState>(JsonElement? element)
    {
        if (element is null
            || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return StateValueFactory.Create<TState>();
        }

        var value = element.Value.Deserialize<TState>(JsonOptions);
        return value is null
            ? StateValueFactory.Create<TState>()
            : value;
    }

    public static TState Clone<TState>(TState state)
        => Deserialize<TState>(Serialize(state));
}
