using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeshDrive.Core;

public static class DeviceCertificateValidator
{
    public static bool IsMeshDriveDeviceCertificate(X509Certificate2? certificate)
    {
        if (certificate is null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        if (!certificate.Subject.Contains(DeviceFingerprints.CertificateSubjectCommonName, StringComparison.Ordinal))
        {
            return false;
        }

        using var key = certificate.GetECDsaPublicKey();
        return key is not null && string.Equals(certificate.Issuer, certificate.Subject, StringComparison.Ordinal);
    }

    public static bool AcceptForPairing(
        X509Certificate? certificate,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        using var loaded = new X509Certificate2(certificate);
        if (!IsMeshDriveDeviceCertificate(loaded))
        {
            return false;
        }

        return IsAllowedSelfSignedError(errors);
    }

    public static bool AcceptTrusted(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        string expectedFingerprint)
    {
        if (!AcceptForPairing(certificate, errors) || certificate is null)
        {
            return false;
        }

        using var loaded = new X509Certificate2(certificate);
        return DeviceFingerprints.FixedEquals(DeviceFingerprints.FromCertificate(loaded), expectedFingerprint);
    }

    public static bool IsAllowedSelfSignedError(SslPolicyErrors errors)
    {
        var remaining = errors & ~(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors);
        return remaining == SslPolicyErrors.None;
    }
}
