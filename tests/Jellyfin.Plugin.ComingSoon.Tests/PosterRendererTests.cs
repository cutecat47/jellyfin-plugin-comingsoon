using System;
using System.IO;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Tracking;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class PosterRendererTests
{
    /// <summary>Set COMINGSOON_POSTER_OUT to a folder to save the rendered samples for a visual check.</summary>
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("COMINGSOON_POSTER_OUT");

    private static byte[] SamplePoster()
    {
        // A stand-in "real" poster: 500x750 with a colourful gradient and some shapes.
        using var surface = SKSurface.Create(new SKImageInfo(500, 750));
        using var paint = new SKPaint();
        paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(500, 750), [new SKColor(0x2B, 0x58, 0x76), new SKColor(0xD9, 0x7B, 0x29)], SKShaderTileMode.Clamp);
        surface.Canvas.DrawRect(0, 0, 500, 750, paint);
        using var circle = new SKPaint { Color = new SKColor(255, 240, 200), IsAntialias = true };
        surface.Canvas.DrawCircle(250, 300, 120, circle);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] jpeg) => SKBitmap.Decode(jpeg);

    private static void Save(string name, byte[] jpeg)
    {
        if (!string.IsNullOrEmpty(OutDir))
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllBytes(Path.Combine(OutDir, name + ".jpg"), jpeg);
        }
    }

    [Theory]
    [InlineData(TrackedStatus.Downloading, 60, EtaBucket.FewHours, null, true)]
    [InlineData(TrackedStatus.Stalled, 45, EtaBucket.Unknown, "The download is stalled with no connections", true)]
    [InlineData(TrackedStatus.Importing, null, EtaBucket.Unknown, null, true)]
    [InlineData(TrackedStatus.Queued, null, EtaBucket.Unknown, "Paused", false)]
    [InlineData(TrackedStatus.Searching, null, EtaBucket.Unknown, "3 of 8 episodes", false)]
    [InlineData(TrackedStatus.WaitingForRelease, null, EtaBucket.Unknown, null, false)]
    public void Renders_EveryStatus_WithAndWithoutArtwork(TrackedStatus status, int? percent, EtaBucket eta, string? detail, bool withPoster)
    {
        var state = new DisplayState(status, percent, eta, detail, status == TrackedStatus.WaitingForRelease ? new DateOnly(2027, 3, 5) : null);

        var jpeg = PosterRenderer.Render(withPoster ? SamplePoster() : null, "Severance - Season 2", state);
        Save($"{status}-{(withPoster ? "poster" : "titlecard")}", jpeg);

        using var bitmap = Decode(jpeg);
        Assert.Equal(PosterRenderer.Width, bitmap.Width);
        Assert.Equal(PosterRenderer.Height, bitmap.Height);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(60)]
    [InlineData(90)]
    public void ProgressBar_IsFilledToThePercentage(int percent)
    {
        var state = new DisplayState(TrackedStatus.Downloading, percent, EtaBucket.FewHours, null, null);
        using var bitmap = Decode(PosterRenderer.Render(SamplePoster(), "Project Hail Mary", state));

        // Bar spans x = 32..568 at y = Height-102 .. Height-82.
        var y = PosterRenderer.Height - 92;
        var barWidth = PosterRenderer.Width - 64;
        var insideX = 32 + (int)(barWidth * (percent / 100.0)) - 12;
        var outsideX = 32 + (int)(barWidth * (percent / 100.0)) + 12;

        var accent = PosterRenderer.AccentFor(TrackedStatus.Downloading);
        Assert.True(Close(bitmap.GetPixel(insideX, y), accent), $"pixel at {insideX} should be the accent colour");
        Assert.False(Close(bitmap.GetPixel(outsideX, y), accent), $"pixel at {outsideX} should be the empty track");
    }

    [Fact]
    public void BrokenArtwork_FallsBackToTitleCard()
    {
        var state = new DisplayState(TrackedStatus.Searching, null, EtaBucket.Unknown, null, null);
        var jpeg = PosterRenderer.Render([1, 2, 3, 4], "A Very Long Movie Title That Has To Wrap Across Several Lines Of The Card", state);
        Save("Searching-brokenart-longtitle", jpeg);
        using var bitmap = Decode(jpeg);
        Assert.Equal(PosterRenderer.Height, bitmap.Height);
    }

    private static bool Close(SKColor a, SKColor b)
        => Math.Abs(a.Red - b.Red) < 40 && Math.Abs(a.Green - b.Green) < 40 && Math.Abs(a.Blue - b.Blue) < 40;
}
