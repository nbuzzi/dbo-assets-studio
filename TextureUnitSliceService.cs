using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// Explicit raw DDS recovery does not infer a PAK entry or its original name.
internal static class TextureUnitSliceService
{
    internal sealed record Plan(int Version, string Source, string SourceSha256, long Offset,
        int Length, string PayloadSha256, uint Width, uint Height, uint MipCount, string Format,
        string NativeHelperSha256);

    private static (Plan Plan, byte[] Bytes) Snapshot(string source, long offset)
    {
        source = Path.GetFullPath(source); ContentSafety.NoLinks(source);
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        if (offset < 0 || offset > stream.Length - 128) throw new InvalidDataException("DDS offset fuera de la unidad.");
        string sourceHash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = offset;
        byte[] header = new byte[128]; stream.ReadExactly(header);
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at, 4));
        if (!header.AsSpan(0, 4).SequenceEqual("DDS "u8) || U(4) != 124 || U(76) != 32)
            throw new InvalidDataException("Cabecera DDS inválida.");
        uint height = U(12), width = U(16), mips = Math.Max(1, U(28));
        if ((U(8) & 0x1007) != 0x1007 || (U(108) & 0x1000) == 0 || width is 0 or > 8192 || height is 0 or > 8192)
            throw new InvalidDataException("Dimensiones/flags DDS no compatibles.");
        if (U(24) > 1 || U(112) != 0 || (U(80) & 4) == 0)
            throw new InvalidDataException("Solo DDS 2D comprimido, sin arrays, cubemaps ni volumen.");
        string format = System.Text.Encoding.ASCII.GetString(header, 84, 4);
        int blockBytes = format switch { "DXT1" => 8, "DXT3" or "DXT5" => 16, _ => throw new InvalidDataException("FourCC DDS no soportado: " + format) };
        int maxMips = 1; for (uint d = Math.Max(width, height); d > 1; d >>= 1) maxMips++;
        if (mips > maxMips || (mips > 1 && (U(8) & 0x20000) == 0)) throw new InvalidDataException("Mip chain DDS inválida.");
        long length = 128; uint w = width, h = height;
        for (int i = 0; i < mips; i++)
        {
            length = checked(length + (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes);
            w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1);
        }
        if (length > 128 * 1024 * 1024 || length > stream.Length - offset) throw new InvalidDataException("DDS truncado o demasiado grande.");
        byte[] bytes = new byte[(int)length]; header.CopyTo(bytes, 0);
        stream.ReadExactly(bytes.AsSpan(128));
        return (new Plan(1, source, sourceHash, offset, (int)length,
            Convert.ToHexString(SHA256.HashData(bytes)), width, height, mips, format,
            ContentSafety.Hash(Path.Combine(AppContext.BaseDirectory, "media-helper.exe"))), bytes);
    }

    private static void Separate(string source, string output)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(source))!;
        output = ContentSafety.SafeOutput(output);
        if (output.Equals(parent, StringComparison.OrdinalIgnoreCase) || output.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("La salida debe quedar fuera de la carpeta fuente.");
    }

    public static async Task<object> CreatePlanAsync(string source, long offset, string output)
    {
        Separate(source, output);
        var snapshot = Snapshot(source, offset);
        var native = await DecodeAsync(snapshot.Plan, snapshot.Bytes);
        ContentSafety.WriteJson(output, snapshot.Plan);
        return new { plan = Path.GetFullPath(output), snapshot.Plan.PayloadSha256, native,
            originalResourceNameKnown = false, installed = false };
    }

    private static async Task<JsonElement> DecodeAsync(Plan plan, byte[] bytes)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "media-helper.exe");
        ContentSafety.NoLinks(helper); string helperHash = ContentSafety.Hash(helper);
        if (helperHash != plan.NativeHelperSha256) throw new IOException("Helper difiere del plan sellado.");
        byte[] result = await NativeMedia.ConvertAsync("texture", bytes, ".dds");
        if (ContentSafety.Hash(helper) != helperHash) throw new IOException("Helper cambió durante decodificación.");
        var metadata = JsonSerializer.Deserialize<JsonElement>(result);
        if (!metadata.GetProperty("nativeReaderPass").GetBoolean()) throw new InvalidDataException("Falló lector nativo DDS.");
        if (metadata.GetProperty("width").GetUInt32() != plan.Width || metadata.GetProperty("height").GetUInt32() != plan.Height || metadata.GetProperty("levels").GetUInt32() != plan.MipCount)
            throw new InvalidDataException("Lector nativo no coincide con cabecera DDS.");
        return metadata;
    }

    private static (Plan Plan, byte[] Bytes) CheckedSnapshot(string planPath)
    {
        ContentSafety.NoLinks(planPath);
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath), ContentSafety.Json)
            ?? throw new InvalidDataException("Plan vacío.");
        if (plan.Version != 1) throw new InvalidDataException("Versión de plan no soportada.");
        var snapshot = Snapshot(plan.Source, plan.Offset);
        if (snapshot.Plan != plan) throw new InvalidDataException("Preimagen/plan DDS cambió; generar otro plan.");
        return snapshot;
    }

    public static async Task<object> PreviewAsync(string planPath)
    {
        var snapshot = CheckedSnapshot(planPath);
        return new { plan = snapshot.Plan, native = await DecodeAsync(snapshot.Plan, snapshot.Bytes),
            originalResourceNameKnown = false, installed = false };
    }

    public static async Task<object> ApplyAsync(string planPath, string destination)
    {
        var snapshot = CheckedSnapshot(planPath); Separate(snapshot.Plan.Source, destination);
        destination = ContentSafety.SafeOutput(destination);
        var native = await DecodeAsync(snapshot.Plan, snapshot.Bytes);
        string pending = ContentSafety.FreshDirectory(destination);
        // Failure retains private pending evidence and never overwrites a candidate.
        await File.WriteAllBytesAsync(Path.Combine(pending, "texture.dds"), snapshot.Bytes);
        ContentSafety.WriteJson(Path.Combine(pending, "plan.json"), snapshot.Plan);
        ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new {
            operation = "explicit-raw-dds-slice", snapshot.Plan, native,
            originalResourceNameKnown = false, indexDecoded = false, installed = false,
            timestampUtc = DateTime.UtcNow });
        Directory.Move(pending, destination);
        return new { candidate = destination, texture = Path.Combine(destination, "texture.dds"),
            snapshot.Plan.PayloadSha256, native, installed = false };
    }
}
