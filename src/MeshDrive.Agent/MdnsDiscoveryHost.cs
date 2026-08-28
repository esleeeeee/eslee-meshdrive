using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;
using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed class MdnsDiscoveryHost : IDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly PeerDirectory _directory;
    private readonly int _port;
    private MulticastService? _mdns;
    private ServiceDiscovery? _serviceDiscovery;
    private ServiceProfile? _profile;
    private Timer? _expiryTimer;
    private bool _disposed;

    public MdnsDiscoveryHost(DeviceIdentity identity, PeerDirectory directory, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(directory);
        _identity = identity;
        _directory = directory;
        _port = port ?? DiscoveryNames.DefaultPort;
    }

    public bool IsRunning { get; private set; }

    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return true;
        }

        try
        {
            var selected = LanInterfaceSelector.Filter(MulticastService.GetNetworkInterfaces()).ToArray();
            var addresses = LanInterfaceSelector.Ipv4Addresses(selected);
            if (selected.Length == 0 || addresses.Length == 0)
            {
                return false;
            }

            _mdns = new MulticastService(LanInterfaceSelector.Filter) { UseIpv4 = true, UseIpv6 = false };
            _serviceDiscovery = new ServiceDiscovery(_mdns);
            _mdns.AnswerReceived += OnAnswerReceived;
            _serviceDiscovery.ServiceInstanceShutdown += OnInstanceShutdown;
            _mdns.Start();

            _profile = new ServiceProfile(
                _identity.DeviceId,
                DiscoveryNames.ServiceType,
                checked((ushort)_port),
                addresses);
            _profile.AddProperty(DiscoveryNames.TxtId, _identity.DeviceId);
            _profile.AddProperty(DiscoveryNames.TxtName, _identity.DeviceName);
            _profile.AddProperty(DiscoveryNames.TxtVersion, AppInfo.Version);
            _serviceDiscovery.Advertise(_profile);
            _serviceDiscovery.QueryServiceInstances(DiscoveryNames.ServiceType);
            _expiryTimer = new Timer(OnTimer, null, DiscoveryNames.QueryInterval, DiscoveryNames.QueryInterval);
            IsRunning = true;
            return true;
        }
        catch (Exception)
        {
            Cleanup();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cleanup();
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        try
        {
            var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToArray();
            foreach (var srv in records.OfType<SRVRecord>())
            {
                var fullName = srv.Name.ToString();
                if (!DiscoveryNames.IsMeshDriveService(fullName))
                {
                    continue;
                }

                var txt = records.OfType<TXTRecord>().FirstOrDefault(record => record.Name.ToString() == fullName);
                if (!DiscoveryTxt.TryRead(txt?.Strings, _identity.DeviceId, out var deviceId, out var name))
                {
                    continue;
                }

                var advertised = records.OfType<AddressRecord>()
                    .Where(record => record.Name.Equals(srv.Target))
                    .Select(record => record.Address);
                if (!DiscoveryTxt.TrySelectConnectionAddresses(
                        e.RemoteEndPoint?.Address,
                        advertised,
                        out var ipv4,
                        out var fallbacks))
                {
                    continue;
                }

                _directory.Upsert(new PeerSighting(deviceId, name, ipv4, srv.Port, fallbacks), DateTimeOffset.UtcNow);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        try
        {
            var deviceId = DiscoveryNames.InstanceDeviceId(e.ServiceInstanceName.ToString());
            _directory.MarkOffline(deviceId, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
        }
    }

    private void OnTimer(object? state)
    {
        _directory.Expire(DateTimeOffset.UtcNow);
        try
        {
            _serviceDiscovery?.QueryServiceInstances(DiscoveryNames.ServiceType);
        }
        catch (Exception)
        {
        }
    }

    private void Cleanup()
    {
        IsRunning = false;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        try
        {
            _serviceDiscovery?.Unadvertise();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        try
        {
            _serviceDiscovery?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        try
        {
            _mdns?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        _profile = null;
        _serviceDiscovery = null;
        _mdns = null;
    }
}
