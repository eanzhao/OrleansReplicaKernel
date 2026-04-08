using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessMembershipGossiper : IMembershipGossiper
{
    private readonly IClusterMembership _membership;
    private readonly IReadOnlyDictionary<string, GossipedClusterMembershipView> _membershipViews;
    private readonly IReadOnlyList<string> _orderedObserverNames;
    private readonly int _fanout;
    private readonly int _antiEntropyInterval;
    private readonly Dictionary<string, long> _lastDeliveredEpochs = new(StringComparer.Ordinal);
    private int _nextFanoutStartIndex;
    private int _tickNumber;

    public InProcessMembershipGossiper(
        IClusterMembership membership,
        IReadOnlyDictionary<string, GossipedClusterMembershipView> membershipViews,
        int fanout,
        int antiEntropyInterval,
        MembershipDisseminationCheckpoint? checkpoint = null)
    {
        _membership = membership;
        _membershipViews = membershipViews;
        _orderedObserverNames = membershipViews.Keys
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        _fanout = Math.Max(1, fanout);
        _antiEntropyInterval = Math.Max(1, antiEntropyInterval);

        if (checkpoint is null)
        {
            return;
        }

        _tickNumber = checkpoint.TickNumber;
        _nextFanoutStartIndex = checkpoint.NextFanoutStartIndex;

        foreach (var observer in checkpoint.Observers)
        {
            _lastDeliveredEpochs[observer.ObserverNodeName] = observer.LastDeliveredEpoch;
        }
    }

    public IReadOnlyList<MembershipGossipDelivery> Gossip()
    {
        _tickNumber++;
        var currentEpoch = _membership.CurrentEpoch;
        var isAntiEntropyRound = _tickNumber == 1 || _tickNumber % _antiEntropyInterval == 0;
        var mode = isAntiEntropyRound ? "anti-entropy" : "fanout";
        var selectedObserverNames = SelectObservers(currentEpoch, isAntiEntropyRound);
        var deliveries = new List<MembershipGossipDelivery>();

        foreach (var observerNodeName in selectedObserverNames)
        {
            var membershipView = _membershipViews[observerNodeName];
            _lastDeliveredEpochs.TryGetValue(observerNodeName, out var fromEpoch);
            var changes = _membership.GetViewChangesSince(fromEpoch);
            var result = membershipView.ApplyGossip(changes);
            var toEpoch = changes.Count > 0 ? changes[^1].Epoch : fromEpoch;

            _lastDeliveredEpochs[observerNodeName] = Math.Max(fromEpoch, toEpoch);

            if (result.ConsumedChanges == 0 && result.StabilizedNodes == 0)
            {
                continue;
            }

            TraceLog.Write(
                "gossip",
                $"tick={_tickNumber} mode={mode} observer={observerNodeName} epochs={fromEpoch + 1}-{Math.Max(fromEpoch, toEpoch)} consumed={result.ConsumedChanges} stabilized={result.StabilizedNodes} epoch={membershipView.CurrentEpoch}");

            deliveries.Add(
                new MembershipGossipDelivery(
                    observerNodeName,
                    fromEpoch,
                    Math.Max(fromEpoch, toEpoch),
                    result.ConsumedChanges,
                    result.StabilizedNodes,
                    mode,
                    _tickNumber));
        }

        foreach (var pair in _membershipViews.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (selectedObserverNames.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }

            var result = pair.Value.RunStabilizationTick();
            if (result.StabilizedNodes == 0)
            {
                continue;
            }

            _lastDeliveredEpochs.TryGetValue(pair.Key, out var epoch);
            TraceLog.Write(
                "gossip",
                $"tick={_tickNumber} mode=stabilization observer={pair.Key} epochs={epoch} consumed=0 stabilized={result.StabilizedNodes} epoch={pair.Value.CurrentEpoch}");
            deliveries.Add(
                new MembershipGossipDelivery(
                    pair.Key,
                    epoch,
                    epoch,
                    0,
                    result.StabilizedNodes,
                    "stabilization",
                    _tickNumber));
        }

        return deliveries;
    }

    public MembershipDisseminationCheckpoint ExportCheckpoint()
    {
        return new MembershipDisseminationCheckpoint(
            _tickNumber,
            _nextFanoutStartIndex,
            _orderedObserverNames
                .Select(observerNodeName => new MembershipObserverCursor(
                    observerNodeName,
                    GetLastDeliveredEpoch(observerNodeName)))
                .ToArray());
    }

    private IReadOnlyList<string> SelectObservers(long currentEpoch, bool isAntiEntropyRound)
    {
        var laggingObservers = _orderedObserverNames
            .Where(observerNodeName => GetLastDeliveredEpoch(observerNodeName) < currentEpoch)
            .ToArray();

        if (isAntiEntropyRound)
        {
            return laggingObservers;
        }

        var candidateObservers = laggingObservers.Length > 0
            ? laggingObservers
            : _orderedObserverNames.ToArray();

        if (_fanout >= candidateObservers.Length)
        {
            return candidateObservers;
        }

        var selected = new List<string>(_fanout);
        for (var offset = 0; offset < _fanout; offset++)
        {
            var index = (_nextFanoutStartIndex + offset) % candidateObservers.Length;
            selected.Add(candidateObservers[index]);
        }

        _nextFanoutStartIndex = (_nextFanoutStartIndex + _fanout) % candidateObservers.Length;
        return selected;
    }

    private long GetLastDeliveredEpoch(string observerNodeName)
        => _lastDeliveredEpochs.TryGetValue(observerNodeName, out var lastDeliveredEpoch)
            ? lastDeliveredEpoch
            : 0;
}
