namespace MeshDrive.Core;

public enum PairingStatus
{
    Waiting,
    Completed,
    Rejected,
    Expired,
}

public sealed class PairingSession
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();

    public PairingSession(
        string sessionId,
        PairingTranscript transcript,
        string peerDeviceId,
        string peerName,
        string peerFingerprint,
        DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerFingerprint);
        SessionId = sessionId;
        Transcript = transcript;
        PeerDeviceId = peerDeviceId;
        PeerName = string.IsNullOrWhiteSpace(peerName) ? peerDeviceId : peerName;
        PeerFingerprint = peerFingerprint;
        ExpiresAt = expiresAt;
        Sas = SasCalculator.Compute(transcript);
    }

    public string SessionId { get; }

    public PairingTranscript Transcript { get; }

    public string PeerDeviceId { get; }

    public string PeerName { get; }

    public string PeerFingerprint { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string Sas { get; }

    public bool LocalAccepted { get; private set; }

    public bool RemoteAccepted { get; private set; }

    public PairingStatus StatusAt(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_rejected)
            {
                return PairingStatus.Rejected;
            }

            if (LocalAccepted && RemoteAccepted)
            {
                return PairingStatus.Completed;
            }

            return now >= ExpiresAt ? PairingStatus.Expired : PairingStatus.Waiting;
        }
    }

    public bool RecordLocal(bool accepted, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_rejected || StatusLocked(now) || LocalAccepted)
            {
                return false;
            }

            if (!accepted)
            {
                _rejected = true;
                return true;
            }

            LocalAccepted = true;
            return true;
        }
    }

    public bool RecordRemote(bool accepted, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_rejected || StatusLocked(now) || RemoteAccepted)
            {
                return false;
            }

            if (!accepted)
            {
                _rejected = true;
                return true;
            }

            RemoteAccepted = true;
            return true;
        }
    }

    public PairingSnapshot Snapshot(DateTimeOffset now) =>
        new(
            SessionId,
            PeerDeviceId,
            PeerName,
            PeerFingerprint,
            Sas,
            StatusAt(now).ToString().ToLowerInvariant(),
            LocalAccepted,
            RemoteAccepted,
            ExpiresAt);

    private bool StatusLocked(DateTimeOffset now) =>
        (LocalAccepted && RemoteAccepted) || now >= ExpiresAt;

    private bool _rejected;
}

public sealed record PairingSnapshot(
    string SessionId,
    string PeerDeviceId,
    string PeerName,
    string PeerFingerprint,
    string Sas,
    string Status,
    bool LocalAccepted,
    bool RemoteAccepted,
    DateTimeOffset ExpiresAt);
