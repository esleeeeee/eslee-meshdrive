using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class DeviceIdentityStoreTests
{
    [TestMethod]
    public void LoadOrCreatePersistsDeviceIdAndUsesMachineName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshdrive-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = DeviceIdentityStore.LoadOrCreate(directory, "Office-PC");
            Assert.IsTrue(DeviceIdentityStore.IsUsableDeviceId(first.DeviceId));
            Assert.AreEqual("Office-PC", first.DeviceName);
            Assert.IsTrue(File.Exists(Path.Combine(directory, DeviceIdentityStore.FileName)));

            var second = DeviceIdentityStore.LoadOrCreate(directory, "Renamed-PC");
            Assert.AreEqual(first.DeviceId, second.DeviceId);
            Assert.AreEqual("Renamed-PC", second.DeviceName);
            Assert.AreEqual("MeshDrive-PC", DeviceIdentityStore.NormalizeName("  "));
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
