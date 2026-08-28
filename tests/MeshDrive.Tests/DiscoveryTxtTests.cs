using System.Net;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class DiscoveryTxtTests
{
    [TestMethod]
    public void ReadsMeshDriveTxtAndSkipsSelf()
    {
        Assert.IsTrue(DiscoveryNames.IsMeshDriveService("abc._meshdrive._tcp.local"));
        Assert.IsTrue(DiscoveryNames.IsMeshDriveService("_meshdrive._tcp.local."));
        Assert.IsFalse(DiscoveryNames.IsMeshDriveService("_eslee-quicksend._tcp.local"));
        Assert.AreEqual("abc123", DiscoveryNames.InstanceDeviceId("abc123._meshdrive._tcp.local."));

        Assert.IsTrue(DiscoveryTxt.TryRead(
            ["id=laptop1", "name=Office Laptop", "v=0.0.2"],
            "desktop1",
            out var deviceId,
            out var name));
        Assert.AreEqual("laptop1", deviceId);
        Assert.AreEqual("Office Laptop", name);

        Assert.IsFalse(DiscoveryTxt.TryRead(
            ["id=desktop1", "name=This PC"],
            "desktop1",
            out _,
            out _));
        Assert.IsFalse(DiscoveryTxt.TryRead(["name=only-name"], "desktop1", out _, out _));
    }

    [TestMethod]
    public void SelectsUsableIpv4AndIgnoresLoopbackAndApipa()
    {
        Assert.IsTrue(DiscoveryTxt.IsUsableIpv4(IPAddress.Parse("192.168.0.12")));
        Assert.IsTrue(DiscoveryTxt.IsUsableIpv4(IPAddress.Parse("10.0.0.8")));
        Assert.IsFalse(DiscoveryTxt.IsUsableIpv4(IPAddress.Loopback));
        Assert.IsFalse(DiscoveryTxt.IsUsableIpv4(IPAddress.Parse("169.254.10.4")));
        Assert.IsFalse(DiscoveryTxt.IsUsableIpv4(IPAddress.IPv6Loopback));

        Assert.IsTrue(DiscoveryTxt.TrySelectConnectionAddresses(
            IPAddress.Parse("10.0.0.8"),
            [IPAddress.Parse("192.168.0.5"), IPAddress.Parse("10.0.0.8")],
            out var primary,
            out var fallbacks));
        Assert.AreEqual("10.0.0.8", primary);
        Assert.HasCount(1, fallbacks);
        Assert.AreEqual("192.168.0.5", fallbacks[0]);

        Assert.IsTrue(DiscoveryTxt.TrySelectIpv4(
            [IPAddress.IPv6Loopback, IPAddress.Parse("192.168.0.5")],
            fallback: IPAddress.Parse("10.0.0.1"),
            out var ipv4));
        Assert.AreEqual("10.0.0.1", ipv4);

        Assert.IsTrue(DiscoveryTxt.TrySelectIpv4(
            [IPAddress.Loopback],
            fallback: IPAddress.Parse("192.168.0.9"),
            out ipv4));
        Assert.AreEqual("192.168.0.9", ipv4);

        Assert.IsFalse(DiscoveryTxt.TrySelectIpv4(
            [IPAddress.Loopback, IPAddress.Parse("169.254.1.1")],
            fallback: IPAddress.IPv6Any,
            out _));
    }
}
