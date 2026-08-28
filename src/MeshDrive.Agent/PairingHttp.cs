namespace MeshDrive.Agent;

public sealed class PairingOfferDto
{
    public int ProtocolVersion { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string CertificateDer { get; set; } = string.Empty;

    public string Nonce { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public int ListenPort { get; set; }
}

public sealed class PairingDecisionDto
{
    public string SessionId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public bool Accepted { get; set; }
}

public sealed class SecurePingDto
{
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;
}
