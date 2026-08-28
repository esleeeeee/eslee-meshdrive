using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class TrustedPeerStoreTests
{
    [TestMethod]
    public void TrustsThenUnpairsAndRejectsWrongFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshdrive-trust-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrustedPeerStore(directory);
            var now = DateTimeOffset.UtcNow;
            store.Trust("laptop1", "Laptop", new string('A', 64), now);
            Assert.IsTrue(store.IsTrusted("laptop1", new string('A', 64)));
            Assert.IsFalse(store.IsTrusted("laptop1", new string('B', 64)));
            Assert.IsTrue(store.IsTrustedFingerprint(new string('A', 64)));

            var reloaded = new TrustedPeerStore(directory);
            Assert.HasCount(1, reloaded.Snapshot());
            Assert.IsTrue(reloaded.Unpair("laptop1"));
            Assert.IsFalse(reloaded.IsTrusted("laptop1", new string('A', 64)));
            Assert.IsFalse(new TrustedPeerStore(directory).IsTrustedFingerprint(new string('A', 64)));
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
