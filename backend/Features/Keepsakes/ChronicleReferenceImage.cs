using SkiaSharp;

namespace Kufr.Backend.Features.Keepsakes;

internal static class ChronicleReferenceImage
{
    internal static byte[]? CopyBounded(byte[] bytes)
    {
        if (bytes.Length is 0 or > 8 * 1024 * 1024) { return null; }
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096) { return null; }
        using var source = SKBitmap.Decode(bytes);
        if (source is null) { return null; }
        var scale = Math.Min(1d, 256d / Math.Max(source.Width, source.Height));
        using var target = new SKBitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, new SKRect(0, 0, target.Width, target.Height), new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(target);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
