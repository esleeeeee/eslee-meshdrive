namespace MeshDrive.Core;

public static class DiscoveryNames
{
    public const string ServiceType = "_meshdrive._tcp";
    public const string ServiceTypeLocal = "_meshdrive._tcp.local";
    public const int DefaultPort = 41241;
    public const string TxtId = "id";
    public const string TxtName = "name";
    public const string TxtVersion = "v";
    public const string DiscoveryMdns = "mdns";
    public const string DiscoveryOff = "off";

    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(10);

    public static bool IsMeshDriveService(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        return fullName.Contains(ServiceType, StringComparison.OrdinalIgnoreCase);
    }

    public static string InstanceDeviceId(string serviceInstanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceInstanceName);
        var dot = serviceInstanceName.IndexOf('.', StringComparison.Ordinal);
        return dot <= 0 ? serviceInstanceName : serviceInstanceName[..dot];
    }
}
