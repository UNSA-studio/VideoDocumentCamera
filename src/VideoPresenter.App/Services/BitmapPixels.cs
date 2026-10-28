// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VideoPresenter.App.Services;

/// <summary>Use projected SoftwareBitmap buffer APIs instead of an unverified, hand-written COM interface.</summary>
internal static class BitmapPixels
{
    public static byte[] ReadBgra(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            throw new ArgumentException("ReadBgra requires a normalized BGRA8 bitmap.");
        int count = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        var buffer = new Windows.Storage.Streams.Buffer((uint)count);
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[count];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);
        return pixels;
    }

    public static SoftwareBitmap FromBgra(byte[] bytes, int width, int height)
    {
        if (bytes.Length != checked(width * height * 4)) throw new ArgumentException("Invalid pixel count.");
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        try { bitmap.CopyFromBuffer(writer.DetachBuffer()); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }
}