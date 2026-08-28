using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed class DeviceCredential
{
    public DeviceCredential(string deviceId, X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(certificate);
        DeviceId = deviceId;
        Certificate = certificate;
        Fingerprint = DeviceFingerprints.FromCertificate(certificate);
    }

    public string DeviceId { get; }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }
}

public static class DeviceCredentialStore
{
    public const string FileName = "device-credential.dpapi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeshDrive.DeviceCredential.v1");

    public static DeviceCredential LoadOrCreate(string dataDirectory, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(path);
                var pfx = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var loaded = X509CertificateLoader.LoadPkcs12(
                    pfx,
                    password: null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
                if (loaded.HasPrivateKey && DeviceCertificateValidator.IsMeshDriveDeviceCertificate(loaded))
                {
                    return new DeviceCredential(deviceId, loaded);
                }
            }
            catch (CryptographicException)
            {
            }
            catch (IOException)
            {
            }
        }

        var created = CreateCertificate(deviceId);
        var exported = created.Export(X509ContentType.Pfx);
        var wrapped = ProtectedData.Protect(exported, Entropy, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, wrapped);
        File.Copy(temp, path, overwrite: true);
        File.Delete(temp);
        return new DeviceCredential(deviceId, created);
    }

    internal static X509Certificate2 CreateCertificate(string deviceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName(
            $"CN={DeviceFingerprints.CertificateSubjectCommonName}, serialNumber={deviceId}");
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
    }
}
