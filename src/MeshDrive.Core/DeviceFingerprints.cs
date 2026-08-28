using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeshDrive.Core;

public static class DeviceFingerprints
{
    public const string CertificateSubjectCommonName = "eslee MeshDrive Device";

    public static string FromCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var key = certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException("MeshDrive 장치 인증서는 ECDSA여야 합니다.");
        return FromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo());
    }

    public static string FromSubjectPublicKeyInfo(byte[] subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);
        return Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));
    }

    public static bool FixedEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
