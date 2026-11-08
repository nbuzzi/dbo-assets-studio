using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DboAssetsStudio;

internal static class DdsPreview
{
    public static Bitmap Load(string path)
    {
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("Archivo demasiado grande para vista previa (máximo 128 MiB).");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 128 || bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S' || bytes[3] != ' ')
            throw new InvalidDataException("El recurso no tiene una cabecera DDS válida.");
        if (BitConverter.ToUInt32(bytes, 4) != 124 || BitConverter.ToUInt32(bytes, 76) != 32)
            throw new InvalidDataException("Cabecera DDS no compatible.");

        int height = checked((int)BitConverter.ToUInt32(bytes, 12));
        int width = checked((int)BitConverter.ToUInt32(bytes, 16));
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216)
            throw new InvalidDataException("Dimensiones DDS rechazadas por seguridad.");
        uint pixelFlags = BitConverter.ToUInt32(bytes, 80);
        int data = 128;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        if ((pixelFlags & 0x4) != 0)
        {
            string format = System.Text.Encoding.ASCII.GetString(bytes, 84, 4);
            if (format is not ("DXT1" or "DXT3" or "DXT5"))
            {
                bitmap.Dispose();
                throw new InvalidDataException($"DDS {format} aún no está habilitado para vista previa.");
            }
            try { DecodeBlocks(bytes, data, bitmap, format); } catch { bitmap.Dispose(); throw; }
            return bitmap;
        }

        if ((pixelFlags & 0x40) == 0)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Vista previa DDS: solo formatos RGBA/BGRA sin compresión o DXT1/3/5.");
        }

        int bpp = checked((int)BitConverter.ToUInt32(bytes, 88));
        if (bpp != 32)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"DDS sin compresión con {bpp} bpp no está soportado.");
        }
        uint rMask = BitConverter.ToUInt32(bytes, 92), gMask = BitConverter.ToUInt32(bytes, 96), bMask = BitConverter.ToUInt32(bytes, 100), aMask = BitConverter.ToUInt32(bytes, 104);
        int stride = checked(width * 4);
        if (data + (long)stride * height > bytes.Length)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Payload DDS truncado.");
        }
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            uint c = BitConverter.ToUInt32(bytes, data + y * stride + x * 4);
            bitmap.SetPixel(x, y, Color.FromArgb(Channel(c, aMask, 255), Channel(c, rMask, 0), Channel(c, gMask, 0), Channel(c, bMask, 0)));
        }
        return bitmap;
    }

    private static int Channel(uint packed, uint mask, int fallback)
    {
        if (mask == 0) return fallback;
        int shift = BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (int)(((packed & mask) >> shift) * 255UL / max);
    }

    private static void DecodeBlocks(byte[] input, int data, Bitmap output, string format)
    {
        int blockBytes = format == "DXT1" ? 8 : 16;
        int blocksX = (output.Width + 3) / 4, blocksY = (output.Height + 3) / 4;
        if (data + (long)blocksX * blocksY * blockBytes > input.Length) throw new InvalidDataException("Payload DXT truncado.");
        int p = data;
        for (int by = 0; by < blocksY; by++) for (int bx = 0; bx < blocksX; bx++)
        {
            byte[] alpha = Enumerable.Repeat((byte)255, 16).ToArray();
            if (format == "DXT3")
            {
                for (int i = 0; i < 8; i++)
                {
                    byte ab = input[p + i]; alpha[i * 2] = (byte)((ab & 15) * 17); alpha[i * 2 + 1] = (byte)((ab >> 4) * 17);
                }
                p += 8;
            }
            else if (format == "DXT5")
            {
                byte a0 = input[p], a1 = input[p + 1];
                var palette = new byte[8]; palette[0] = a0; palette[1] = a1;
                if (a0 > a1) for (int i = 1; i <= 6; i++) palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                else { for (int i = 1; i <= 4; i++) palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5); palette[6] = 0; palette[7] = 255; }
                ulong selectors = 0; for (int i = 0; i < 6; i++) selectors |= (ulong)input[p + 2 + i] << (8 * i);
                for (int i = 0; i < 16; i++) alpha[i] = palette[(int)((selectors >> (3 * i)) & 7)];
                p += 8;
            }
            ushort c0 = BitConverter.ToUInt16(input, p), c1 = BitConverter.ToUInt16(input, p + 2);
            uint colorBits = BitConverter.ToUInt32(input, p + 4); p += 8;
            Color[] colors = MakeColorPalette(c0, c1, format == "DXT1");
            for (int i = 0; i < 16; i++)
            {
                int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                if (x >= output.Width || y >= output.Height) continue;
                int ci = (int)((colorBits >> (2 * i)) & 3);
                Color c = colors[ci];
                int a = format == "DXT1" && c.A == 0 ? 0 : alpha[i];
                output.SetPixel(x, y, Color.FromArgb(a, c.R, c.G, c.B));
            }
        }
    }

    private static Color[] MakeColorPalette(ushort c0, ushort c1, bool dxt1)
    {
        Color a = From565(c0), b = From565(c1);
        if (!dxt1 || c0 > c1)
            return [a, b, Mix(a, b, 2, 1, 3), Mix(a, b, 1, 2, 3)];
        return [a, b, Mix(a, b, 1, 1, 2), Color.FromArgb(0, 0, 0, 0)];
    }
    private static Color From565(ushort value) => Color.FromArgb(255, ((value >> 11) & 31) * 255 / 31, ((value >> 5) & 63) * 255 / 63, (value & 31) * 255 / 31);
    private static Color Mix(Color a, Color b, int x, int y, int divisor) => Color.FromArgb(255, (x * a.R + y * b.R) / divisor, (x * a.G + y * b.G) / divisor, (x * a.B + y * b.B) / divisor);
}
