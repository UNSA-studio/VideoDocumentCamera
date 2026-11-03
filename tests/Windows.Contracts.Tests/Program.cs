using VideoPresenter.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

static void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    Console.WriteLine("PASS " + label);
}
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run these contracts on Windows, not a mocked WinRT runtime.");
foreach (var (width, height) in new[] { (4, 3), (13, 5) })
{
    var pixels = new byte[width * height * 4];
    for (int i = 0; i < pixels.Length; i += 4)
    {
        pixels[i] = (byte)(i * 3 % 256);
        pixels[i + 1] = (byte)(i * 7 % 256);
        pixels[i + 2] = (byte)(i * 11 % 256);
        pixels[i + 3] = 255;
    }
    using var bitmap = BitmapPixels.FromBgra(pixels, width, height);
    Check(BitmapPixels.ReadBgra(bitmap).SequenceEqual(pixels), $"BGRA buffer byte order/length {width}x{height}");
    foreach (bool jpeg in new[] { false, true })
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (jpeg)
        {
            var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.95f, Windows.Foundation.PropertyType.Single) };
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties);
        }
        else encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        Check(stream.Size > 0, $"{(jpeg ? "JPEG" : "PNG")} encoder produced bytes {width}x{height}");
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var decoded = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        Check(decoded.PixelWidth == width && decoded.PixelHeight == height, $"encoded image decodes with original dimensions, jpeg={jpeg}");
        if (!jpeg) Check(BitmapPixels.ReadBgra(decoded).SequenceEqual(pixels), "PNG lossless pixel roundtrip");
    }
}
using (var straight = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 2, 2, BitmapAlphaMode.Straight))
{
    var pixels = Enumerable.Repeat(new byte[] { 200, 100, 50, 128 }, 4).SelectMany(x => x).ToArray();
    using var writer = new DataWriter();
    writer.WriteBytes(pixels);
    straight.CopyFromBuffer(writer.DetachBuffer());
    using var converted = SoftwareBitmap.Convert(straight, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    var result = BitmapPixels.ReadBgra(converted);
    Check(Math.Abs(result[0] - 100) <= 1 && Math.Abs(result[1] - 50) <= 1 && Math.Abs(result[2] - 25) <= 1 && result[3] == 128,
        "BGRA straight-to-premultiplied alpha conversion");
}
bool rejected = false;
try { using var invalid = BitmapPixels.FromBgra(new byte[3], 2, 2); }
catch (ArgumentException) { rejected = true; }
Check(rejected, "reject invalid BGRA byte count");

string directory = Path.Combine(AppContext.BaseDirectory, "Users", "WindowsContracts", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var folder = await StorageFolder.GetFolderFromPathAsync(directory);
    var old = await folder.CreateFileAsync("photo.png", CreationCollisionOption.FailIfExists);
    await FileIO.WriteTextAsync(old, "original-not-overwritten");
    var staged = await folder.CreateFileAsync(".pending-test.png", CreationCollisionOption.FailIfExists);
    await FileIO.WriteTextAsync(staged, "new-photo");
    await staged.RenameAsync("photo.png", NameCollisionOption.GenerateUniqueName);
    Check(await FileIO.ReadTextAsync(old) == "original-not-overwritten", "publishing colliding photo preserves old file");
    Check(!string.Equals(old.Path, staged.Path, StringComparison.OrdinalIgnoreCase) && await FileIO.ReadTextAsync(staged) == "new-photo",
        "publishing uses actual unique file path");
    Check(!File.Exists(Path.Combine(directory, ".pending-test.png")), "temporary photo name disappears after successful publish");
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine("ALL WINDOWS BITMAP/STORAGE CONTRACTS PASSED (NOT A CAMERA OR WINUI DISPLAY TEST)");