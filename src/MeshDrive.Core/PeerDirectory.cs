namespace MeshDrive.Core;

public sealed class PeerDirectory
{
    private readonly Dictionary<string, DiscoveredPeer> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _localDeviceId;
    private readonly TimeSpan _offlineAfter;

    public PeerDirectory(string localDeviceId, TimeSpan offlineAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDeviceId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(offlineAfter, TimeSpan.Zero);
        _localDeviceId = localDeviceId;
        _offlineAfter = offlineAfter;
    }

    public string LocalDeviceId => _localDeviceId;

    public void Upsert(PeerSighting sighting, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sighting);
        if (string.IsNullOrWhiteSpace(sighting.DeviceId) ||
            string.Equals(sighting.DeviceId, _localDeviceId, StringComparison.Ordinal))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(sighting.Name) ? sighting.DeviceId : sighting.Name.Trim();
        lock (_gate)
        {
            var existingTrust = _peers.TryGetValue(sighting.DeviceId, out var existing)
                ? existing.TrustState
                : TrustStates.Unpaired;
            _peers[sighting.DeviceId] = new DiscoveredPeer(
                sighting.DeviceId,
                name,
                sighting.Ipv4,
                sighting.Port,
                IsOnline: true,
                now,
                sighting.FallbackIpv4s ?? existing?.FallbackIpv4s,
                existingTrust);
        }
    }

    public void MarkOffline(string deviceId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.Equals(deviceId, _localDeviceId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            if (_peers.TryGetValue(deviceId, out var peer) && peer.IsOnline)
            {
                _peers[deviceId] = peer with { IsOnline = false, LastSeen = now };
            }
        }
    }

    public int Expire(DateTimeOffset now)
    {
        var cutoff = now - _offlineAfter;
        var changed = 0;
        lock (_gate)
        {
            foreach (var pair in _peers.ToArray())
            {
                if (pair.Value.IsOnline && pair.Value.LastSeen < cutoff)
                {
                    _peers[pair.Key] = pair.Value with { IsOnline = false };
                    changed++;
                }
            }
        }

        return changed;
    }

    public IReadOnlyList<DiscoveredPeer> Snapshot()
    {
        lock (_gate)
        {
            return _peers.Values
                .OrderBy(static peer => peer.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static peer => peer.DeviceId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
