using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.ComingSoon.Tracking;
using SkiaSharp;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>
/// Draws the status onto a stub's artwork — badge, headline, percentage, progress bar and a detail line —
/// so client home rows and grids show progress without any client changes. Renders the portrait poster
/// (Primary) and a 16:9 thumbnail (Thumb, used by Moonfin's "thumbnail" rows). Uses the SkiaSharp that
/// Jellyfin itself ships (same version) and bundled Lato fonts (SIL OFL), so text never depends on the
/// container's fonts.
/// </summary>
public static class PosterRenderer
{
    public const int Width = 600;
    public const int Height = 900;
    public const int ThumbWidth = 960;
    public const int ThumbHeight = 540;

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

    /// <summary>Portrait poster, 600x900 JPEG. <paramref name="source"/> may be null or undecodable (a title card is drawn instead).</summary>
    public static byte[] Render(byte[]? source, string title, DisplayState state) => Render(source, title, state, Width, Height);

    /// <summary>Landscape thumbnail, 960x540 JPEG, best made from the backdrop.</summary>
    public static byte[] RenderThumb(byte[]? source, string title, DisplayState state) => Render(source, title, state, ThumbWidth, ThumbHeight);

    private static byte[] Render(byte[]? source, string title, DisplayState state, int w, int h)
    {
        var layout = new Layout(w, h);
        var accent = AccentFor(state.Status);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        using (var art = source is { Length: > 0 } ? SKImage.FromEncodedData(source) : null)
        {
            if (art is not null)
            {
                DrawCover(canvas, art, layout);
            }
            else
            {
                DrawTitleCard(canvas, title, accent, layout);
            }
        }

        DrawBadge(canvas, accent, layout);
        DrawStatusPanel(canvas, state, accent, layout);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static void DrawCover(SKCanvas canvas, SKImage art, Layout l)
    {
        var scale = Math.Max((float)l.W / art.Width, (float)l.H / art.Height);
        var w = art.Width * scale;
        var h = art.Height * scale;
        var dest = SKRect.Create((l.W - w) / 2, (l.H - h) / 2, w, h);
        canvas.DrawImage(art, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static void DrawTitleCard(SKCanvas canvas, string title, SKColor accent, Layout l)
    {
        using var background = new SKPaint();
        background.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, l.H),
            [Blend(accent, new SKColor(0x10, 0x14, 0x18), 0.55f), new SKColor(0x10, 0x14, 0x18)],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, l.W, l.H, background);

        using var font = new SKFont(Bold.Value, 56 * l.K);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var lineHeight = 66 * l.K;
        var available = l.PanelTop - (100 * l.K);
        var maxLines = Math.Max(1, Math.Min(5, (int)(available / lineHeight)));
        var lines = Wrap(title, font, l.W - (2 * l.Margin), maxLines);
        var top = (100 * l.K) + ((available - (lines.Count * lineHeight)) / 2) + (font.Size * 0.8f);
        for (var i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], l.W / 2f, top + (i * lineHeight), SKTextAlign.Center, font, paint);
        }
    }

    private static void DrawBadge(SKCanvas canvas, SKColor accent, Layout l)
    {
        const string Text = "COMING SOON";
        using var font = new SKFont(Bold.Value, 20 * l.K);
        var textWidth = font.MeasureText(Text);
        var rect = SKRect.Create(24 * l.K, 24 * l.K, textWidth + (40 * l.K), 40 * l.K);

        using var fill = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true };
        canvas.DrawRoundRect(rect, rect.Height / 2, rect.Height / 2, fill);
        using var dot = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawCircle(rect.Left + (18 * l.K), rect.MidY, 5 * l.K, dot);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(Text, rect.Left + (30 * l.K), rect.MidY + (7 * l.K), SKTextAlign.Left, font, text);
    }

    private static void DrawStatusPanel(SKCanvas canvas, DisplayState state, SKColor accent, Layout l)
    {
        var (headline, subline, progress) = StatusText.PosterLines(state);

        // Fade to near-black so the text reads on any artwork.
        using (var shade = new SKPaint())
        {
            shade.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, l.PanelTop),
                new SKPoint(0, l.H),
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 215), new SKColor(0, 0, 0, 240)],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, l.PanelTop, l.W, l.H - l.PanelTop, shade);
        }

        using var headlineFont = new SKFont(Bold.Value, 40 * l.K);
        using var sublineFont = new SKFont(Regular.Value, 30 * l.K);
        using var accentPaint = new SKPaint { Color = accent, IsAntialias = true };
        using var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var softPaint = new SKPaint { Color = new SKColor(255, 255, 255, 220), IsAntialias = true };

        var right = l.W - l.Margin;
        var percentText = state.Percent is int p && state.Status is TrackedStatus.Downloading or TrackedStatus.Stalled
            ? p.ToString(CultureInfo.InvariantCulture) + "%"
            : null;
        var percentWidth = percentText is null ? 0 : headlineFont.MeasureText(percentText) + (16 * l.K);

        var headlineY = l.H - (126 * l.K);
        canvas.DrawText(Ellipsize(headline, headlineFont, right - l.Margin - percentWidth), l.Margin, headlineY, SKTextAlign.Left, headlineFont, accentPaint);
        if (percentText is not null)
        {
            canvas.DrawText(percentText, right, headlineY, SKTextAlign.Right, headlineFont, whitePaint);
        }

        var bar = BarRect(l.W, l.H);
        var radius = bar.Height / 2;
        using (var track = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(progress is null ? 40 : 70)), IsAntialias = true })
        {
            canvas.DrawRoundRect(bar, radius, radius, track);
        }

        if (progress is double fraction && fraction > 0)
        {
            var filled = SKRect.Create(bar.Left, bar.Top, Math.Max(bar.Height, bar.Width * (float)Math.Clamp(fraction, 0, 1)), bar.Height);
            canvas.DrawRoundRect(filled, radius, radius, accentPaint);
        }

        canvas.DrawText(Ellipsize(subline, sublineFont, l.W - (2 * l.Margin)), l.Margin, l.H - (42 * l.K), SKTextAlign.Left, sublineFont, softPaint);
    }

    /// <summary>Where the progress bar is drawn (exposed for tests).</summary>
    public static SKRect BarRect(int w, int h)
    {
        var l = new Layout(w, h);
        return SKRect.Create(l.Margin, h - (102 * l.K), w - (2 * l.Margin), 20 * l.K);
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

    /// <summary>Sizes are designed for a 600-wide poster and a 540-high thumbnail; K scales them to fit both.</summary>
    private readonly record struct Layout(int W, int H)
    {
        public float K => Math.Min(W / 600f, H / 540f);

        public float Margin => 32 * K;

        public float PanelTop => H - Math.Min(300 * K, H * 0.55f);
    }
}
