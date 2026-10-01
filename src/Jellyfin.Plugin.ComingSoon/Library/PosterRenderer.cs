using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.ComingSoon.Tracking;
using SkiaSharp;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>
/// Draws the status onto a stub's poster — badge, headline, percentage, progress bar and a detail line —
/// so every client's poster grid shows progress without any client changes. Uses the SkiaSharp that
/// Jellyfin itself ships (same version), and bundled Lato fonts (SIL OFL) so text never depends on
/// what fonts the container has.
/// </summary>
public static class PosterRenderer
{
    public const int Width = 600;
    public const int Height = 900;

    private const float Margin = 32;

    private static readonly Lazy<SKTypeface> Bold = new(() => LoadFont("Lato-Bold.ttf"));
    private static readonly Lazy<SKTypeface> Regular = new(() => LoadFont("Lato-Regular.ttf"));

    public static SKColor AccentFor(TrackedStatus status) => status switch
    {
        TrackedStatus.Downloading => new SKColor(0x00, 0xA4, 0xDC),
        TrackedStatus.Stalled => new SKColor(0xE5, 0xA0, 0x0D),
        TrackedStatus.Importing => new SKColor(0x52, 0xB5, 0x4B),
        TrackedStatus.Queued => new SKColor(0x9A, 0xA5, 0xB1),
        TrackedStatus.WaitingForRelease => new SKColor(0x5B, 0x8D, 0xEF),
        _ => new SKColor(0xB0, 0x7C, 0xD8),
    };

    /// <summary>Renders a 600x900 JPEG. <paramref name="source"/> may be null or undecodable (a title card is drawn instead).</summary>
    public static byte[] Render(byte[]? source, string title, DisplayState state)
    {
        var accent = AccentFor(state.Status);
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        using (var poster = source is { Length: > 0 } ? SKImage.FromEncodedData(source) : null)
        {
            if (poster is not null)
            {
                DrawCover(canvas, poster);
            }
            else
            {
                DrawTitleCard(canvas, title, accent);
            }
        }

        DrawBadge(canvas, accent);
        DrawStatusPanel(canvas, state, accent);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static void DrawCover(SKCanvas canvas, SKImage poster)
    {
        var scale = Math.Max((float)Width / poster.Width, (float)Height / poster.Height);
        var w = poster.Width * scale;
        var h = poster.Height * scale;
        var dest = SKRect.Create((Width - w) / 2, (Height - h) / 2, w, h);
        canvas.DrawImage(poster, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static void DrawTitleCard(SKCanvas canvas, string title, SKColor accent)
    {
        using var background = new SKPaint();
        background.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, Height),
            [Blend(accent, new SKColor(0x10, 0x14, 0x18), 0.55f), new SKColor(0x10, 0x14, 0x18)],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, Width, Height, background);

        using var font = new SKFont(Bold.Value, 56);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var lines = Wrap(title, font, Width - (2 * Margin), maxLines: 5);
        var lineHeight = 66f;
        var top = (Height * 0.42f) - (lines.Count * lineHeight / 2) + 48;
        for (var i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], Width / 2f, top + (i * lineHeight), SKTextAlign.Center, font, paint);
        }
    }

    private static void DrawBadge(SKCanvas canvas, SKColor accent)
    {
        const string Text = "COMING SOON";
        using var font = new SKFont(Bold.Value, 20);
        var textWidth = font.MeasureText(Text);
        var rect = SKRect.Create(24, 24, textWidth + 40, 40);

        using var fill = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true };
        canvas.DrawRoundRect(rect, 20, 20, fill);
        using var dot = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawCircle(rect.Left + 18, rect.MidY, 5, dot);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(Text, rect.Left + 30, rect.MidY + 7, SKTextAlign.Left, font, text);
    }

    private static void DrawStatusPanel(SKCanvas canvas, DisplayState state, SKColor accent)
    {
        var (headline, subline, progress) = StatusText.PosterLines(state);

        // Fade to near-black so the text reads on any poster.
        using (var shade = new SKPaint())
        {
            shade.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, Height - 300),
                new SKPoint(0, Height),
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 215), new SKColor(0, 0, 0, 240)],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, Height - 300, Width, 300, shade);
        }

        using var headlineFont = new SKFont(Bold.Value, 40);
        using var sublineFont = new SKFont(Regular.Value, 30);
        using var accentPaint = new SKPaint { Color = accent, IsAntialias = true };
        using var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var softPaint = new SKPaint { Color = new SKColor(255, 255, 255, 220), IsAntialias = true };

        var right = Width - Margin;
        var percentText = state.Percent is int p && state.Status is TrackedStatus.Downloading or TrackedStatus.Stalled
            ? p.ToString(CultureInfo.InvariantCulture) + "%"
            : null;
        var percentWidth = percentText is null ? 0 : headlineFont.MeasureText(percentText) + 16;

        canvas.DrawText(Ellipsize(headline, headlineFont, right - Margin - percentWidth), Margin, Height - 126, SKTextAlign.Left, headlineFont, accentPaint);
        if (percentText is not null)
        {
            canvas.DrawText(percentText, right, Height - 126, SKTextAlign.Right, headlineFont, whitePaint);
        }

        var bar = SKRect.Create(Margin, Height - 102, Width - (2 * Margin), 20);
        using (var track = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(progress is null ? 40 : 70)), IsAntialias = true })
        {
            canvas.DrawRoundRect(bar, 10, 10, track);
        }

        if (progress is double fraction && fraction > 0)
        {
            var filled = SKRect.Create(bar.Left, bar.Top, Math.Max(bar.Height, bar.Width * (float)Math.Clamp(fraction, 0, 1)), bar.Height);
            canvas.DrawRoundRect(filled, 10, 10, accentPaint);
        }

        canvas.DrawText(Ellipsize(subline, sublineFont, Width - (2 * Margin)), Margin, Height - 42, SKTextAlign.Left, sublineFont, softPaint);
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        if (lines.Count > maxLines)
        {
            lines = lines.GetRange(0, maxLines);
            lines[^1] = Ellipsize(lines[^1] + "…", font, maxWidth);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = Ellipsize(lines[i], font, maxWidth);
        }

        return lines;
    }

    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        var trimmed = text.TrimEnd('…');
        while (trimmed.Length > 1 && font.MeasureText(trimmed + "…") > maxWidth)
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed + "…";
    }

    private static SKColor Blend(SKColor a, SKColor b, float t)
        => new(
            (byte)((a.Red * (1 - t)) + (b.Red * t)),
            (byte)((a.Green * (1 - t)) + (b.Green * t)),
            (byte)((a.Blue * (1 - t)) + (b.Blue * t)));

    private static SKTypeface LoadFont(string file)
    {
        var name = "Jellyfin.Plugin.ComingSoon.Resources." + file;
        using var stream = typeof(PosterRenderer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded font missing: " + name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        using var data = SKData.CreateCopy(memory.ToArray());
        return SKTypeface.FromData(data) ?? SKTypeface.Default;
    }
}
