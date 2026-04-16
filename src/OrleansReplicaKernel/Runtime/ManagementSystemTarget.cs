using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Runtime;

public sealed class ManagementSystemTarget : ISystemTarget
{
    public const string TargetType = "management";

    private readonly IClusterMembershipView _membershipView;

    public ManagementSystemTarget(IClusterMembershipView membershipView)
    {
        _membershipView = membershipView ?? throw new ArgumentNullException(nameof(membershipView));
    }

    public static SystemTargetId CreateId(string nodeName) => new(TargetType, nodeName);

    public string GetMembershipViewSummary()
    {
        var members = _membershipView.GetMembers()
            .OrderBy(item => item.NodeName, StringComparer.Ordinal)
            .Select(item => $"{item.NodeName}:stable={item.StableStatus}/observed={item.ObservedStatus}@{item.LastObservedEpoch}")
            .ToArray();

        var memberSummary = members.Length == 0
            ? "<empty>"
            : string.Join(", ", members);

        return $"observer={_membershipView.ObserverNodeName} epoch={_membershipView.CurrentEpoch} members={memberSummary}";
    }
}
