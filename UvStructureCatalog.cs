using System.Buffers.Binary;
using System.Text;

namespace DboAssetsStudio;

// Structural observations only. Never changes a dictionary or claims native
// interpolation; keeps frame words available when no registered profile fits.
internal static class UvStructureCatalog
{
    public static object Inspect(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (Path.GetExtension(path).ToLowerInvariant() != ".uva" || new FileInfo(path).Length is < 28 or > 1024 * 1024)
            throw new InvalidDataException("Bounded UVA dictionary required, maximum1MiB.");
        string hash = ContentSafety.Hash(path); byte[] bytes = File.ReadAllBytes(path);
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        float Number(int offset) => BitConverter.Int32BitsToSingle(unchecked((int)Word(offset)));
        int Chunk(int offset, uint type, int limit)
        {
            if (offset < 0 || offset > limit - 12 || Word(offset) != type || Word(offset + 4) > limit - offset - 12)
                throw new InvalidDataException("UVA chunk boundary/type differs.");
            return checked(offset + 12 + (int)Word(offset + 4));
        }
        if (Chunk(0, 0x2B, bytes.Length) != bytes.Length || Chunk(12, 1, bytes.Length) != 28 || Word(16) != 4 || Word(24) is < 1 or > 64)
            throw new InvalidDataException("Bounded native UVA dictionary header required.");
        var entries = new List<object>(); int cursor = 28;
        for (uint i = 0; i < Word(24); i++)
        {
            int end = Chunk(cursor, 0x1B, bytes.Length);
            if (end - cursor < 100) throw new InvalidDataException("Short UVA animation entry.");
            uint count = Word(cursor + 20);
            int payloadBytes = end - cursor - 100;
            if (count is < 1 or > 4096) throw new InvalidDataException("Unbounded UVA frame count.");
            byte[] nameBytes = bytes.AsSpan(cursor + 36, 32).ToArray(); int zero = Array.IndexOf(nameBytes, (byte)0);
            if (zero < 1 || nameBytes.Take(zero).Any(value => value < 32 || value > 126))
                throw new InvalidDataException("Literal bounded UVA entry name required.");
            uint[] maps = Enumerable.Range(0, 8).Select(index => Word(cursor + 68 + index * 4)).ToArray();
            var frames = new List<object>();
            bool layout32 = payloadBytes == count * 32;
            if (layout32)
            {
                for (int frame = 0; frame < count; frame++)
                {
                    int position = cursor + 100 + frame * 32;
                    uint[] words = Enumerable.Range(0, 8).Select(index => Word(position + index * 4)).ToArray();
                    frames.Add(new { ordinal = frame, offset = position, firstFloat = Number(position),
                        finalWord = words[7], wordsHex = words.Select(value => value.ToString("X8")).ToArray() });
                }
            }
            entries.Add(new { name = Encoding.ASCII.GetString(nameBytes, 0, zero), offset = cursor, bytes = end - cursor,
                chunkVersion = Word(cursor + 8), streamVersion = Word(cursor + 12), interpolatorType = Word(cursor + 16),
                framesDeclared = count, flags = Word(cursor + 24), duration = Number(cursor + 28), customId = Word(cursor + 32),
                nodeToChannelMap = maps, framePayloadBytes = payloadBytes, matches32ByteFrameLayout = layout32,
                frameWordObservations = frames });
            cursor = end;
        }
        if (cursor != bytes.Length || ContentSafety.Hash(path) != hash) throw new IOException("UVA input changed or trailing bytes remain.");
        return new { profile = "taiwan-uva-structural-observation-v1", source = path, sourceSha256 = hash,
            bytes = bytes.Length, entries, structuralOnly = true, nativeReaderPass = false,
            sourcePreserved = true, runtimeModified = false, renderingTested = false, worldTested = false };
    }
}
