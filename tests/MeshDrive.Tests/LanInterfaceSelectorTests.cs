using System.Net.NetworkInformation;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class LanInterfaceSelectorTests
{
    [TestMethod]
    public void EthernetAndWifiAreBothLanTypes()
    {
        Assert.IsTrue(LanInterfaceSelector.IsLanType(NetworkInterfaceType.Ethernet));
        Assert.IsTrue(LanInterfaceSelector.IsLanType(NetworkInterfaceType.GigabitEthernet));
        Assert.IsTrue(LanInterfaceSelector.IsLanType(NetworkInterfaceType.Wireless80211));
        Assert.IsFalse(LanInterfaceSelector.IsLanType(NetworkInterfaceType.Loopback));
        Assert.IsFalse(LanInterfaceSelector.IsLanType(NetworkInterfaceType.Tunnel));
        Assert.IsTrue(LanInterfaceSelector.IsExcludedType(NetworkInterfaceType.Loopback));
        Assert.IsTrue(LanInterfaceSelector.IsExcludedType(NetworkInterfaceType.Tunnel));
        Assert.IsFalse(LanInterfaceSelector.IsExcludedType(NetworkInterfaceType.Ethernet));
        Assert.IsFalse(LanInterfaceSelector.IsExcludedType(NetworkInterfaceType.Wireless80211));
    }

    [TestMethod]
    public void FilterKeepsEthernetAndWifiTogether()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        var selected = LanInterfaceSelector.Filter(all).ToArray();
        foreach (var nic in selected)
        {
            Assert.AreEqual(OperationalStatus.Up, nic.OperationalStatus);
            Assert.IsFalse(LanInterfaceSelector.IsExcludedType(nic.NetworkInterfaceType));
            Assert.IsTrue(nic.SupportsMulticast);
            Assert.IsTrue(LanInterfaceSelector.HasUsableIpv4(nic));
        }

        var eligibleLan = all
            .Where(static nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(static nic => !LanInterfaceSelector.IsExcludedType(nic.NetworkInterfaceType))
            .Where(static nic => nic.SupportsMulticast)
            .Where(LanInterfaceSelector.HasUsableIpv4)
            .Where(static nic => LanInterfaceSelector.IsLanType(nic.NetworkInterfaceType))
            .ToArray();
        if (eligibleLan.Any(static nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            Assert.IsTrue(
                selected.Any(static nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211),
                "Wi-Fi 인터페이스가 있는데 Ethernet 때문에 제외되면 안 됩니다.");
        }

        if (eligibleLan.Any(IsEthernetFamily))
        {
            Assert.IsTrue(
                selected.Any(IsEthernetFamily),
                "Ethernet 인터페이스가 있는데 Wi-Fi 때문에 제외되면 안 됩니다.");
        }
    }

    private static bool IsEthernetFamily(NetworkInterface nic) =>
        nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet;
}
