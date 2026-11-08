using System.Buffers.Binary;
using System.Text.Json;

namespace DboAssetsStudio;

internal sealed record GlbInspection(int Meshes, int Primitives, int Skins, int Vertices, int Materials, bool HasSkin, bool CanConvert, string[] Notes)
{
    // This is a preview safety bound, not a converter compatibility rule. Keep it
    // aligned with the GLB byte cap below so a single imported model cannot make
    // inspection and WebGL upload allocate unbounded amounts of memory.
    private const int MaxPreviewVertices = 2_000_000;
    private const long MaxPreviewBytes = 128L * 1024 * 1024;

    public static GlbInspection Read(string path)
    {
        if (new FileInfo(path).Length > MaxPreviewBytes) throw new IOException("GLB mayor a 128 MiB.");
        return ReadBytes(File.ReadAllBytes(path));
    }

    public static GlbInspection ReadBytes(byte[] bytes)
    {
        if (bytes.LongLength > MaxPreviewBytes) throw new IOException("GLB mayor a 128 MiB.");
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length) throw new InvalidDataException("Se requiere GLB 2.0 válido, con todos sus recursos embebidos.");
        int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        if (size < 2 || size > 8 * 1024 * 1024 || 20L + size + 8 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 0x4E4F534A) throw new InvalidDataException("Cabecera JSON del GLB inválida.");
        int binaryHeader = 20 + size;
        uint binaryLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binaryHeader));
        if (size % 4 != 0 || binaryLength % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binaryHeader + 4)) != 0x004E4942 || (long)binaryHeader + 8 + binaryLength != bytes.Length) throw new InvalidDataException("Chunk binario GLB inválido o truncado.");
        using var document = JsonDocument.Parse(bytes.AsMemory(20, size)); var root = document.RootElement;
        int Count(string key) => root.TryGetProperty(key, out var a) ? a.GetArrayLength() : 0;
        foreach (string collection in new[] { "buffers", "images" })
            if (root.TryGetProperty(collection, out var records))
                foreach (var row in records.EnumerateArray())
                    if (row.TryGetProperty("uri", out _)) throw new InvalidDataException("El GLB debe incluir imágenes/buffers internos (sin referencias URI externas o data URI).");
        var notes = new List<string>();
        if (root.TryGetProperty("extensionsRequired", out var extensions) && extensions.GetArrayLength() != 0) notes.Add("Extensiones glTF requeridas: necesita adaptación manual.");
        int primitives = 0, vertices = 0;
        if (root.TryGetProperty("meshes", out var meshes))
            foreach (var mesh in meshes.EnumerateArray())
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                {
                    primitives++;
                    if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) notes.Add("La malla debe usar triángulos.");
                    var attributes = primitive.GetProperty("attributes");
                    if (!attributes.TryGetProperty("POSITION", out var index)) throw new InvalidDataException("Primitive sin atributo POSITION.");
                    int accessorIndex = index.GetInt32();
                    if (!root.TryGetProperty("accessors", out var accessorArray) || accessorIndex < 0 || accessorIndex >= accessorArray.GetArrayLength()) throw new InvalidDataException("Accessor POSITION fuera de rango.");
                    var accessor = accessorArray[accessorIndex];
                    int count = accessor.GetProperty("count").GetInt32();
                    if (count < 1 || !string.Equals(accessor.GetProperty("type").GetString(), "VEC3", StringComparison.Ordinal)) throw new InvalidDataException("Accessor POSITION inválido.");
                    if (!accessor.TryGetProperty("bufferView", out var viewIndex) || !root.TryGetProperty("bufferViews", out var views) || viewIndex.GetInt32() < 0 || viewIndex.GetInt32() >= views.GetArrayLength()) throw new InvalidDataException("BufferView POSITION fuera de rango o ausente.");
                    var view = views[viewIndex.GetInt32()];
                    int componentSize = accessor.GetProperty("componentType").GetInt32() switch
                    {
                        5120 or 5121 => 1,
                        5122 or 5123 => 2,
                        5125 or 5126 => 4,
                        _ => throw new InvalidDataException("Tipo de componente POSITION no admitido.")
                    };
                    long viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt64() : 0;
                    long viewLength = view.GetProperty("byteLength").GetInt64();
                    long accessorOffset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt64() : 0;
                    long stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt64() : 3L * componentSize;
                    if (viewOffset < 0 || viewLength < 0 || viewOffset > binaryLength || viewLength > binaryLength - viewOffset || accessorOffset < 0 || accessorOffset > viewLength || stride < 3L * componentSize || checked((long)(count - 1) * stride + 3L * componentSize + accessorOffset) > viewLength)
                        throw new InvalidDataException("Los datos POSITION exceden los límites del buffer BIN.");
                    vertices = checked(vertices + count);
                    if (!attributes.TryGetProperty("TEXCOORD_0", out _)) notes.Add("Faltan UV para la textura.");
                }
        if (vertices > MaxPreviewVertices || vertices < 1) throw new InvalidDataException($"La vista previa admite de 1 a {MaxPreviewVertices:N0} vértices (GLB máximo: 128 MiB).");
        bool skin = Count("skins") > 0;
        if (!skin) notes.Add("Falta esqueleto: usá Preparar rig con Meshy.");
        if (Count("meshes") != 1 || primitives != 1) notes.Add("El perfil M_AKM_A1 requiere una malla y un primitive; no se descartan piezas automáticamente.");
        if (root.TryGetProperty("accessors", out var accessors) && accessors.EnumerateArray().Any(a => a.TryGetProperty("sparse", out _))) notes.Add("Accessors sparse requieren adaptación manual.");
        if (Count("materials") != 1) notes.Add("El perfil de conversión requiere un único material de color base.");
        return new(Count("meshes"), primitives, Count("skins"), vertices, Count("materials"), skin, notes.Count == 0, notes.Distinct().ToArray());
    }
}
