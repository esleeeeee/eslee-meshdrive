using System.Net;
using System.Net.NetworkInformation;

namespace MeshDrive.Core;

public static class LanInterfaceSelector
{
    public static IEnumerable<NetworkInterface> Filter(IEnumerable<NetworkInterface> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var usable = candidates
            .Where(static nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(static nic => !IsExcludedType(nic.NetworkInterfaceType))
            .Where(static nic => nic.SupportsMulticast)
            .Where(HasUsableIpv4)
            .ToArray();

        // Ethernet과 Wi-Fi를 함께 유지한다. 한쪽만 고르면 같은 공유기의
        // 유선 Desktop과 무선 Laptop이 서로를 못 찾는다.
        var lan = usable.Where(static nic => IsLanType(nic.NetworkInterfaceType)).ToArray();
        return lan.Length > 0 ? lan : usable;
    }

    public static IPAddress[] Ipv4Addresses(IEnumerable<NetworkInterface> interfaces) =>
        interfaces
            .SelectMany(Ipv4Unicast)
            .Select(static item => item.Address)
            .Distinct()
            .ToArray();

    public static bool IsLanType(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.Wireless80211;

    public static bool IsExcludedType(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel;

    public static bool HasUsableIpv4(NetworkInterface nic)
    {
        ArgumentNullException.ThrowIfNull(nic);
        return Ipv4Unicast(nic).Any();
    }

    public static IEnumerable<UnicastIPAddressInformation> Ipv4Unicast(NetworkInterface nic)
    {
        ArgumentNullException.ThrowIfNull(nic);
        return nic.GetIPProperties().UnicastAddresses
            .Where(static item => DiscoveryTxt.IsUsableIpv4(item.Address));
    }
}
