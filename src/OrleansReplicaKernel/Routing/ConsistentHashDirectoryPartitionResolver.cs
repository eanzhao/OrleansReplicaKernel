using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public sealed class ConsistentHashDirectoryPartitionResolver
{
    public string SelectOwner(GrainId grainId, IClusterMembershipView membershipView)
    {
        ArgumentNullException.ThrowIfNull(membershipView);

        var candidates = membershipView.GetHealthyMembers();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No healthy directory owner candidate is available for grain '{grainId}'.");
        }

        string? selectedNodeName = null;
        ulong selectedScore = 0;

        foreach (var nodeName in candidates)
        {
            var score = ComputeScore(grainId, nodeName);
            if (selectedNodeName is null
                || score > selectedScore
                || (score == selectedScore
                    && string.CompareOrdinal(nodeName, selectedNodeName) < 0))
            {
                selectedNodeName = nodeName;
                selectedScore = score;
            }
        }

        return selectedNodeName!;
    }

    private static ulong ComputeScore(GrainId grainId, string nodeName)
    {
        var payload = Encoding.UTF8.GetBytes($"{grainId.GrainType}\n{grainId.Key}\n{nodeName}");
        var hash = SHA256.HashData(payload);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
