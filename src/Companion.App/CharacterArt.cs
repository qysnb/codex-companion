using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Buffers.Binary;
namespace CodexCompanion.App;

internal sealed record CharacterFrame(BitmapSource Image, byte[] Alpha)
{
    internal long Bytes => (long)Image.PixelWidth * Image.PixelHeight * 4 + Alpha.Length;
    internal static CharacterFrame FromImage(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Bgra32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(bytes, source.PixelWidth * 4, 0);
        var alpha = new byte[bytes.Length / 4];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = bytes[i * 4 + 3];
        if (image.CanFreeze) image.Freeze();
        return new(image, alpha);
    }
}

internal static class CharacterArt
{
    internal static double HeadFraction(string character) => character switch
    {
        "elegant" or "fable" => .23,
        "mischievous" => .29,
        "pro" => .26,
        _ => .54
    };
#if DEBUG
    readonly record struct Subject(int Id, int Count, int Left, int Top, int Right, int Bottom);
#endif

    internal static BitmapSource DecodeStrip(string path, int height)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24]; stream.ReadExactly(header); stream.Position = 0;
        var sourceWidth = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var sourceHeight = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth % 3 != 0) throw new InvalidDataException("Invalid registered PNG strip dimensions.");
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelHeight = height;
        // Force exact cell boundaries after WIC resizing; rounded strip widths
        // must not introduce an extra one-pixel shift in the third cell.
        bitmap.DecodePixelWidth = 3 * Math.Max(1, (int)Math.Round(sourceWidth / 3d * height / sourceHeight));
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    internal static CharacterFrame[] SharedFrames(BitmapSource sheet)
    {
        if (sheet.PixelWidth % 3 != 0) throw new InvalidDataException("Registered frame boundaries must be exact.");
        var result = new CharacterFrame[3]; var width = sheet.PixelWidth / 3;
        for (var i = 0; i < 3; i++)
        {
            var crop = new CroppedBitmap(sheet, new Int32Rect(i * width, 0, width, sheet.PixelHeight)); crop.Freeze();
            result[i] = CharacterFrame.FromImage(crop);
        }
        return result;
    }

#if DEBUG
    internal static CharacterFrame[] PrepareFrames(BitmapSource sheet, string character, int outputHeight = 0)
    {
        var source = new FormatConvertedBitmap(sheet, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth; var height = source.PixelHeight;
        var rgba = new byte[checked(width * height * 4)]; source.CopyPixels(rgba, width * 4, 0);
        var labels = new int[width * height]; var queue = new int[labels.Length];
        var subjects = new List<Subject>(3); var label = 0; var tail = 0;
        void Add(int index)
        {
            if (labels[index] != 0 || rgba[index * 4 + 3] <= 32) return;
            labels[index] = label; queue[tail++] = index;
        }
        // Equal-width cropping is unsafe: Astra's adjacent frames have some
        // overlapping X ranges. Separate the complete silhouettes instead.
        for (var index = 0; index < labels.Length; index++)
        {
            if (labels[index] != 0 || rgba[index * 4 + 3] <= 32) continue;
            label++; tail = 0; Add(index);
            var left = width; var right = 0; var top = height; var bottom = 0;
            for (var head = 0; head < tail; head++)
            {
                var pixel = queue[head]; var y = pixel / width; var x = pixel - y * width;
                left = Math.Min(left, x); right = Math.Max(right, x + 1); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
                if (x > 0) Add(pixel - 1); if (x + 1 < width) Add(pixel + 1);
                if (y > 0) Add(pixel - width); if (y + 1 < height) Add(pixel + width);
            }
            if (subjects.Count == 3 && tail <= subjects[^1].Count) continue;
            subjects.Add(new(label, tail, left, top, right, bottom));
            subjects.Sort((a, b) => b.Count.CompareTo(a.Count));
            if (subjects.Count > 3) subjects.RemoveAt(3);
        }
        if (subjects.Count != 3 || subjects[2].Count < subjects[0].Count * .55)
            throw new InvalidDataException("Character strip must contain three separate complete figures.");
        subjects.Sort((a, b) => a.Left.CompareTo(b.Left));
        var fraction = HeadFraction(character);
        double HeadCenter(Subject subject)
        {
            double sum = 0, weight = 0;
            var limit = subject.Top + (int)((subject.Bottom - subject.Top) * fraction);
            for (var y = subject.Top; y < limit; y++) for (var x = subject.Left; x < subject.Right; x++)
            {
                var pixel = y * width + x;
                if (labels[pixel] != subject.Id) continue;
                var alpha = rgba[pixel * 4 + 3]; sum += x * alpha; weight += alpha;
            }
            return sum / weight;
        }
        var reference = subjects[0]; var anchor = HeadCenter(reference);
        var scales = new double[3]; var offsetX = new double[3]; var offsetY = new double[3];
        var cellWidth = width / 3; double extra = 0;
        for (var i = 0; i < 3; i++)
        {
            var item = subjects[i];
            scales[i] = (double)(reference.Bottom - reference.Top) / (item.Bottom - item.Top);
            offsetX[i] = anchor - HeadCenter(item) * scales[i];
            offsetY[i] = reference.Bottom - item.Bottom * scales[i];
            extra = Math.Max(extra, Math.Max(2 - (item.Left * scales[i] + offsetX[i]), item.Right * scales[i] + offsetX[i] + 2 - cellWidth));
        }
        // Symmetric padding preserves the neutral character's screen position
        // when the Image control centers the wider complete canvas.
        var padding = (int)Math.Ceiling(extra);
        var rasterScale = (double)(outputHeight > 0 ? outputHeight : height) / height;
        var outputWidth = (int)Math.Ceiling((cellWidth + padding * 2) * rasterScale);
        var outputPixelHeight = (int)Math.Round(height * rasterScale);
        bool NearSubject(int x, int y, int id)
        {
            for (var yy = Math.Max(0, y - 2); yy <= Math.Min(height - 1, y + 2); yy++)
                for (var xx = Math.Max(0, x - 2); xx <= Math.Min(width - 1, x + 2); xx++)
                    if (labels[yy * width + xx] == id) return true;
            return false;
        }
        var result = new CharacterFrame[3];
        for (var i = 0; i < 3; i++)
        {
            var item = subjects[i];
            var left = Math.Max(0, item.Left - 2); var top = Math.Max(0, item.Top - 2);
            var cropWidth = Math.Min(width, item.Right + 2) - left; var cropHeight = Math.Min(height, item.Bottom + 2) - top;
            var cropped = new byte[cropWidth * cropHeight * 4];
            for (var y = 0; y < cropHeight; y++) for (var x = 0; x < cropWidth; x++)
            {
                var pixel = (y + top) * width + x + left;
                if (rgba[pixel * 4 + 3] == 0) continue;
                var belongsToOther = labels[pixel] == subjects[(i + 1) % 3].Id || labels[pixel] == subjects[(i + 2) % 3].Id;
                if (labels[pixel] != item.Id && (belongsToOther || !NearSubject(x + left, y + top, item.Id))) continue;
                Buffer.BlockCopy(rgba, pixel * 4, cropped, (y * cropWidth + x) * 4, 4);
            }
            var bitmap = BitmapSource.Create(cropWidth, cropHeight, 96, 96, PixelFormats.Bgra32, null, cropped, cropWidth * 4); bitmap.Freeze();
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(bitmap, new Rect((left * scales[i] + offsetX[i] + padding) * rasterScale, (top * scales[i] + offsetY[i]) * rasterScale, cropWidth * scales[i] * rasterScale, cropHeight * scales[i] * rasterScale));
            var aligned = new RenderTargetBitmap(outputWidth, outputPixelHeight, 96, 96, PixelFormats.Pbgra32); aligned.Render(visual);
            result[i] = CharacterFrame.FromImage(aligned);
        }
        return result;
    }
#endif
}
