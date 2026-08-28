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
        foreach (var address in addresses)
        {
            if (IsUsableIpv4(address))
            {
                ipv4 = address.ToString();
                return true;
            }
        }

        if (fallback is not null && IsUsableIpv4(fallback))
        {
            ipv4 = fallback.ToString();
            return true;
        }

        ipv4 = string.Empty;
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
