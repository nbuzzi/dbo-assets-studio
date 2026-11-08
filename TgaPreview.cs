using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DboAssetsStudio;

internal static class TgaPreview
{
    private const int HeaderSize = 18;
    private const int MaxDimension = 8192;
    private const long MaxPixels = 16_777_216;
    private const int MaxInputBytes = 128 * 1024 * 1024;

    public static Bitmap Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxInputBytes) throw new InvalidDataException("TGA demasiado grande para vista previa (m谩ximo 128 MiB).");
        if (bytes.Length < HeaderSize) throw new InvalidDataException("Cabecera TGA truncada.");

        int idLength = bytes[0];
        int colorMapType = bytes[1];
        int imageType = bytes[2];
        int colorMapFirst = ReadU16(bytes, 3);
        int colorMapLength = ReadU16(bytes, 5);
        int colorMapDepth = bytes[7];
        int width = ReadU16(bytes, 12);
        int height = ReadU16(bytes, 14);
        int pixelDepth = bytes[16];
        int descriptor = bytes[17];

        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || (long)width * height > MaxPixels)
            throw new InvalidDataException("Dimensiones TGA rechazadas por seguridad.");
        if ((descriptor & 0xC0) != 0) throw new InvalidDataException("TGA con entrelazado no est谩 soportado.");
        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11)) throw new InvalidDataException($"Tipo de imagen TGA {imageType} no soportado.");

        bool indexed = imageType is 1 or 9;
        bool trueColor = imageType is 2 or 10;
        bool rle = imageType is 9 or 10 or 11;
        if (indexed)
        {
            if (colorMapType != 1 || colorMapLength == 0 || pixelDepth is not (8 or 16) || colorMapDepth is not (16 or 24 or 32))
                throw new InvalidDataException("TGA indexado requiere mapa de color y entradas de 16/24/32 bits con 铆ndices de 8/16 bits.");
            if ((long)colorMapFirst + colorMapLength > 65_536) throw new InvalidDataException("Rango de 铆ndices de paleta TGA inv谩lido.");
        }
        else if (trueColor)
        {
            if (colorMapType != 0 || pixelDepth is not (16 or 24 or 32))
                throw new InvalidDataException("TGA truecolor soporta 16/24/32 bpp sin mapa de color.");
        }
        else
        {
            if (colorMapType != 0 || pixelDepth is not (8 or 16))
                throw new InvalidDataException("TGA grayscale soporta 8/16 bits sin mapa de color.");
        }

        int p = HeaderSize;
        Require(bytes, p, idLength, "Identificador TGA truncado.");
        p += idLength;
        Color[]? palette = null;
        if (indexed)
        {
            int entrySize = (colorMapDepth + 7) / 8;
            int paletteBytes = checked(colorMapLength * entrySize);
            Require(bytes, p, paletteBytes, "Paleta TGA truncada.");
            palette = new Color[colorMapLength];
            for (int i = 0; i < palette.Length; i++) palette[i] = ReadColor(bytes, p + i * entrySize, colorMapDepth, (descriptor & 0x0F) != 0);
            p += paletteBytes;
        }

        int count = checked(width * height);
        var colors = new Color[count];
        int sourceBytesPerPixel = (pixelDepth + 7) / 8;
        int pixel = 0;
        while (pixel < count)
        {
            int run = 1;
            bool repeated = false;
            if (rle)
            {
                Require(bytes, p, 1, "Paquetes RLE TGA truncados.");
                int packet = bytes[p++];
                run = (packet & 0x7F) + 1;
                repeated = (packet & 0x80) != 0;
                if (run > count - pixel) throw new InvalidDataException("Paquete RLE TGA excede el n煤mero de p铆xeles.");
            }

            if (repeated)
            {
                Color c = ReadPixel(bytes, ref p, indexed, trueColor, pixelDepth, colorMapFirst, palette, descriptor);
                Array.Fill(colors, c, pixel, run);
                pixel += run;
            }
            else
            {
                int needed = checked(run * sourceBytesPerPixel);
                Require(bytes, p, needed, "P铆xeles TGA truncados.");
                for (int i = 0; i < run; i++) colors[pixel++] = ReadPixel(bytes, ref p, indexed, trueColor, pixelDepth, colorMapFirst, palette, descriptor);
            }
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var rect = new Rectangle(0, 0, width, height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] row = new byte[checked(width * 4)];
                bool topOrigin = (descriptor & 0x20) != 0;
                bool rightOrigin = (descriptor & 0x10) != 0;
                for (int y = 0; y < height; y++)
                {
                    int sourceY = topOrigin ? y : height - 1 - y;
                    for (int x = 0; x < width; x++)
                    {
                        int sourceX = rightOrigin ? width - 1 - x : x;
                        Color c = colors[sourceY * width + sourceX];
                        int q = x * 4;
                        row[q] = c.B; row[q + 1] = c.G; row[q + 2] = c.R; row[q + 3] = c.A;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * stride), row.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Color ReadPixel(byte[] bytes, ref int p, bool indexed, bool trueColor, int depth, int paletteFirst, Color[]? palette, int descriptor)
    {
        Require(bytes, p, (depth + 7) / 8, "Píxel TGA truncado.");
        if (indexed)
        {
            int index = depth == 8 ? bytes[p++] : ReadU16(bytes, p);
            if (depth == 16) p += 2;
            int slot = index - paletteFirst;
            if (palette is null || (uint)slot >= (uint)palette.Length) throw new InvalidDataException($"脥ndice de paleta TGA fuera de rango: {index}.");
            return palette[slot];
        }
        if (trueColor) { Color c = ReadColor(bytes, p, depth, (descriptor & 0x0F) != 0); p += (depth + 7) / 8; return c; }
        byte gray = bytes[p++];
        byte alpha = 255;
        if (depth == 16) { byte stored = bytes[p++]; if ((descriptor & 15) != 0) alpha = stored; }
        return Color.FromArgb(alpha, gray, gray, gray);
    }

    private static Color ReadColor(byte[] bytes, int p, int depth, bool alphaDeclared)
    {
        if (depth == 16)
        {
            int value = ReadU16(bytes, p);
            int b = Expand5(value & 31), g = Expand5((value >> 5) & 31), r = Expand5((value >> 10) & 31);
            int a = alphaDeclared ? ((value & 0x8000) != 0 ? 255 : 0) : 255;
            return Color.FromArgb(a, r, g, b);
        }
        byte blue = bytes[p], green = bytes[p + 1], red = bytes[p + 2];
        byte alpha = depth == 32 && alphaDeclared ? bytes[p + 3] : (byte)255;
        return Color.FromArgb(alpha, red, green, blue);
    }

    private static int Expand5(int value) => (value << 3) | (value >> 2);
    private static int ReadU16(byte[] bytes, int p) => bytes[p] | (bytes[p + 1] << 8);
    private static void Require(byte[] bytes, int offset, int count, string message)
    {
        if (offset < 0 || count < 0 || (long)offset + count > bytes.Length) throw new InvalidDataException(message);
    }
}
