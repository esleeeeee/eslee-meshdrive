namespace MeshDrive.Core;

public sealed record DiscoveredPeer(
    string DeviceId,
    string Name,
    string Ipv4,
    int Port,
    bool IsOnline,
    DateTimeOffset LastSeen);

public sealed record PeerSighting(
    string DeviceId,
    string Name,
    string Ipv4,
    int Port);
