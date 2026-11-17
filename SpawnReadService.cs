using System.Globalization;

namespace DboAssetsStudio;

/// <summary>
/// Read-only access to Taiwan spawn tables (spawn_mob_*.rdf / spawn_npc_*.rdf, 84-byte rows).
/// Foundation of the World Editor (see WORLD-EDITOR-PLAN.md, phase 1). Never writes.
/// </summary>
internal static class SpawnReadService
{
    internal sealed record SpawnRow(int Index, uint Tblidx, uint RefTblidx, float X, float Y, float Z,
        float DirX, float DirY, float DirZ, byte Range, byte Quantity, ushort CoolTime, byte MoveType,
        byte Wander, byte MoveRange, uint PartyIndex, bool PartyLeader, uint SpawnGroup);

    internal sealed record SpawnFile(string Path, string Sha256, string Kind, int RowCount, SpawnRow[] Rows,
        float[] BoundsMin, float[] BoundsMax, int DuplicateIds, int NonFinite);

    public static SpawnFile Load(string path, string kind)
    {
        string schemaName = kind switch
        {
            "mob" => "MobSpawn",
            "npc" => "NpcSpawn",
            _ => throw new ArgumentException("kind must be mob or npc.")
        };
        var schemas = RdfSchema.LoadCatalog(RdfCli.LocateCatalog());
        var schema = schemas.Single(s => s.Name == schemaName);
        if (schema.RowSize != 84) throw new InvalidDataException("The spawn schema must be 84 bytes.");
        var doc = RdfDocument.Open(path, schema);
        RdfField F(string name) => schema.Fields.Single(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        string refName = kind == "mob" ? "mob_tblidx" : "npc_tblidx";
        var fTbl = F("tblidx"); var fRef = F(refName); var fLoc = F("vSpawn_Loc"); var fDir = F("vSpawn_Dir");
        var fRange = F("bySpawnLocRange"); var fQty = F("bySpawnQuantity"); var fCool = F("wSpawnCoolTime");
        var fMove = F("bySpawnMoveType"); var fWander = F("byWanderRange"); var fMoveRange = F("byMoveRange");
        var fParty = F("dwPartyIndex"); var fLeader = F("bPartyLeader"); var fGroup = F("spawnGroup");

        float[] Floats(ReadOnlySpan<byte> r, RdfField f) =>
            [BitConverter.ToSingle(r.Slice(f.Offset, 4)), BitConverter.ToSingle(r.Slice(f.Offset + 4, 4)), BitConverter.ToSingle(r.Slice(f.Offset + 8, 4))];

        var rows = new List<SpawnRow>(doc.Rows.Count);
        var seen = new HashSet<uint>();
        int dup = 0, bad = 0;
        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue], max = [float.MinValue, float.MinValue, float.MinValue];
        foreach (var row in doc.Rows)
        {
            ReadOnlySpan<byte> r = row.Current;
            uint id = BitConverter.ToUInt32(r.Slice(fTbl.Offset, 4));
            var loc = Floats(r, fLoc); var dir = Floats(r, fDir);
            if (!seen.Add(id)) dup++;
            if (loc.Any(v => !float.IsFinite(v))) { bad++; continue; }
            for (int i = 0; i < 3; i++) { min[i] = Math.Min(min[i], loc[i]); max[i] = Math.Max(max[i], loc[i]); }
            rows.Add(new SpawnRow(row.Index, id, BitConverter.ToUInt32(r.Slice(fRef.Offset, 4)), loc[0], loc[1], loc[2],
                dir[0], dir[1], dir[2], r[fRange.Offset], r[fQty.Offset], BitConverter.ToUInt16(r.Slice(fCool.Offset, 2)),
                r[fMove.Offset], r[fWander.Offset], r[fMoveRange.Offset], BitConverter.ToUInt32(r.Slice(fParty.Offset, 4)),
                r[fLeader.Offset] != 0, BitConverter.ToUInt32(r.Slice(fGroup.Offset, 4))));
        }
        if (rows.Count == 0) { min = [0, 0, 0]; max = [0, 0, 0]; }
        return new SpawnFile(Path.GetFullPath(path), doc.OriginalHash, kind, doc.Rows.Count, rows.ToArray(), min, max, dup, bad);
    }

    /// <summary>Compact payload for the viewer: parallel arrays keep 100k+ spawns cheap to serialize.</summary>
    public static object ToViewerPayload(SpawnFile f) => new
    {
        schema = "world-editor-spawns-v1", f.Path, sha256 = f.Sha256, f.Kind, count = f.Rows.Length, f.DuplicateIds, f.NonFinite,
        boundsMin = f.BoundsMin, boundsMax = f.BoundsMax,
        id = f.Rows.Select(r => r.Tblidx).ToArray(),
        refId = f.Rows.Select(r => r.RefTblidx).ToArray(),
        x = f.Rows.Select(r => r.X).ToArray(), y = f.Rows.Select(r => r.Y).ToArray(), z = f.Rows.Select(r => r.Z).ToArray(),
        range = f.Rows.Select(r => (int)r.Range).ToArray(), quantity = f.Rows.Select(r => (int)r.Quantity).ToArray(),
        group = f.Rows.Select(r => r.SpawnGroup).ToArray(),
        dirX = f.Rows.Select(r => r.DirX).ToArray(), dirZ = f.Rows.Select(r => r.DirZ).ToArray(),
        coolTime = f.Rows.Select(r => (int)r.CoolTime).ToArray(), moveType = f.Rows.Select(r => (int)r.MoveType).ToArray(),
        wander = f.Rows.Select(r => (int)r.Wander).ToArray(), moveRange = f.Rows.Select(r => (int)r.MoveRange).ToArray(),
        partyIndex = f.Rows.Select(r => r.PartyIndex).ToArray(), partyLeader = f.Rows.Select(r => r.PartyLeader ? 1 : 0).ToArray(),
        uid = f.Rows.Select(r => r.Index).ToArray()
    };

    public static object Summarize(SpawnFile f) => new
    {
        f.Path, sha256 = f.Sha256, f.Kind, f.RowCount, f.DuplicateIds, f.NonFinite, boundsMin = f.BoundsMin, boundsMax = f.BoundsMax,
        distinctRefs = f.Rows.Select(r => r.RefTblidx).Distinct().Count(),
        distinctGroups = f.Rows.Select(r => r.SpawnGroup).Distinct().Count(),
        topRefs = f.Rows.GroupBy(r => r.RefTblidx).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new { refTblidx = g.Key, count = g.Count() }).ToArray(),
        sample = f.Rows.Take(5).ToArray()
    };
}

internal static class SpawnCli
{
    public static object Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            return new { commands = new[] { "summary <spawn_mob_*.rdf|spawn_npc_*.rdf> [mob|npc]", "list <file> [mob|npc] [limit]", "viewer-json <file> <new-output.json> [mob|npc]", "validate <file> [mob|npc]", "catalog <rdf-folder> mob|npc [query] [minLevel] [maxLevel] [limit]", "edit <file> <ops.json> <new-candidate-dir> [mob|npc] [also-check.rdf ...]" }, safety = "Read-only; never writes." };
        // Optional flags (any position): --tables <folder>  --first-id <n>
        string? tablesFlag = null; uint? firstIdFlag = null;
        var plain = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tables" && i + 1 < args.Length) tablesFlag = args[++i];
            else if (args[i] == "--first-id" && i + 1 < args.Length) firstIdFlag = uint.Parse(args[++i], CultureInfo.InvariantCulture);
            else plain.Add(args[i]);
        }
        args = plain.ToArray();
        SpawnEditSession Configure(SpawnEditSession ses) { if (tablesFlag != null) ses.UseTablesFolder(tablesFlag); if (firstIdFlag is uint f0) ses.SetFirstId(f0); return ses; }
        if (args.Length >= 2 && args[0] == "selftest")
            return SpawnSelfTest.Run(args[1], KindOf(args, 2), args.Length > 3 && int.TryParse(args[3], out int seedArg) ? seedArg : 12345, args.Length > 4 && int.TryParse(args[4], out int stepsArg) ? stepsArg : 150);
        if (args.Length >= 3 && args[0] == "new")
            return new { path = SpawnEditSession.CreateEmptyFile(args[1], args.Length > 3 ? args[3] : "mob", args[2]), installAllowed = false };
        if (args.Length < 2) throw new ArgumentException("The spawn file is missing.");
        if (args[0] == "viewer-json")
        {
            if (args.Length < 3) throw new ArgumentException("Usage: spawn viewer-json <file> <new-output.json> [mob|npc].");
            string outPath = ContentSafety.SafeOutput(args[2]);
            if (File.Exists(outPath) || Directory.Exists(outPath)) throw new IOException("The output must be a new file.");
            string k = args.Length > 3 ? args[3] : (Path.GetFileName(args[1]).Contains("npc", StringComparison.OrdinalIgnoreCase) ? "npc" : "mob");
            ContentSafety.WriteJson(outPath, SpawnReadService.ToViewerPayload(SpawnReadService.Load(args[1], k)));
            return new { output = outPath, sha256 = ContentSafety.Hash(outPath) };
        }
        static string KindOf(string[] a, int i) => a.Length > i && a[i] is "mob" or "npc" ? a[i] : (Path.GetFileName(a[1]).Contains("npc", StringComparison.OrdinalIgnoreCase) ? "npc" : "mob");
        if (args[0] == "catalog")
        {
            if (args.Length < 3) throw new ArgumentException("Usage: spawn catalog <rdf-folder> mob|npc [query] [minLevel] [maxLevel] [limit].");
            var cat = SpawnCatalog.Load(args[1]);
            int Num(int i, int d) => args.Length > i && int.TryParse(args[i], out int n) ? n : d;
            return cat.ToResult(args[2], cat.Search(args[2], args.Length > 3 ? args[3] : null, Num(4, 0), Num(5, 255), Num(6, 20), 0));
        }
        if (args[0] == "portals")
        {
            if (args.Length < 2) throw new ArgumentException("Usage: spawn portals <spawn.rdf> [worldId] [mob|npc].");
            var ps = SpawnEditSession.Open(args[1], KindOf(args, 3));
            return ps.Portals(args.Length > 2 && uint.TryParse(args[2], out uint w) ? w : null);
        }
        if (args[0] == "validate")
        {
            var v = Configure(SpawnEditSession.Open(args[1], KindOf(args, 2)));
            v.TryAttachCatalog();
            var issues = v.Validate();
            return new { v.SourcePath, sha256 = v.SourceSha256, valid = !issues.Any(i => i.Severity == "error"), issues };
        }
        if (args[0] == "edit")
        {
            if (args.Length < 4) throw new ArgumentException("Usage: spawn edit <file> <ops.json> <new-candidate-dir> [mob|npc] [also.rdf ...].");
            var session = Configure(SpawnEditSession.Open(args[1], KindOf(args, 4)));
            session.TryAttachCatalog();
            using var opsDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[2]));
            var applied = session.ApplyOps(opsDoc.RootElement);
            var also = args.Skip(args.Length > 4 && args[4] is "mob" or "npc" ? 5 : 4).ToArray();
            var saved = session.SaveCandidate(args[3], also.Length > 0 ? also : null);
            return new { applied, saved };
        }
        string kind = args.Length > 2 ? args[2] : (Path.GetFileName(args[1]).Contains("npc", StringComparison.OrdinalIgnoreCase) ? "npc" : "mob");
        var file = SpawnReadService.Load(args[1], kind);
        switch (args[0])
        {
            case "summary": return SpawnReadService.Summarize(file);
            case "list":
                int limit = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 50;
                return new { file.Path, file.RowCount, rows = file.Rows.Take(limit).ToArray() };
            default: throw new ArgumentException("Unknown spawn command; see help.");
        }
    }
}
