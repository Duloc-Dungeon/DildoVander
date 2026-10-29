using Kufr.Backend.Features.Chronicle;
using SkiaSharp;

namespace Kufr.Backend.Features.Keepsakes;

// The interior is taken from the generated source scene. A shared frame and a
// stable symbol identify the achievement without replacing the personal motif.
internal static class ChronicleBadgeRenderer
{
    internal static byte[] Render(ChronicleImage image, string awardId)
    {
        using var source = SKBitmap.Decode(image.Bytes) ?? throw new InvalidDataException("chronicle_image_invalid");
        using var badge = new SKBitmap(384, 432);
        using var canvas = new SKCanvas(badge);
        canvas.Clear(SKColors.Transparent);
        using var ribbon = new SKPaint { IsAntialias = true, Color = new SKColor(69, 98, 86) };
        using var pathBuilder = new SKPathBuilder();
        pathBuilder.MoveTo(100, 290); pathBuilder.LineTo(70, 428); pathBuilder.LineTo(136, 396);
        pathBuilder.LineTo(180, 430); pathBuilder.LineTo(205, 290); pathBuilder.Close();
        using var path = pathBuilder.Detach();
        canvas.DrawPath(path, ribbon);
        canvas.Save(); canvas.Translate(384, 0); canvas.Scale(-1, 1); canvas.DrawPath(path, ribbon); canvas.Restore();
        canvas.Save();
        using var circleBuilder = new SKPathBuilder(); circleBuilder.AddCircle(192, 190, 160);
        using var circle = circleBuilder.Detach();
        canvas.ClipPath(circle);
        var sceneHeight = Math.Min(source.Width, source.Height);
        canvas.DrawBitmap(source, new SKRect(0, 0, source.Width, sceneHeight), new SKRect(32, 30, 352, 350),
            new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
        using var frame = new SKPaint { Color = new SKColor(217, 192, 130), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10 };
        canvas.DrawCircle(192, 190, 165, frame);
        frame.StrokeWidth = 2; canvas.DrawCircle(192, 190, 150, frame);
        using var seal = new SKPaint { Color = new SKColor(25, 34, 31), IsAntialias = true };
        canvas.DrawCircle(192, 340, 43, seal);
        frame.StrokeWidth = 4; canvas.DrawCircle(192, 340, 43, frame);
        // Geometric symbols keep this usable without platform emoji fonts.
        switch (awardId)
        {
            case "mind-reader":
                canvas.DrawOval(new SKRect(164, 326, 220, 354), frame);
                canvas.DrawCircle(192, 340, 7, frame);
                break;
            case "accomplices":
                canvas.DrawCircle(179, 338, 11, frame); canvas.DrawCircle(205, 338, 11, frame);
                break;
            case "remembered-opponent":
                canvas.DrawLine(175, 324, 210, 356, frame); canvas.DrawLine(175, 356, 210, 324, frame);
                break;
            case "first-team-match":
                canvas.DrawRoundRect(new SKRect(164, 330, 198, 351), 9, 9, frame);
                canvas.DrawRoundRect(new SKRect(186, 330, 220, 351), 9, 9, frame);
                break;
            case "seal_released":
                canvas.DrawRect(new SKRect(172, 328, 212, 356), frame);
                canvas.DrawArc(new SKRect(179, 312, 204, 343), 190, 170, false, frame);
                break;
            case "second_edition":
                canvas.DrawLine(182, 322, 182, 358, frame); canvas.DrawLine(202, 322, 202, 358, frame);
                break;
            default:
                canvas.DrawLine(178, 356, 206, 324, frame);
                canvas.DrawLine(200, 322, 210, 322, frame);
                canvas.DrawLine(208, 316, 208, 328, frame);
                break;
        }
        using var rendered = SKImage.FromBitmap(badge);
        using var encoded = rendered.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
