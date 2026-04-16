using System.Text;
using System.Text.Json;

namespace OrleansReplicaKernel.Streaming;

public readonly record struct StreamId(
    string ProviderName,
    string Namespace,
    string Key)
{
    public string ToStableKey()
        => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(this)));

    public override string ToString() => $"{ProviderName}:{Namespace}/{Key}";
}
