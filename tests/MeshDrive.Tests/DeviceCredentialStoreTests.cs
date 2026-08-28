using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class DeviceCredentialStoreTests
{
    [TestMethod]
    public void PersistsUserProtectedKeyAndReloadsSameFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshdrive-cred-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = DeviceCredentialStore.LoadOrCreate(directory, "abc123");
            Assert.IsTrue(DeviceCertificateValidator.IsMeshDriveDeviceCertificate(first.Certificate));
            Assert.AreEqual(64, first.Fingerprint.Length);
            Assert.IsTrue(first.Certificate.HasPrivateKey);
            Assert.IsTrue(File.Exists(Path.Combine(directory, DeviceCredentialStore.FileName)));

            var second = DeviceCredentialStore.LoadOrCreate(directory, "abc123");
            Assert.AreEqual(first.Fingerprint, second.Fingerprint);
            Assert.IsTrue(DeviceFingerprints.FixedEquals(first.Fingerprint, second.Fingerprint));
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
