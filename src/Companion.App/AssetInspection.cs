using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal static class AssetInspection
{
    internal static object Check()
    {
        var items = new List<object>();
        foreach (var name in CompanionSettings.Characters)
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "assets", "characters", name, "registered.png"));
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
            var transparent = 0; var opaque = 0;
            for (var i = 3; i < pixels.Length; i += 4) { if (pixels[i] == 0) transparent++; if (pixels[i] == 255) opaque++; }
            if (transparent == 0 || opaque == 0 || pixels[3] != 0 || pixels[^1] != 0) throw new InvalidDataException("Invalid transparent character sheet: " + name);
            var registrations = new List<object>();
            var heights = CompanionSettings.Sizes.SelectMany(size => new[] { 1d, 1.25, 1.5, 2, 3 }.Select(scale => ResourcePolicy.DecodeHeight(size.Height, scale))).Distinct().Order();
            foreach (var height in heights)
            {
                var small = CharacterArt.DecodeStrip(Path.Combine(AppContext.BaseDirectory, "assets", "characters", name, "registered.png"), height);
                var frames = CharacterArt.SharedFrames(small);
                var centers = frames.Select(item => HeadCenter(item, name)).ToArray();
                var headSpread = centers.Max(item => item.X) - centers.Min(item => item.X);
                var baselineSpread = centers.Max(item => item.Bottom) - centers.Min(item => item.Bottom);
                if (frames.Length != 3 || frames.Any(item => !item.Image.IsFrozen || item.Image.PixelHeight != height || item.Image.PixelWidth > height || item.Alpha[0] != 0 || item.Alpha[^1] != 0)) throw new InvalidDataException("Invalid registered whole frames: " + name);
                if (headSpread > 1.5 || baselineSpread > 1) throw new InvalidDataException($"Frame registration is not stable: {name}/{height}, head={headSpread:0.00}px, baseline={baselineSpread}px.");
                if (frames.Sum(item => item.Bytes) > 15L * height * height) throw new InvalidDataException("Frame cache exceeded its pixel budget.");
                if (frames.Any(item => item.Image is not CroppedBitmap crop || !ReferenceEquals(crop.Source, small))) throw new InvalidDataException("Frames must share one decoded strip.");
                registrations.Add(new { height, width = frames[0].Image.PixelWidth, headSpreadPixels = headSpread, baselineSpreadPixels = baselineSpread, cacheBytes = frames.Sum(item => item.Bytes), sharedDecode = true });
            }
            items.Add(new { character = name, displayName = CharacterNames.Get(name), width = frame.PixelWidth, height = frame.PixelHeight, sourceFrames = 3, runtimeFrames = 3, transparentPixels = transparent, opaquePixels = opaque, wholeCharacterVerified = true, registrations });
        }
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "dialogue.json")));
        var total = 0;
        foreach (var person in CompanionSettings.Characters) foreach (var lang in CompanionSettings.Languages)
        {
            var lines = document.RootElement.GetProperty(person).GetProperty(lang).EnumerateArray().Select(x => x.GetString()).ToArray();
            if (lines.Length < 30 || lines.Any(string.IsNullOrWhiteSpace) || lines.Distinct().Count() != lines.Length) throw new InvalidDataException("Invalid dialogue group.");
            total += lines.Length;
        }
        return new { ok = true, sheets = items, dialogueLines = total };
    }

#if DEBUG
    internal static object PrepareArt(string root, string? onlyCharacter = null)
    {
        var outputs = new List<object>();
        foreach (var name in CompanionSettings.Characters)
        {
            if (onlyCharacter is not null && name != onlyCharacter) continue;
            var directory = Path.Combine(Path.GetFullPath(root), "assets", "characters", name);
            using var input = File.OpenRead(Path.Combine(directory, "sheet.png"));
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = CharacterArt.PrepareFrames(decoder.Frames[0], name);
            var width = frames[0].Image.PixelWidth; var height = frames[0].Image.PixelHeight;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                for (var i = 0; i < 3; i++) drawing.DrawImage(frames[i].Image, new System.Windows.Rect(i * width, 0, width, height));
            var strip = new RenderTargetBitmap(width * 3, height, 96, 96, PixelFormats.Pbgra32); strip.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(strip));
            var path = Path.Combine(directory, "registered.png");
            using (var output = File.Create(path)) encoder.Save(output);
            outputs.Add(new { character = name, path, width = width * 3, height });
        }
        return new { ok = true, outputs };
    }
#endif

    static (double X, int Bottom) HeadCenter(CharacterFrame frame, string character)
    {
        var width = frame.Image.PixelWidth; var height = frame.Image.PixelHeight;
        var top = height; var bottom = 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            if (frame.Alpha[y * width + x] > 32) { top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1); }
        var fraction = CharacterArt.HeadFraction(character);
        var limit = top + (int)((bottom - top) * fraction);
        double sum = 0, weight = 0;
        for (var y = top; y < limit; y++) for (var x = 0; x < width; x++)
        { var alpha = frame.Alpha[y * width + x]; sum += x * alpha; weight += alpha; }
        return (sum / weight, bottom);
    }
}
