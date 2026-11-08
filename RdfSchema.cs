using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal sealed record RdfField(string Name, string Type, int Count, int Offset)
{
    public int Width => Type switch
    {
        "int" or "float" => 4,
        "short" => 2,
        "byte" or "bool" or "string" => 1,
        "double" => 8,
        "wstring" => 2,
        _ => throw new InvalidDataException($"Unsupported RDF field type '{Type}'.")
    };
    public int Size => checked(Width * Count);
    public bool IsPadding => Name.StartsWith("pad", StringComparison.OrdinalIgnoreCase) || Name.Equals("byPadding", StringComparison.OrdinalIgnoreCase);
}

internal sealed class RdfSchema
{
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ItemData"] = ["table_item_data.rdf", "o_table_item_data.rdf"],
        ["ItemEnchantData"] = ["table_item_enchant_data.rdf", "o_table_item_enchant_data.rdf"],
        ["SetItemTable"] = ["table_set_item_data.rdf", "o_table_set_item_data.rdf"],
        ["Charm"] = ["table_charm_data.rdf", "o_table_charm_data.rdf"],
        ["ItemOption"] = ["table_item_option_data.rdf", "o_table_item_option_data.rdf"],
        ["WorldTable"] = ["table_world_data.rdf", "o_table_world_data.rdf"],
        ["table_pc_data"] = ["table_pc_data.rdf", "o_table_pc_data.rdf"],
        ["FormulaTable"] = ["td_formula.rdf", "table_formula_data.rdf"],
        ["MerchantTable"] = ["table_merchant_data.rdf", "o_table_merchant_data.rdf"],
        ["HLSItem"] = ["table_hls_item_data.rdf"],
        ["QuestProbability"] = ["table_quest_probability_data.rdf", "o_table_quest_probability_data.rdf"],
        ["MasterNPC"] = ["table_npc_data.rdf", "o_table_npc_data.rdf"]
    };

    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string Sheet { get; init; }
    public required IReadOnlyList<RdfField> Fields { get; init; }
    public required int RowSize { get; init; }
    public string KeyField => Fields.FirstOrDefault(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase))?.Name ?? Fields[0].Name;

    public static IReadOnlyList<RdfSchema> LoadCatalog(string path, string? onlySchema = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("No encuentro el catálogo Taiwan taiwan.txt.", path);
        var list = new List<RdfSchema>();
        foreach (var original in File.ReadAllLines(path).Concat(TaiwanSupplementalSchemas.Lines()))
        {
            var line = original.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var tokens = line.Split(';');
            if (tokens.Length < 4) continue;
            if (onlySchema != null && tokens[0] != onlySchema) continue;
            int offset = 0;
            var fields = new List<RdfField>();
            for (int i = 3; i < tokens.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(tokens[i])) continue;
                var spec = tokens[i].Split(',');
                if (spec.Length < 3 || !int.TryParse(spec[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0)
                    throw new InvalidDataException($"Invalid field definition in schema {tokens[0]}: {tokens[i]}");
                var field = new RdfField(spec[0].Trim(), spec[1].Trim().ToLowerInvariant(), count, offset);
                // Older catalog rows repeat "pad". Keep every reserved region
                // addressable in exports without changing its bytes or offset.
                if (fields.Any(f => f.Name.Equals(field.Name, StringComparison.Ordinal)))
                {
                    if (!field.IsPadding) throw new InvalidDataException($"Duplicate field '{field.Name}' in schema {tokens[0]}.");
                    field = field with { Name = $"{field.Name}_{offset}" };
                }
                offset = checked(offset + field.Size);
                fields.Add(field);
            }
            if (fields.Count == 0 || offset == 0) throw new InvalidDataException($"Empty schema: {tokens[0]}");
            if (tokens[0].Equals("table_pc_data", StringComparison.OrdinalIgnoreCase) && offset != 248)
                throw new InvalidDataException($"PC Taiwan ABI mismatch: schema={offset}, expected=248 bytes from PCTable.h.");
            if (tokens[0].Equals("FormulaTable", StringComparison.OrdinalIgnoreCase) && offset != 24)
                throw new InvalidDataException($"Formula Taiwan ABI mismatch: schema={offset}, expected=24 bytes from FormulaTable.h.");
            if (tokens[0].Equals("MerchantTable", StringComparison.OrdinalIgnoreCase) && offset != 516)
                throw new InvalidDataException($"Merchant Taiwan ABI mismatch: schema={offset}, expected=516 bytes from MerchantTable.h.");
            if (tokens[0].Equals("HLSItem", StringComparison.OrdinalIgnoreCase) && offset != 20)
                throw new InvalidDataException($"HLS item Taiwan ABI mismatch: schema={offset}, expected=20 bytes from HLSItemTable.h.");
            if (tokens[0].Equals("CharTitle", StringComparison.OrdinalIgnoreCase) && offset != 3128)
                throw new InvalidDataException($"CharTitle Taiwan ABI mismatch: schema={offset}, expected=3128 bytes from CharTitleTable.h.");
            if (tokens[0].Equals("UseItem", StringComparison.OrdinalIgnoreCase) && offset != 220)
                throw new InvalidDataException($"UseItem Taiwan ABI mismatch: schema={offset}, expected=220 bytes from UseItemTable.h.");
            if (tokens[0].Equals("SystemEffect", StringComparison.OrdinalIgnoreCase) && offset != 172)
                throw new InvalidDataException($"SystemEffect Taiwan ABI mismatch: schema={offset}, expected=172 bytes from SystemEffectTable.h.");
            if (tokens[0].Equals("Skill", StringComparison.OrdinalIgnoreCase) && offset != 348)
                throw new InvalidDataException($"Skill Taiwan ABI mismatch: schema={offset}, expected=348 bytes from SkillTable.h.");
            if (tokens[0].Equals("Action", StringComparison.OrdinalIgnoreCase) && offset != 60)
                throw new InvalidDataException($"Action Taiwan ABI mismatch: schema={offset}, expected=60 bytes from ActionTable.h.");
            if (tokens[0].Equals("ChatCommand", StringComparison.OrdinalIgnoreCase) && offset != 40)
                throw new InvalidDataException($"ChatCommand Taiwan ABI mismatch: schema={offset}, expected=40 bytes from ChatCommandTable.h.");
            if (tokens[0].Equals("NpcData", StringComparison.OrdinalIgnoreCase) && offset != 500)
                throw new InvalidDataException($"NPC Taiwan ABI mismatch: schema={offset}, expected=500 bytes from NpcTable.h.");
            if (tokens[0].Equals("SpawnData", StringComparison.OrdinalIgnoreCase) && offset != 84)
                throw new InvalidDataException($"Spawn Taiwan ABI mismatch: schema={offset}, expected=84 bytes from SpawnTable.h.");
            if (tokens[0] is "MobServerData" or "NpcServerData" && offset != 12)
                throw new InvalidDataException($"Bot server Taiwan ABI mismatch: schema={offset}, expected=12 bytes from MobServerTable/NpcServerTable.h.");
            if (tokens[0].Equals("WorldZoneTable", StringComparison.OrdinalIgnoreCase) && offset != 100)
                throw new InvalidDataException($"World zone Taiwan ABI mismatch: schema={offset}, expected=100 bytes from WorldZoneTable.h.");
            if (tokens[0].Equals("ObjectSpawn", StringComparison.OrdinalIgnoreCase) && offset != 868)
                throw new InvalidDataException($"Object spawn Taiwan ABI mismatch: schema={offset}, expected=868 bytes from ObjectTable.h (x86/x64).");
            if (tokens[0].Equals("Newbie", StringComparison.OrdinalIgnoreCase) && offset != 200)
                throw new InvalidDataException($"Newbie Taiwan ABI mismatch: schema={offset}, expected=200 bytes from NewbieTable.h; items10, skills7, quickslots5x6, PORTALID byte.");
            var nativeMapStrides = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase)
            { ["Dungeon"]=36, ["Portal"]=452, ["WorldMap"]=140, ["Landmark"]=192,
              ["WorldPlay"]=16, ["ItemDisassemble"]=20, ["QuestRewardSelect"]=248 };
            if (nativeMapStrides.TryGetValue(tokens[0], out int nativeMapStride) && offset != nativeMapStride)
                throw new InvalidDataException($"Native map/table ABI mismatch: {tokens[0]} schema={offset}, expected={nativeMapStride} bytes.");
            if (tokens[0].Equals("MasterNPC", StringComparison.OrdinalIgnoreCase) && offset != 500)
                throw new InvalidDataException($"Master NPC Taiwan ABI mismatch: schema={offset}, expected=500 bytes from NPCTable.h (pack(4), x86/x64).");
            if (tokens[0].Equals("QuestProbability", StringComparison.OrdinalIgnoreCase) && offset != 1656)
                throw new InvalidDataException($"Quest probability Taiwan ABI mismatch: schema={offset}, expected=1656 bytes from QuestProbabilityTable.h.");
            if (tokens[0].Equals("ItemRecipe", StringComparison.OrdinalIgnoreCase) && offset != 84)
                throw new InvalidDataException($"Item recipe Taiwan ABI mismatch: schema={offset}, expected=84 bytes from ItemRecipeTable.h.");
            if (tokens[0].Equals("DungeonTable", StringComparison.OrdinalIgnoreCase) && offset != 36)
                throw new InvalidDataException($"Dungeon Taiwan ABI mismatch: schema={offset}, expected=36 bytes from DungeonTable.h.");
            if (tokens[0].Equals("WorldPlayTable", StringComparison.OrdinalIgnoreCase) && offset != 16)
                throw new InvalidDataException($"WorldPlay Taiwan ABI mismatch: schema={offset}, expected=16 bytes from WorldPlayTable.h.");
            if (tokens[0].Equals("NpcServerTable", StringComparison.OrdinalIgnoreCase) && offset != 12)
                throw new InvalidDataException($"NPC server Taiwan ABI mismatch: schema={offset}, expected=12 bytes from NPCServerTable.h.");
            if (tokens[0].Equals("NpcData", StringComparison.OrdinalIgnoreCase) && offset != 500)
                throw new InvalidDataException($"NPC Taiwan ABI mismatch: schema={offset}, expected=500 bytes from NpcTable.h.");
            if (tokens[0].Equals("NpcSpawn", StringComparison.OrdinalIgnoreCase) && offset != 84)
                throw new InvalidDataException($"NPC spawn Taiwan ABI mismatch: schema={offset}, expected=84 bytes from SpawnTable.h.");
            if (tokens[0].Equals("MobSpawn", StringComparison.OrdinalIgnoreCase) && offset != 84)
                throw new InvalidDataException($"MOB spawn Taiwan ABI mismatch: schema={offset}, expected=84 bytes from SpawnTable.h.");
            if (tokens[0].Equals("ItemGroupList", StringComparison.OrdinalIgnoreCase) && offset != 296)
                throw new InvalidDataException($"Item group Taiwan ABI mismatch: schema={offset}, expected=296 bytes from ItemGroupListTable.h.");
            if (tokens[0].Equals("ItemBagList", StringComparison.OrdinalIgnoreCase) && offset != 256)
                throw new InvalidDataException($"Item bag Taiwan ABI mismatch: schema={offset}, expected=256 bytes from ItemBagListTable.h.");
            if (tokens[0].Equals("ItemOption", StringComparison.OrdinalIgnoreCase) && offset != 260)
                throw new InvalidDataException($"Item option Taiwan ABI mismatch: schema={offset}, expected=260 bytes from ItemOptionTable.h.");
            if (tokens[0].Equals("QuestReward", StringComparison.OrdinalIgnoreCase) && offset != 108)
                throw new InvalidDataException($"Quest reward Taiwan ABI mismatch: schema={offset}, expected=108 bytes from QuestRewardTable.h.");
            if (tokens[0].Equals("Charm", StringComparison.OrdinalIgnoreCase) && offset != 28)
                throw new InvalidDataException($"Charm Taiwan ABI mismatch: schema={offset}, expected=28 bytes from CharmTable.h.");
            if (tokens[0].Equals("ItemOption", StringComparison.OrdinalIgnoreCase) && offset != 260)
                throw new InvalidDataException($"Item option Taiwan ABI mismatch: schema={offset}, expected=260 bytes from ItemOptionTable.h.");
            list.Add(new RdfSchema { Name = tokens[0], FileName = tokens[1], Sheet = tokens[2], Fields = fields, RowSize = offset });
        }
        return list;
    }

    public static RdfSchema? Detect(string filePath, IReadOnlyList<RdfSchema> schemas)
    {
        var name = Path.GetFileName(filePath);
        if (name.Equals("Table_NPC_Data.rdf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("table_npc_data.rdf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("o_table_npc_data.rdf", StringComparison.OrdinalIgnoreCase))
            return schemas.FirstOrDefault(s => s.Name.Equals("MasterNPC", StringComparison.OrdinalIgnoreCase));
        var exact = schemas.FirstOrDefault(s => string.Equals(s.FileName, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // World-specific table names share the reviewed Taiwan Object ABI.
        // Do not infer a schema from a source record's size or an EDF extension.
        if (Regex.IsMatch(name, @"^spawn_object_[a-z0-9_]+\.rdf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return schemas.Single(s => s.Name == "ObjectSpawn");
        if (Regex.IsMatch(name, @"^spawn_(mob|npc)_[a-z0-9_]+\.rdf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return schemas.Single(s => s.Name == "SpawnData");
        foreach (var s in schemas)
            if (Aliases.TryGetValue(s.Name, out var names) && names.Contains(name, StringComparer.OrdinalIgnoreCase)) return s;
        return null;
    }

    public string Decode(RdfField field, ReadOnlySpan<byte> source)
    {
        var values = new string[field.Count];
        for (int i = 0; i < field.Count; i++)
        {
            int p = field.Offset + i * field.Width;
            values[i] = field.Type switch
            {
                "int" => IsSignedInt(field.Name) ? BitConverter.ToInt32(source.Slice(p, 4)).ToString(CultureInfo.InvariantCulture) : BitConverter.ToUInt32(source.Slice(p, 4)).ToString(CultureInfo.InvariantCulture),
                "float" => BitConverter.ToSingle(source.Slice(p, 4)).ToString("R", CultureInfo.InvariantCulture),
                "short" => BitConverter.ToUInt16(source.Slice(p, 2)).ToString(CultureInfo.InvariantCulture),
                "byte" => source[p].ToString(CultureInfo.InvariantCulture),
                "bool" => source[p].ToString(CultureInfo.InvariantCulture),
                "double" => BitConverter.ToDouble(source.Slice(p, 8)).ToString("R", CultureInfo.InvariantCulture),
                "string" => DecodeFixedString(source.Slice(field.Offset, field.Count)),
                "wstring" => DecodeFixedWide(source.Slice(field.Offset, field.Count * 2)),
                _ => throw new InvalidDataException($"Unsupported type {field.Type}")
            };
            if (field.Type is "string" or "wstring") break;
        }
        return field.Type is "string" or "wstring" ? values[0] : string.Join(' ', values);
    }

    public void Encode(RdfField field, string? text, Span<byte> target)
    {
        if (field.IsPadding) throw new InvalidOperationException("El padding reservado no es editable.");
        // Parse on a copy: a failure midway through an array/string must not
        // leave a partially changed record, even outside the grid UI.
        byte[] working = target.ToArray();
        EncodeCore(field, text, working);
        working.CopyTo(target);
    }

    private void EncodeCore(RdfField field, string? text, Span<byte> target)
    {
        text ??= string.Empty;
        if (field.Type == "string") { EncodeFixedString(text, target.Slice(field.Offset, field.Count)); return; }
        if (field.Type == "wstring") { EncodeFixedWide(text, target.Slice(field.Offset, field.Count * 2)); return; }
        var parts = Regex.Split(text.Trim(), @"[\s,;]+").Where(x => x.Length > 0).ToArray();
        if (parts.Length != field.Count) throw new FormatException($"{field.Name}: se esperan {field.Count} valores y recibí {parts.Length}.");
        for (int i = 0; i < parts.Length; i++)
        {
            int p = field.Offset + i * field.Width;
            switch (field.Type)
            {
                case "int":
                    if (IsSignedInt(field.Name)) BinaryPrimitives.WriteInt32LittleEndian(target.Slice(p, 4), int.Parse(parts[i], CultureInfo.InvariantCulture));
                    else BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(p, 4), uint.Parse(parts[i], CultureInfo.InvariantCulture));
                    break;
                case "float":
                    float f = float.Parse(parts[i], CultureInfo.InvariantCulture);
                    if (!float.IsFinite(f)) throw new FormatException($"{field.Name}: NaN e infinitos no son valores válidos.");
                    BinaryPrimitives.WriteInt32LittleEndian(target.Slice(p, 4), BitConverter.SingleToInt32Bits(f)); break;
                case "short": BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(p, 2), ushort.Parse(parts[i], CultureInfo.InvariantCulture)); break;
                case "byte": target[p] = byte.Parse(parts[i], CultureInfo.InvariantCulture); break;
                case "bool":
                    byte b = byte.Parse(parts[i], CultureInfo.InvariantCulture);
                    if (b > 1) throw new FormatException($"{field.Name}: el booleano debe ser 0 o 1.");
                    target[p] = b; break;
                case "double":
                    double d = double.Parse(parts[i], CultureInfo.InvariantCulture);
                    if (!double.IsFinite(d)) throw new FormatException($"{field.Name}: NaN e infinitos no son valores válidos.");
                    BinaryPrimitives.WriteInt64LittleEndian(target.Slice(p, 8), BitConverter.DoubleToInt64Bits(d)); break;
                default: throw new InvalidDataException($"Unsupported type {field.Type}");
            }
        }
    }

    // SpawnTable.h uses unsigned TBLIDX for these two scalar references;
    // their prefix is not the signed "ai" array naming convention.
    private static bool IsSignedInt(string fieldName) => fieldName is not ("aiScript" or "aiScriptScene") &&
        Regex.IsMatch(fieldName, "^(n|an|i|ai)[A-Z_]", RegexOptions.CultureInvariant);
    private static string DecodeFixedString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(bytes[..(end >= 0 ? end : bytes.Length)]);
    }
    private static string DecodeFixedWide(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length;
        for (int i = 0; i + 1 < bytes.Length; i += 2) if (bytes[i] == 0 && bytes[i + 1] == 0) { n = i; break; }
        return Encoding.Unicode.GetString(bytes[..n]);
    }
    private static void EncodeFixedString(string text, Span<byte> target)
    {
        if (text.Any(c => c > 255 || c == '\0')) throw new FormatException("El campo requiere bytes ANSI representables; no admite esos caracteres.");
        var bytes = Encoding.Latin1.GetBytes(text);
        if (bytes.Length >= target.Length) throw new FormatException($"Texto supera el límite de {target.Length - 1} bytes ANSI.");
        target.Clear();
        bytes.CopyTo(target);
    }
    private static void EncodeFixedWide(string text, Span<byte> target)
    {
        if (text.Contains('\0')) throw new FormatException("No se permiten terminadores NUL dentro del texto.");
        var bytes = new UnicodeEncoding(false, false, true).GetBytes(text);
        if (bytes.Length >= target.Length - 1) throw new FormatException($"Texto supera el límite de {target.Length / 2 - 1} caracteres UTF-16.");
        target.Clear();
        bytes.CopyTo(target);
    }
}

internal sealed class RdfRow
{
    public int Index { get; init; }
    public required byte[] Original { get; init; }
    public required byte[] Current { get; init; }
    public bool Modified => !Original.AsSpan().SequenceEqual(Current);
}

internal sealed class RdfDocument
{
    public required string Path { get; init; }
    public required RdfSchema Schema { get; init; }
    public required byte Margin { get; init; }
    public required byte[] OriginalBytes { get; init; }
    public required List<RdfRow> Rows { get; init; }
    public string OriginalHash { get; init; } = "";
    public int ModifiedRows => Rows.Count(r => r.Modified);

    public static RdfDocument Open(string path, RdfSchema schema)
    {
        var info = new FileInfo(path);
        if (info.Length < 1 || info.Length > 512L * 1024 * 1024) throw new InvalidDataException("RDF vacío o supera el límite seguro de 512 MiB.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 1) throw new InvalidDataException("RDF vacío: falta el byte inicial Taiwan.");
        var payload = bytes.Length - 1;
        if (payload % schema.RowSize != 0)
            throw new InvalidDataException($"Tamaño RDF inválido: bytes luego del margen={payload}, stride Taiwan={schema.RowSize}, resto={payload % schema.RowSize}.");
        int count = payload / schema.RowSize;
        var rows = new List<RdfRow>(count);
        for (int i = 0; i < count; i++)
        {
            var row = bytes.AsSpan(1 + i * schema.RowSize, schema.RowSize).ToArray();
            rows.Add(new RdfRow { Index = i, Original = (byte[])row.Clone(), Current = row });
        }
        return new RdfDocument { Path = path, Schema = schema, Margin = bytes[0], OriginalBytes = bytes, Rows = rows, OriginalHash = Convert.ToHexString(SHA256.HashData(bytes)) };
    }

    public byte[] BuildBytes()
    {
        var result = new byte[OriginalBytes.Length];
        OriginalBytes.CopyTo(result, 0);
        foreach (var row in Rows) row.Current.CopyTo(result, 1 + row.Index * Schema.RowSize);
        return result;
    }
}
