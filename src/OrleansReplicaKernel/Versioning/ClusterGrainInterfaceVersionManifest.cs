using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Versioning;

public sealed class ClusterGrainInterfaceVersionManifest
{
    private readonly IGrainInterfaceVersionTable _table;

    public ClusterGrainInterfaceVersionManifest(IGrainInterfaceVersionTable table)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
    }

    public void Register(string nodeName, IReadOnlyCollection<GrainInterfaceVersionDescriptor> supportedInterfaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(supportedInterfaces);

        var normalizedInterfaces = supportedInterfaces
            .Distinct()
            .OrderBy(item => item.GrainType, StringComparer.Ordinal)
            .ThenBy(item => item.CompatibilityFamily, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .ToArray();

        while (true)
        {
            var snapshot = _table.ReadAsync().GetAwaiter().GetResult();
            var existing = snapshot.Checkpoint.Nodes
                .FirstOrDefault(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal));
            if (existing is not null
                && existing.SupportedInterfaces.SequenceEqual(normalizedInterfaces))
            {
                return;
            }

            var updatedNode = new NodeGrainInterfaceVersionManifest(nodeName, normalizedInterfaces);
            var checkpoint = new GrainInterfaceVersionManifestCheckpoint(
                snapshot.Checkpoint.Nodes
                    .Where(item => !string.Equals(item.NodeName, nodeName, StringComparison.Ordinal))
                    .Append(updatedNode)
                    .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                    .ToArray());

            if (_table.WriteAsync(new GrainInterfaceVersionTableWriteRequest(snapshot.Version, checkpoint))
                .GetAwaiter()
                .GetResult())
            {
                return;
            }
        }
    }

    public bool Supports(string nodeName, GrainInterfaceVersionDescriptor requestedInterface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(requestedInterface);

        var snapshot = _table.ReadAsync().GetAwaiter().GetResult();
        var familyKnownToCluster = snapshot.Checkpoint.Nodes
            .SelectMany(item => item.SupportedInterfaces)
            .Any(item =>
                string.Equals(item.GrainType, requestedInterface.GrainType, StringComparison.Ordinal)
                && string.Equals(item.CompatibilityFamily, requestedInterface.CompatibilityFamily, StringComparison.Ordinal));
        if (!familyKnownToCluster)
        {
            return true;
        }

        var node = snapshot.Checkpoint.Nodes
            .FirstOrDefault(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal));
        if (node is null)
        {
            return true;
        }

        return node.SupportedInterfaces.Contains(requestedInterface);
    }

    public IReadOnlyList<string> GetCompatibleHealthyMembers(
        IClusterMembershipView membershipView,
        GrainInterfaceVersionDescriptor requestedInterface)
    {
        ArgumentNullException.ThrowIfNull(membershipView);
        ArgumentNullException.ThrowIfNull(requestedInterface);

        return membershipView.GetHealthyMembers()
            .Where(nodeName => Supports(nodeName, requestedInterface))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }
}
