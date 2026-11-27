using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NexNet.Inspector.App.Checks;

/// <summary>Reads pixels of a rendered frame (32-bit BGRA or RGBA, as the headless Skia renderer produces).</summary>
public static class FramePixels
{
    /// <summary>The colour at (<paramref name="x"/>, <paramref name="y"/>) in device pixels.</summary>
    public static Color At(WriteableBitmap frame, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using ILockedFramebuffer buffer = frame.Lock();
        return Read(buffer, x, y);
    }

    /// <summary>
    /// Whether the frame holds at least <paramref name="minimum"/> distinct colours, sampled every
    /// <paramref name="step"/> pixels: a blank (single-colour) frame means nothing was drawn.
    /// </summary>
    public static int DistinctColours(WriteableBitmap frame, int step = 4, int minimum = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using ILockedFramebuffer buffer = frame.Lock();
        HashSet<uint> seen = [];
        for (int y = 0; y < buffer.Size.Height; y += step)
        {
            for (int x = 0; x < buffer.Size.Width; x += step)
            {
                seen.Add(Read(buffer, x, y).ToUInt32());
                if (seen.Count >= minimum) return seen.Count;
            }
        }

        return seen.Count;
    }

    private static Color Read(ILockedFramebuffer buffer, int x, int y)
    {
        if ((uint)x >= (uint)buffer.Size.Width || (uint)y >= (uint)buffer.Size.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {buffer.Size.Width}x{buffer.Size.Height} frame");

        int value = Marshal.ReadInt32(buffer.Address, (y * buffer.RowBytes) + (x * 4));
        byte b0 = (byte)value, b1 = (byte)(value >> 8), b2 = (byte)(value >> 16), a = (byte)(value >> 24);
        return buffer.Format == PixelFormats.Rgba8888 ? Color.FromArgb(a, b0, b1, b2) : Color.FromArgb(a, b2, b1, b0);
    }
}
