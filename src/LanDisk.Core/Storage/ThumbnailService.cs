using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using LanDisk.Core.Configuration;
using LanDisk.Core.Media;
using LanDisk.Core.Paths;

namespace LanDisk.Core.Storage;

public sealed class ThumbnailResult
{
    public required byte[] Bytes { get; init; }
    public required string ContentType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool FromCache { get; init; }
}

public enum ThumbnailStatus
{
    Ok,
    NotAnImage,
    SourceTooLarge,
    DecodeFailed,
    Unsupported
}

public sealed class ThumbnailOutcome
{
    public ThumbnailStatus Status { get; init; }
    public ThumbnailResult? Result { get; init; }
    public string? Detail { get; init; }

    public static ThumbnailOutcome Fail(ThumbnailStatus status, string? detail = null)
        => new() { Status = status, Detail = detail };
}

/// <summary>
/// Builds small JPEG thumbnails for image listings and caches them on disk.
///
/// The source image is always read through the handle produced by <see cref="PathResolver"/>, so a
/// thumbnail request cannot reach a file that a normal read could not. Decoding is bounded by file
/// size and by a megapixel ceiling, and every failure mode returns an explicit status so the UI can
/// fall back to a plain icon instead of showing a broken image.
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>Refuse to decode anything larger than this many pixels, independent of file size.</summary>
    private const long MaxPixels = 120_000_000;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"
    };

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;

    public ThumbnailService(AppPaths paths, AppSettings settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public bool IsThumbnailable(string fileName)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(fileName))) return false;
        // SVG is deliberately excluded: it is active content and must never be rasterised into
        // something the application origin would then serve back inline.
        return !MimeTypes.IsActiveContent(fileName);
    }

    public ThumbnailOutcome Get(ResolvedTarget target)
    {
        if (target.IsDirectory) return ThumbnailOutcome.Fail(ThumbnailStatus.NotAnImage, "目标是目录。");

        string fileName = target.ParentName;
        if (!IsThumbnailable(fileName))
            return ThumbnailOutcome.Fail(ThumbnailStatus.NotAnImage, "该类型不生成缩略图。");

        long sourceLength = target.Length;
        if (sourceLength <= 0)
            return ThumbnailOutcome.Fail(ThumbnailStatus.DecodeFailed, "文件为空。");
        if (sourceLength > _settings.ThumbnailSourceMaxBytes)
            return ThumbnailOutcome.Fail(ThumbnailStatus.SourceTooLarge, "图片过大，不生成缩略图。");

        int maxEdge = Math.Clamp(_settings.ThumbnailMaxEdge, 64, 1024);

        // The cache key covers the object identity and the modification stamp, so replacing a file
        // with different content at the same path invalidates the cached image.
        string key = string.Join('|',
            "v1",
            maxEdge.ToString(),
            target.Opened!.ObjectId.ToString(),
            target.Opened.Length.ToString(),
            target.Opened.LastWriteTime.UtcTicks.ToString());

        string cachePath = Path.Combine(_paths.ThumbnailDir, Sha256Hex(key) + ".jpg");
        if (File.Exists(cachePath))
        {
            try
            {
                byte[] cached = File.ReadAllBytes(cachePath);
                if (cached.Length > 0)
                {
                    (int width, int height) = ReadJpegSize(cached);
                    return new ThumbnailOutcome
                    {
                        Status = ThumbnailStatus.Ok,
                        Result = new ThumbnailResult
                        {
                            Bytes = cached,
                            ContentType = "image/jpeg",
                            Width = width,
                            Height = height,
                            FromCache = true
                        }
                    };
                }
            }
            catch
            {
                // A damaged cache entry is simply regenerated below.
            }
        }

        try
        {
            byte[] jpeg = Render(target.Opened.Handle, maxEdge);
            WriteCache(cachePath, jpeg);
            (int width, int height) = ReadJpegSize(jpeg);
            return new ThumbnailOutcome
            {
                Status = ThumbnailStatus.Ok,
                Result = new ThumbnailResult
                {
                    Bytes = jpeg,
                    ContentType = "image/jpeg",
                    Width = width,
                    Height = height,
                    FromCache = false
                }
            };
        }
        catch (OutOfMemoryException)
        {
            // GDI+ reports an unreadable or absurdly large image as OutOfMemoryException.
            return ThumbnailOutcome.Fail(ThumbnailStatus.DecodeFailed, "无法解码该图片。");
        }
        catch (Exception ex)
        {
            return ThumbnailOutcome.Fail(ThumbnailStatus.DecodeFailed, ex.Message);
        }
    }

    private byte[] Render(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int maxEdge)
    {
        using var source = new FileStream(handle, FileAccess.Read);

        // Image.FromStream needs the stream to stay alive for the lifetime of the Image.
        using var image = Image.FromStream(source, useEmbeddedColorManagement: false, validateImageData: true);

        if ((long)image.Width * image.Height > MaxPixels)
            throw new InvalidOperationException("图片像素尺寸过大。");

        int rotation = ReadExifOrientation(image);

        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(image.Width, image.Height));
        int targetWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(image.Height * scale));

        bool swapAxes = rotation is 6 or 8;
        int canvasWidth = swapAxes ? targetHeight : targetWidth;
        int canvasHeight = swapAxes ? targetWidth : targetHeight;

        using var bitmap = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.Clear(Color.White);

            switch (rotation)
            {
                case 3:
                    graphics.TranslateTransform(canvasWidth, canvasHeight);
                    graphics.RotateTransform(180);
                    break;
                case 6:
                    graphics.TranslateTransform(canvasWidth, 0);
                    graphics.RotateTransform(90);
                    break;
                case 8:
                    graphics.TranslateTransform(0, canvasHeight);
                    graphics.RotateTransform(270);
                    break;
            }

            graphics.DrawImage(image, new Rectangle(0, 0, targetWidth, targetHeight));
        }

        using var output = new MemoryStream();
        ImageCodecInfo? encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
        {
            bitmap.Save(output, ImageFormat.Jpeg);
        }
        else
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            bitmap.Save(output, encoder, parameters);
        }

        return output.ToArray();
    }

    private static int ReadExifOrientation(Image image)
    {
        try
        {
            if (image.PropertyIdList.Contains(0x0112))
            {
                System.Drawing.Imaging.PropertyItem? item = image.GetPropertyItem(0x0112);
                byte[]? data = item?.Value;
                if (data is { Length: > 0 })
                {
                    int value = data[0];
                    if (value is >= 1 and <= 8) return value;
                }
            }
        }
        catch
        {
            // Missing or malformed EXIF is not an error; the image is simply drawn as stored.
        }
        return 1;
    }

    /// <summary>Reads width/height straight from the JPEG SOF marker without a full decode.</summary>
    private static (int Width, int Height) ReadJpegSize(byte[] jpeg)
    {
        int index = 2;
        while (index + 9 < jpeg.Length)
        {
            if (jpeg[index] != 0xFF)
            {
                index++;
                continue;
            }

            byte marker = jpeg[index + 1];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                int height = (jpeg[index + 5] << 8) | jpeg[index + 6];
                int width = (jpeg[index + 7] << 8) | jpeg[index + 8];
                return (width, height);
            }

            if (marker is 0xD8 or 0xD9) { index += 2; continue; }
            int length = (jpeg[index + 2] << 8) | jpeg[index + 3];
            if (length <= 0) break;
            index += 2 + length;
        }

        return (0, 0);
    }

    private void WriteCache(string cachePath, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, cachePath, true);
            EvictIfNeeded();
        }
        catch
        {
            // A cache write failure must never fail the request; the thumbnail is still returned.
        }
    }

    private void EvictIfNeeded()
    {
        long budget = _settings.ThumbnailCacheBytes;
        if (budget <= 0) return;

        var files = new DirectoryInfo(_paths.ThumbnailDir).GetFiles("*.jpg");
        long total = files.Sum(f => f.Length);
        if (total <= budget) return;

        foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= budget) break;
            try
            {
                total -= file.Length;
                file.Delete();
            }
            catch { }
        }
    }

    public long CurrentCacheBytes()
    {
        try
        {
            var dir = new DirectoryInfo(_paths.ThumbnailDir);
            return dir.Exists ? dir.GetFiles("*.jpg").Sum(f => f.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void ClearCache()
    {
        try
        {
            var dir = new DirectoryInfo(_paths.ThumbnailDir);
            if (dir.Exists) foreach (FileInfo file in dir.GetFiles("*.jpg")) file.Delete();
        }
        catch { }
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
