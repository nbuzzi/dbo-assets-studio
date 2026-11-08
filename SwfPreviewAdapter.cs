using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DboAssetsStudio;

internal sealed record SwfPreview(byte[] Bytes, int EmbeddedImages, string[] MissingImages, int[] UnsupportedTags);
internal static class SwfPreviewAdapter
{
    private const int Limit = 128 * 1024 * 1024;
    // GFx DefineExternalImage (1001) is previewed as standard SWF DefineBitsLossless2 (36).
    // All changes exist only in this generated preview; the original PAK/SWF is untouched.
    public static async Task<SwfPreview> PrepareAsync(byte[] source, Func<string, Task<Bitmap?>> imageLoader, CancellationToken token)
    {
        if (source.Length < 9 || source.Length > Limit) throw new InvalidDataException("SWF truncado o demasiado grande.");
        string signature = Encoding.ASCII.GetString(source, 0, 3);
        uint declared = BitConverter.ToUInt32(source, 4);
        if (declared < 9 || declared > Limit) throw new InvalidDataException("Tamaño SWF descomprimido fuera de límites.");
        if (signature == "ZWS") return new SwfPreview(source, 0, [], []);
        if (signature is not ("FWS" or "CWS")) throw new InvalidDataException("Cabecera SWF no compatible.");
        byte[] body;
        if (signature == "CWS")
        {
            body = new byte[declared - 8]; using var input = new MemoryStream(source, 8, source.Length - 8);
            using var zip = new ZLibStream(input, CompressionMode.Decompress); await zip.ReadExactlyAsync(body, token);
            if (zip.ReadByte() != -1) throw new InvalidDataException("El contenido SWF supera el tamaño declarado.");
        }
        else { if (source.Length != declared) throw new InvalidDataException("La longitud SWF no coincide con la cabecera."); body = source[8..]; }
        int header = (5 + 4 * (body[0] >> 3) + 7) / 8 + 4;
        if (header > body.Length) throw new InvalidDataException("Stage SWF truncado.");
        int embedded = 0; long pixelBudget = 32L * 1024 * 1024;
        var missing = new List<string>(); var unsupported = new HashSet<int>();
        async Task<byte[]> Rewrite(byte[] input, int start, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Anidamiento SWF excesivo.");
            using var output = new MemoryStream(); output.Write(input, 0, start);
            int p = start;
            while (p < input.Length)
            {
                token.ThrowIfCancellationRequested();
                if (p + 2 > input.Length) throw new InvalidDataException("Tag SWF truncado.");
                ushort record = BitConverter.ToUInt16(input, p); p += 2; int tag = record >> 6; uint length = (uint)(record & 63);
                if (length == 63) { if (p + 4 > input.Length) throw new InvalidDataException("Tag SWF truncado."); length = BitConverter.ToUInt32(input, p); p += 4; }
                if ((long)p + length > input.Length) throw new InvalidDataException("Tag SWF fuera del archivo.");
                byte[] payload = input.AsSpan(p, (int)length).ToArray(); p += (int)length;
                if (tag == 1001)
                {
                    if (payload.Length < 10) throw new InvalidDataException("DefineExternalImage truncado.");
                    ushort id = BitConverter.ToUInt16(payload, 0), width = BitConverter.ToUInt16(payload, 4), height = BitConverter.ToUInt16(payload, 6);
                    int q = 8;
                    string ReadName() { if (q >= payload.Length) throw new InvalidDataException("Nombre GFx truncado."); int len = payload[q++]; if (q + len > payload.Length) throw new InvalidDataException("Nombre GFx truncado."); string value = Encoding.Latin1.GetString(payload, q, len); q += len; return value; }
                    ReadName(); string file = ReadName();
                    if (q != payload.Length || width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16_777_216) throw new InvalidDataException("DefineExternalImage no compatible.");
                    using var image = await imageLoader(file);
                    if (image == null) missing.Add(file);
                    else
                    {
                        pixelBudget -= (long)width * height;
                        if (pixelBudget < 0) throw new InvalidDataException("Las imágenes del SWF exceden el límite de vista previa.");
                        using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                        using (var graphics = Graphics.FromImage(resized)) { graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; graphics.DrawImage(image, new Rectangle(0, 0, width, height)); }
                        payload = EncodeLossless(id, resized); tag = 36; embedded++;
                    }
                }
                else if (tag == 39) { if (payload.Length < 4) throw new InvalidDataException("Sprite SWF truncado."); payload = await Rewrite(payload, 4, depth + 1); }
                else if (tag > 1000) unsupported.Add(tag);
                using (var writer = new BinaryWriter(output, Encoding.UTF8, true)) { writer.Write((ushort)((tag << 6) | 63)); writer.Write((uint)payload.Length); writer.Write(payload); }
                if (output.Length > Limit - 8) throw new InvalidDataException("Vista previa SWF mayor a 128 MiB.");
                if (tag == 0) { if (output.Length + input.Length - p > Limit - 8) throw new InvalidDataException("Vista SWF demasiado grande."); if (p != input.Length) output.Write(input, p, input.Length - p); break; }
            }
            return output.ToArray();
        }
        byte[] rewritten = await Rewrite(body, header, 0);
        using var result = new MemoryStream(); using var final = new BinaryWriter(result);
        final.Write(Encoding.ASCII.GetBytes("FWS")); final.Write(source[3]); final.Write((uint)(rewritten.Length + 8)); final.Write(rewritten);
        return new SwfPreview(result.ToArray(), embedded, missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), unsupported.Order().ToArray());
    }

    private static byte[] EncodeLossless(ushort id, Bitmap bitmap)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(id); writer.Write((byte)5); writer.Write((ushort)bitmap.Width); writer.Write((ushort)bitmap.Height); writer.Flush();
        var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var zip = new ZLibStream(output, CompressionLevel.Fastest, true);
            var bgra = new byte[bitmap.Width * 4]; var argb = new byte[bgra.Length];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), bgra, 0, bgra.Length);
                for (int x = 0; x < bgra.Length; x += 4) { int alpha = bgra[x + 3]; argb[x] = (byte)alpha; argb[x + 1] = (byte)((bgra[x + 2] * alpha + 127) / 255); argb[x + 2] = (byte)((bgra[x + 1] * alpha + 127) / 255); argb[x + 3] = (byte)((bgra[x] * alpha + 127) / 255); }
                zip.Write(argb);
            }
        }
        finally { bitmap.UnlockBits(bits); }
        return output.ToArray();
    }
}
