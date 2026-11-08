using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace DboAssetsStudio;

internal static class AssetDependencyCatalog
{
    internal sealed record TextureRequest(string NameHex, string Name, string MaskHex, string Mask, bool ResourcePathSafe);
    public static async Task<object> InspectAsync(string mode, string path, bool rawNames = false)
    {
        string source = Path.GetFullPath(path); ContentSafety.NoLinks(source);
        string ext = Path.GetExtension(source).ToLowerInvariant();
        if (new FileInfo(source).Length is < 1 or > 128L * 1024 * 1024 ||
            (mode == "model-dependencies" ? ext != ".dff" : mode == "sound" ? ext is not (".wav" or ".mp3" or ".ogg") : ext is not (".dds" or ".png" or ".bmp")))
            throw new InvalidDataException("Modelo DFF o textura DDS/PNG/BMP requerida; límite128MiB.");
        string before = ContentSafety.Hash(source);
        string helper = Path.Combine(AppContext.BaseDirectory, "media-helper.exe"); ContentSafety.NoLinks(helper);
        string helperHash = ContentSafety.Hash(helper);
        byte[] raw = await File.ReadAllBytesAsync(source);
        if (Convert.ToHexString(SHA256.HashData(raw)) != before || ContentSafety.Hash(source) != before)
            throw new IOException("Fuente modificada durante snapshot.");
        byte[] native = await NativeMedia.ConvertAsync(mode == "sound" ? "audio" : mode, raw, ext);
        if (ContentSafety.Hash(source) != before || ContentSafety.Hash(helper) != helperHash)
            throw new IOException("Fuente/helper modificados durante inspección.");
        if (mode == "sound")
        {
            // The pinned native helper emits a complete, canonical PCM16 WAV
            // after a full FMOD read, with output type NOSOUND. Never play it.
            if (native.Length <= 44 || !native.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !native.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) || !native.AsSpan(36, 4).SequenceEqual("data"u8))
                throw new InvalidDataException("Native FMOD PCM header invalid.");
            uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(offset, 4));
            ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(native.AsSpan(offset, 2));
            uint rate = U32(24), pcmBytes = U32(40); ushort channels = U16(22), block = U16(32);
            if (U32(4) != native.Length - 8 || U32(16) != 16 || U16(20) != 1 || U16(34) != 16 ||
                channels is < 1 or > 8 || rate is < 8000 or > 192000 || block != channels * 2 ||
                U32(28) != rate * block || pcmBytes != native.Length - 44 || pcmBytes % block != 0)
                throw new InvalidDataException("Native FMOD PCM stream/frames/EOF invalid.");
            uint frames = pcmBytes / block;
            return new { source, sourceSha256 = before, helperSha256 = helperHash, nativeReaderPass = true,
                fullNativeSilentFmodDecode = true, channels, rate, frames, duration = (double)frames / rate,
                pcmBytes, pcmSha256 = Convert.ToHexString(SHA256.HashData(native.AsSpan(44))),
                sourcePreserved = true, audioPlayed = false, worldTested = false, renderingTested = false };
        }
        if (mode == "texture-pixels")
        {
            if (native.Length < 40 || !native.AsSpan(0, 4).SequenceEqual("OTR1"u8))
                throw new InvalidDataException("Snapshot RGBA nativo inválido.");
            uint Word(int index) => BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(4 + index * 4, 4));
            uint width = Word(0), height = Word(1), levels = Word(4), bytes = Word(8);
            if (width is < 1 or > 16384 || height is < 1 or > 16384 || levels is < 1 or > 15 ||
                (ulong)width * height * 4 != bytes || (ulong)bytes + 40 != (ulong)native.Length)
                throw new InvalidDataException("Dimensiones/EOF RGBA nativos inválidos.");
            return new { source, sourceSha256 = before, helperSha256 = helperHash,
                nativeReaderPass = true, nativePixelReadback = true, width, height,
                depth = Word(2), format = Word(3), levels, filter = Word(5), addressU = Word(6), addressV = Word(7),
                pixelBytes = bytes, rgbaSha256 = Convert.ToHexString(SHA256.HashData(native.AsSpan(40))),
                pixelLayout = "Native RenderWare D3D9 level0 converted to RGBA row-major without stride padding",
                sourcePreserved = true, topLevelOnly = true, mipChainTested = false,
                equivalentResource = false, renderingTested = false };
        }
        using var metadata = JsonDocument.Parse(native);
        var root = metadata.RootElement;
        if (mode == "texture")
        {
            int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
            if (width is < 1 or > 16384 || height is < 1 or > 16384 || !root.GetProperty("nativeReaderPass").GetBoolean())
                throw new InvalidDataException("Metadata de textura nativa inválida.");
            return new { source, sourceSha256 = before, helperSha256 = helperHash, nativeReaderPass = true,
                width, height, depth = root.GetProperty("depth").GetInt32(), format = root.GetProperty("format").GetInt32(),
                levels = root.GetProperty("levels").GetInt32(), sourcePreserved = true, renderingTested = false };
        }
        var requests = new List<TextureRequest>();
        foreach (var item in root.GetProperty("textureRequests").EnumerateArray())
        {
            string n = item.GetProperty("nameHex").GetString()!, m = item.GetProperty("maskHex").GetString()!;
            string name = Encoding.Latin1.GetString(Convert.FromHexString(n)), mask = Encoding.Latin1.GetString(Convert.FromHexString(m));
            if ((!rawNames && name.Length == 0) || name.Length > 127 || mask.Length > 127 || requests.Count >= 16384)
                throw new InvalidDataException("Referencia de textura fuera de límites.");
            bool safe = name.Length > 0;
            try { ContentSafety.ResourcePath(name); if (mask.Length > 0) ContentSafety.ResourcePath(mask); }
            catch (InvalidDataException) { if (!rawNames) throw; safe = false; }
            // Raw-name inspection is metadata only: never turn an unsafe callback name into a filesystem path.
            requests.Add(new(n, name, m, mask, safe));
        }
        return new { source, sourceSha256 = before, helperSha256 = helperHash, nativeReaderPass = true,
            meshes = root.GetProperty("meshes").GetInt32(), vertices = root.GetProperty("vertices").GetInt32(),
            triangles = root.GetProperty("triangles").GetInt32(), textureRequests = requests.Distinct().ToList(),
            texturesSubstituted = true, sourcePreserved = true, renderingTested = false,
            scope = "Requests observed by native RenderWare texture callback, including plugin requests; external property/effect dependencies require their own catalogs." };
    }
}
