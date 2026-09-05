using MeshDrive.Agent;
using MeshDrive.Core;
using SkiaSharp;

namespace MeshDrive.Tests;

[TestClass]
public sealed class PhotoTests
{
    [TestMethod]
    public async Task ThumbnailsAndOriginalCachePreserveSourceAndInvalidate()
    {
        await using var a = await StorageHttpsTests.Node.CreateAsync("A");
        await using var b = await StorageHttpsTests.Node.CreateAsync("B"); await a.PairAsync(b);
        using var bitmap = new SKBitmap(640, 480); bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        var share = b.Storage.Shares.Save(null, "Photos", b.Root, SharePermissions.ReadOnly);
        using var photos = new RemotePhotoService(a.Remote, a.Data);
        foreach (var format in new[] { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp })
        {
            var name = "photo." + format.ToString().ToLowerInvariant();
            using var encoded = image.Encode(format, 90); var source = encoded.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(b.Root, name), source);
            var thumb = await photos.GetAsync(b.Identity.DeviceId, share.Id, name, true, CancellationToken.None);
            using var small = SKBitmap.Decode(thumb); Assert.IsTrue(small.Width <= 256 && small.Height <= 256);
            var original = await photos.GetAsync(b.Identity.DeviceId, share.Id, name, false, CancellationToken.None);
            CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(original));
            Assert.AreEqual(original, await photos.GetAsync(b.Identity.DeviceId, share.Id, name, false, CancellationToken.None));
            File.SetLastWriteTimeUtc(Path.Combine(b.Root, name), DateTime.UtcNow.AddSeconds(5));
            Assert.AreNotEqual(original, await photos.GetAsync(b.Identity.DeviceId, share.Id, name, false, CancellationToken.None));
        }
        b.Storage.Paused = true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => photos.GetAsync(b.Identity.DeviceId, share.Id, "photo.png", false, CancellationToken.None));
    }

    [TestMethod]
    public void CacheTrimsOldestItemsToBudget()
    {
        using var f = new StorageTests.StorageFixture();
        var cache = new PhotoCache(Path.Combine(f.Data, "cache"), 10);
        var old = cache.PathFor("old", ".jpg"); var recent = cache.PathFor("recent", ".jpg");
        File.WriteAllBytes(old, new byte[8]); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        File.WriteAllBytes(recent, new byte[8]); cache.Trim(recent);
        Assert.IsFalse(File.Exists(old)); Assert.IsTrue(File.Exists(recent));
    }
}
