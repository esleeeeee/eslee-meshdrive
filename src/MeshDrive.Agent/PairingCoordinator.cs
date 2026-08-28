using System.Security.Cryptography.X509Certificates;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class PairingCoordinator
{
    private readonly DeviceIdentity _identity;
    private readonly DeviceCredential _credential;
    private readonly TrustedPeerStore _trust;
    private readonly PeerDirectory _directory;
    private readonly PeerHttpsClient _https;
    private readonly int _listenPort;
    private readonly object _gate = new();
    private PairingSession? _session;
    private string[] _peerAddresses = [];
    private int _peerPort;
    private readonly Dictionary<string, (string[] Addresses, int Port)> _endpoints = new(StringComparer.Ordinal);

    public PairingCoordinator(
        DeviceIdentity identity,
        DeviceCredential credential,
        TrustedPeerStore trust,
        PeerDirectory directory,
        int listenPort)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(directory);
        _identity = identity;
        _credential = credential;
        _trust = trust;
        _directory = directory;
        _listenPort = listenPort;
        _https = new PeerHttpsClient(credential);
    }

    public IReadOnlyList<DiscoveredPeer> ListPeers()
    {
        PairingSnapshot? pairing;
        lock (_gate)
        {
            pairing = _session?.Snapshot(DateTimeOffset.UtcNow);
        }

        return _directory.Snapshot()
            .Select(peer =>
            {
                var trust = _trust.TryGetFingerprint(peer.DeviceId, out _)
                    ? TrustStates.Trusted
                    : pairing is not null &&
                      string.Equals(pairing.PeerDeviceId, peer.DeviceId, StringComparison.Ordinal) &&
                      string.Equals(pairing.Status, "waiting", StringComparison.OrdinalIgnoreCase)
                        ? TrustStates.Pending
                        : TrustStates.Unpaired;
                return peer with { TrustState = trust };
            })
            .ToArray();
    }

    public PairingSnapshot? CurrentPairing()
    {
        lock (_gate)
        {
            return _session?.Snapshot(DateTimeOffset.UtcNow);
        }
    }

    public IReadOnlyList<TrustedPeer> ListTrusted() => _trust.Snapshot();

    public async Task<IpcMessage?> HandleIpcAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            if (string.Equals(message.Type, IpcProtocol.TypeGetPairing, StringComparison.Ordinal))
            {
                return IpcProtocol.FromPairing(CurrentPairing(), message.Id);
            }

            if (string.Equals(message.Type, IpcProtocol.TypeGetTrusted, StringComparison.Ordinal))
            {
                return new IpcMessage
                {
                    Type = IpcProtocol.TypeTrusted,
                    Trusted = IpcProtocol.ToTrustedPayloads(ListTrusted()),
                };
            }

            if (string.Equals(message.Type, IpcProtocol.TypeStartPairing, StringComparison.Ordinal))
            {
                var snapshot = await StartOutgoingAsync(
                        message.DeviceId,
                        message.Ipv4,
                        message.Port,
                        cancellationToken)
                    .ConfigureAwait(false);
                return IpcProtocol.FromPairing(snapshot, message.Id);
            }

            if (string.Equals(message.Type, IpcProtocol.TypeDecidePairing, StringComparison.Ordinal))
            {
                var snapshot = await DecideLocalAsync(message.Accepted == true, cancellationToken)
                    .ConfigureAwait(false);
                return IpcProtocol.FromPairing(snapshot, message.Id);
            }

            if (string.Equals(message.Type, IpcProtocol.TypeUnpair, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(message.DeviceId))
                {
                    throw new InvalidOperationException("연결 해제할 기기가 없습니다.");
                }

                var removed = _trust.Unpair(message.DeviceId);
                return new IpcMessage
                {
                    Type = IpcProtocol.TypeUnpairAck,
                    DeviceId = message.DeviceId,
                    Succeeded = removed,
                };
            }

            if (string.Equals(message.Type, IpcProtocol.TypeSecurePing, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(message.DeviceId))
                {
                    throw new InvalidOperationException("확인할 기기가 없습니다.");
                }

                await PingTrustedAsync(message.DeviceId, cancellationToken).ConfigureAwait(false);
                return new IpcMessage
                {
                    Type = IpcProtocol.TypePingResult,
                    DeviceId = message.DeviceId,
                    Succeeded = true,
                };
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or HttpRequestException or UnauthorizedAccessException)
        {
            return new IpcMessage
            {
                Type = IpcProtocol.TypeError,
                Error = exception.Message,
            };
        }

        return null;
    }

    public async Task<PairingSnapshot> StartOutgoingAsync(
        string? deviceId,
        string? ipv4,
        int? port,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (_trust.TryGetFingerprint(deviceId, out _))
        {
            throw new InvalidOperationException("이미 신뢰된 기기입니다. 다시 연결하려면 먼저 연결 해제하세요.");
        }

        var peer = _directory.Snapshot().FirstOrDefault(item =>
            string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal));
        var addresses = new List<string>();
        if (!string.IsNullOrWhiteSpace(ipv4))
        {
            addresses.Add(ipv4);
        }

        if (peer is not null)
        {
            foreach (var address in peer.ConnectionIpv4s())
            {
                if (!addresses.Contains(address, StringComparer.Ordinal))
                {
                    addresses.Add(address);
                }
            }
        }

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("상대 기기 주소를 찾지 못했습니다.");
        }

        var listenPort = port ?? peer?.Port ?? _listenPort;
        lock (_gate)
        {
            if (_session is not null && _session.StatusAt(DateTimeOffset.UtcNow) == PairingStatus.Waiting)
            {
                throw new InvalidOperationException("이미 진행 중인 페어링이 있습니다.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var offer = CreateLocalOffer(Guid.NewGuid().ToString("N"), PairingNonce.Create(), now.Add(PairingSession.DefaultLifetime));
        PairingOfferDto? response = null;
        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                response = await _https.OfferAsync(address, listenPort, offer, cancellationToken).ConfigureAwait(false);
                Remember(deviceId, addresses, listenPort);
                _peerAddresses = [address, .. addresses.Where(item => item != address)];
                _peerPort = listenPort;
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or HttpIOException or InvalidOperationException)
            {
                last = exception;
            }
        }

        if (response is null)
        {
            throw last ?? new InvalidOperationException("상대 기기에 연결하지 못했습니다.");
        }

        ValidateOffer(response, expectedSessionId: offer.SessionId);
        var session = new PairingSession(
            offer.SessionId,
            PairingTranscript.Create(
                new PairingSide(_identity.DeviceId, _credential.Fingerprint, offer.Nonce),
                new PairingSide(response.DeviceId, response.Fingerprint, response.Nonce)),
            response.DeviceId,
            response.DeviceName,
            response.Fingerprint,
            offer.ExpiresAt);
        lock (_gate)
        {
            _session = session;
        }

        return session.Snapshot(DateTimeOffset.UtcNow);
    }

    public PairingOfferDto AcceptOffer(PairingOfferDto offer, X509Certificate2 clientCertificate, string? remoteIpv4)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        if (offer.ProtocolVersion != 1)
        {
            throw new InvalidOperationException("지원하지 않는 페어링 프로토콜입니다.");
        }

        ValidateOffer(offer, expectedSessionId: offer.SessionId);
        var clientFingerprint = DeviceFingerprints.FromCertificate(clientCertificate);
        if (!DeviceFingerprints.FixedEquals(clientFingerprint, offer.Fingerprint))
        {
            throw new UnauthorizedAccessException("TLS 인증서가 페어링 요청과 일치하지 않습니다.");
        }

        if (_trust.TryGetFingerprint(offer.DeviceId, out _))
        {
            throw new InvalidOperationException("이미 신뢰된 기기입니다.");
        }

        var now = DateTimeOffset.UtcNow;
        if (offer.ExpiresAt <= now)
        {
            throw new InvalidOperationException("페어링 요청이 만료되었습니다.");
        }

        var reply = CreateLocalOffer(offer.SessionId, PairingNonce.Create(), offer.ExpiresAt);
        var session = new PairingSession(
            offer.SessionId,
            PairingTranscript.Create(
                new PairingSide(_identity.DeviceId, _credential.Fingerprint, reply.Nonce),
                new PairingSide(offer.DeviceId, offer.Fingerprint, offer.Nonce)),
            offer.DeviceId,
            offer.DeviceName,
            offer.Fingerprint,
            offer.ExpiresAt);
        lock (_gate)
        {
            if (_session is not null && _session.StatusAt(now) == PairingStatus.Waiting)
            {
                throw new InvalidOperationException("이미 진행 중인 페어링이 있습니다.");
            }

            _session = session;
            _peerPort = offer.ListenPort > 0 ? offer.ListenPort : _listenPort;
            _peerAddresses = string.IsNullOrWhiteSpace(remoteIpv4) ? [] : [remoteIpv4];
        }

        if (!string.IsNullOrWhiteSpace(remoteIpv4))
        {
            Remember(offer.DeviceId, [remoteIpv4], offer.ListenPort > 0 ? offer.ListenPort : _listenPort);
        }

        return reply;
    }

    public void AcceptRemoteDecision(PairingDecisionDto decision, X509Certificate2 clientCertificate)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        PairingSession session;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("진행 중인 페어링이 없습니다.");
        }

        if (!string.Equals(session.SessionId, decision.SessionId, StringComparison.Ordinal) ||
            !string.Equals(session.PeerDeviceId, decision.DeviceId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("페어링 세션이 일치하지 않습니다.");
        }

        var fingerprint = DeviceFingerprints.FromCertificate(clientCertificate);
        if (!DeviceFingerprints.FixedEquals(fingerprint, session.PeerFingerprint))
        {
            throw new UnauthorizedAccessException("TLS 인증서가 페어링 세션과 일치하지 않습니다.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!session.RecordRemote(decision.Accepted, now))
        {
            throw new InvalidOperationException("이 페어링 세션은 더 이상 결정할 수 없습니다.");
        }

        CompleteIfReady(session, now);
    }

    public async Task<PairingSnapshot> DecideLocalAsync(bool accepted, CancellationToken cancellationToken)
    {
        PairingSession session;
        string[] addresses;
        int port;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("진행 중인 페어링이 없습니다.");
            addresses = _peerAddresses;
            port = _peerPort;
        }

        var now = DateTimeOffset.UtcNow;
        if (!session.RecordLocal(accepted, now))
        {
            throw new InvalidOperationException("이 페어링 세션은 더 이상 결정할 수 없습니다.");
        }

        var decision = new PairingDecisionDto
        {
            SessionId = session.SessionId,
            DeviceId = _identity.DeviceId,
            Accepted = accepted,
        };
        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                await _https.DecideAsync(address, port, decision, cancellationToken).ConfigureAwait(false);
                last = null;
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or HttpIOException or InvalidOperationException)
            {
                last = exception;
            }
        }

        CompleteIfReady(session, DateTimeOffset.UtcNow);
        var snapshot = session.Snapshot(DateTimeOffset.UtcNow);
        if (last is not null && snapshot.Status == "waiting" && accepted)
        {
            throw last;
        }

        return snapshot;
    }

    public async Task PingTrustedAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (!_trust.TryGetFingerprint(deviceId, out var fingerprint))
        {
            throw new UnauthorizedAccessException("신뢰되지 않은 기기입니다.");
        }

        var peer = _directory.Snapshot().FirstOrDefault(item =>
            string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal));
        var addresses = new List<string>();
        if (peer is not null)
        {
            addresses.AddRange(peer.ConnectionIpv4s());
        }

        lock (_gate)
        {
            if (_endpoints.TryGetValue(deviceId, out var known))
            {
                foreach (var address in known.Addresses)
                {
                    if (!addresses.Contains(address, StringComparer.Ordinal))
                    {
                        addresses.Add(address);
                    }
                }
            }
        }

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("상대 기기 주소를 찾지 못했습니다.");
        }

        int port;
        lock (_gate)
        {
            port = peer?.Port ?? (_endpoints.TryGetValue(deviceId, out var known) ? known.Port : _listenPort);
        }
        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                var ping = await _https.PingAsync(address, port, fingerprint, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(ping.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("상대 기기 ID가 일치하지 않습니다.");
                }

                return;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or HttpIOException or InvalidOperationException or UnauthorizedAccessException)
            {
                last = exception;
            }
        }

        throw last ?? new UnauthorizedAccessException("인증된 HTTPS 연결에 실패했습니다.");
    }

    public bool IsSecureClientAllowed(X509Certificate2 certificate) =>
        DeviceCertificateValidator.IsMeshDriveDeviceCertificate(certificate) &&
        _trust.IsTrustedFingerprint(DeviceFingerprints.FromCertificate(certificate));

    private void CompleteIfReady(PairingSession session, DateTimeOffset now)
    {
        if (session.StatusAt(now) != PairingStatus.Completed)
        {
            return;
        }

        _trust.Trust(session.PeerDeviceId, session.PeerName, session.PeerFingerprint, now);
        string[] addresses;
        int port;
        lock (_gate)
        {
            addresses = _peerAddresses;
            port = _peerPort;
        }

        if (addresses.Length > 0)
        {
            Remember(session.PeerDeviceId, addresses, port);
        }
    }

    private void Remember(string deviceId, IReadOnlyList<string> addresses, int port)
    {
        lock (_gate)
        {
            _endpoints[deviceId] = ([.. addresses], port);
        }
    }

    private PairingOfferDto CreateLocalOffer(string sessionId, string nonce, DateTimeOffset expiresAt) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
            Fingerprint = _credential.Fingerprint,
            CertificateDer = Convert.ToBase64String(_credential.Certificate.Export(X509ContentType.Cert)),
            Nonce = nonce,
            ExpiresAt = expiresAt,
            ListenPort = _listenPort,
        };

    private static void ValidateOffer(PairingOfferDto offer, string expectedSessionId)
    {
        if (!string.Equals(offer.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("페어링 세션 ID가 일치하지 않습니다.");
        }

        if (!DeviceIdentityStore.IsUsableDeviceId(offer.DeviceId) ||
            string.IsNullOrWhiteSpace(offer.Fingerprint) ||
            string.IsNullOrWhiteSpace(offer.Nonce))
        {
            throw new InvalidOperationException("페어링 요청이 올바르지 않습니다.");
        }

        var der = Convert.FromBase64String(offer.CertificateDer);
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        if (!DeviceCertificateValidator.IsMeshDriveDeviceCertificate(certificate) ||
            !DeviceFingerprints.FixedEquals(DeviceFingerprints.FromCertificate(certificate), offer.Fingerprint))
        {
            throw new UnauthorizedAccessException("장치 인증서가 올바르지 않습니다.");
        }
    }
}
