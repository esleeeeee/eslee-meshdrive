namespace MeshDrive.Core;

public sealed record DiscoveredPeer(
    string DeviceId,
    string Name,
    string Ipv4,
    int Port,
    bool IsOnline,
    DateTimeOffset LastSeen,
    IReadOnlyList<string>? FallbackIpv4s = null,
    string TrustState = TrustStates.Unpaired)
{
    public IReadOnlyList<string> ConnectionIpv4s()
    {
        var addresses = new List<string>();
        AddUnique(addresses, Ipv4);
        if (FallbackIpv4s is not null)
        {
            foreach (var address in FallbackIpv4s)
            {
                AddUnique(addresses, address);
            }
        }

        return addresses;
    }

    private static void AddUnique(List<string> addresses, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        foreach (var existing in addresses)
        {
            if (string.Equals(existing, address, StringComparison.Ordinal))
            {
                return;
            }
        }

        addresses.Add(address);
    }
}

public sealed record PeerSighting(
    string DeviceId,
    string Name,
    string Ipv4,
    int Port,
    IReadOnlyList<string>? FallbackIpv4s = null);

public static class TrustStates
{
    public const string Unpaired = "unpaired";
    public const string Trusted = "trusted";
    public const string Pending = "pending";
}
