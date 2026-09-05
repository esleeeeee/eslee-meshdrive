using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace MeshDrive.Agent;

public sealed class PhotoCache(string directory, long capacity = 1024L * 1024 * 1024)
{
    private readonly object _gate = new();
    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";
    public string PathFor(string key, string extension)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + extension);
    }
    public void Trim(string? keep = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var files = new DirectoryInfo(directory).GetFiles().Where(f => !f.Name.EndsWith(".tmp", StringComparison.Ordinal)).OrderBy(f => f.LastWriteTimeUtc).ToArray();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= capacity) break;
                if (file.FullName == keep) continue;
                try { file.Delete(); total -= file.Length; } catch (IOException) { }
            }
        }
    }
    public string Thumbnail(string source)
    {
        if (!IsImage(source)) throw new IOException("미리보기를 지원하지 않는 이미지 형식입니다.");
        lock (_gate)
        {
            var info = new FileInfo(source);
            var target = PathFor(source + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks, ".jpg");
            if (!File.Exists(target))
            {
                using var stream = File.OpenRead(source);
                using var codec = SKCodec.Create(stream) ?? throw new IOException("이미지를 읽을 수 없습니다.");
                if ((long)codec.Info.Width * codec.Info.Height > 100_000_000) throw new IOException("미리보기 최대 이미지 크기를 초과했습니다.");
                using var bitmap = SKBitmap.Decode(codec) ?? throw new IOException("이미지 디코딩에 실패했습니다.");
                var scale = Math.Min(1d, 256d / Math.Max(bitmap.Width, bitmap.Height));
                using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bitmap, new SKRect(0, 0, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))));
                using var image = surface.Snapshot(); using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                using (var output = File.Create(target + ".tmp")) encoded.SaveTo(output);
                File.Move(target + ".tmp", target, true);
            }
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow); Trim(target); return target;
        }
    }
}
