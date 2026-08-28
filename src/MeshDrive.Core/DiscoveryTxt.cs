using System.Net;
using System.Net.Sockets;

namespace MeshDrive.Core;

public static class DiscoveryTxt
{
    public static bool TryRead(
        IEnumerable<string>? txtStrings,
        string localDeviceId,
        out string deviceId,
        out string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDeviceId);
        deviceId = string.Empty;
        name = string.Empty;
        var properties = Parse(txtStrings);
        if (!properties.TryGetValue(DiscoveryNames.TxtId, out var id) || !DeviceIdentityStore.IsUsableDeviceId(id))
        {
            return false;
        }

        if (string.Equals(id, localDeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        deviceId = id;
        if (properties.TryGetValue(DiscoveryNames.TxtName, out var advertised) &&
            !string.IsNullOrWhiteSpace(advertised))
        {
            name = advertised.Trim();
        }
        else
        {
            name = id;
        }

        return true;
    }

    public static Dictionary<string, string> Parse(IEnumerable<string>? txtStrings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (txtStrings is null)
        {
            return result;
        }

        foreach (var value in txtStrings)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var split = value.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
            {
                continue;
            }

            result[value[..split]] = value[(split + 1)..];
        }

        return result;
    }

    public static bool TrySelectIpv4(IEnumerable<IPAddress> addresses, IPAddress? fallback, out string ipv4)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (TrySelectConnectionAddresses(fallback, addresses, out var primary, out _))
        {
            ipv4 = primary;
            return true;
        }

        ipv4 = string.Empty;
        return false;
    }

    /// <summary>
    /// mDNS 패킷을 실제로 보낸 상대 IPv4를 연결 주소로 우선하고,
    /// 광고된 다른 IPv4는 fallback으로 둔다.
    /// </summary>
    public static bool TrySelectConnectionAddresses(
        IPAddress? packetSource,
        IEnumerable<IPAddress> advertised,
        out string primary,
        out IReadOnlyList<string> fallbacks)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        var advertisedUsable = new List<string>();
        foreach (var address in advertised)
        {
            if (IsUsableIpv4(address))
            {
                var text = address.ToString();
                if (!advertisedUsable.Contains(text, StringComparer.Ordinal))
                {
                    advertisedUsable.Add(text);
                }
            }
        }

        if (packetSource is not null && IsUsableIpv4(packetSource))
        {
            var sourceText = packetSource.ToString();
            primary = sourceText;
            fallbacks = advertisedUsable
                .Where(address => !string.Equals(address, sourceText, StringComparison.Ordinal))
                .ToArray();
            return true;
        }

        if (advertisedUsable.Count > 0)
        {
            primary = advertisedUsable[0];
            fallbacks = advertisedUsable.Skip(1).ToArray();
            return true;
        }

        primary = string.Empty;
        fallbacks = [];
        return false;
    }

    public static bool IsUsableIpv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length != 4 || bytes[0] != 169 || bytes[1] != 254;
    }
}
