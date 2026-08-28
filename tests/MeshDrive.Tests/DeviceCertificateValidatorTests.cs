using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class DeviceCertificateValidatorTests
{
    [TestMethod]
    public void PairingAllowsMeshDriveCertAndTrustedRequiresMatchingFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshdrive-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            var credential = DeviceCredentialStore.LoadOrCreate(directory, "dev1");
            var errors = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
            Assert.IsTrue(DeviceCertificateValidator.AcceptForPairing(credential.Certificate, errors));
            Assert.IsTrue(DeviceCertificateValidator.AcceptTrusted(credential.Certificate, errors, credential.Fingerprint));
            Assert.IsFalse(DeviceCertificateValidator.AcceptTrusted(credential.Certificate, errors, new string('F', 64)));
            Assert.IsFalse(DeviceCertificateValidator.AcceptForPairing(certificate: null, errors));
            Assert.IsFalse(DeviceCertificateValidator.IsMeshDriveDeviceCertificate(certificate: null));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
