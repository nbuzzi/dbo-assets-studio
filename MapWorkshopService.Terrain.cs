using System.Text.Json;

namespace DboAssetsStudio;

// PLANET-001 F2: deterministic original planet terrain (Namek POC) for the same OSM1 -> DFF/BSP path as the arena.
// Reference look: an archipelago in a green sea, terraced mesas with flat blue-grass tops and steep
// pink/ochre layered cliffs, sand on the shores, tall rock towers with grassy ledges, and one "dying Namek" area with
// cracked plates over lava. Ajisa trees and dome houses are the game's own objects, placed in a later step.
internal static partial class MapWorkshopService
{
    internal const string TerrainProfile = "assets-namek-terrain-v1";
    internal static readonly string[] TerrainTextures = ["orig_namek_grass_v2", "orig_namek_rock_v2", "orig_namek_water_v2", "orig_namek_cracked_v1", "orig_namek_lava_v1", "orig_namek_sand_v1"];
    private const int Grass = 0, Rock = 1, Water = 2, Cracked = 3, Lava = 4, Sand = 5;
    internal sealed record TerrainRecipe(string Profile, string Name, float Size, float Cell, int Seed, float HillHeight,
        float WaterLevel, int Spires, float DyingRadius, float LavaLevel, string? TextureManifest = null, string? TextureManifestSha256 = null);
    // Optional game textures (scripts/prepare-planet-native-textures.py), in material order, instead of the procedural set.
    private sealed record NativeTexture(string Material, string Texture, string File, string Sha256, string SourceEntry, string SourceSha256, float[] Tint);
    private sealed record NativeTextureManifest(string Schema, string Selection, JsonElement PackIndex, NativeTexture[] Textures);
    private static readonly string[] TerrainMaterials = ["grass", "rock", "water", "cracked", "lava", "sand"];

    private static bool IsTerrain(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new IOException("Bounded map JSON required.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.TryGetProperty("profile", out var p) && p.GetString() == TerrainProfile;
    }
    private static TerrainRecipe ValidateTerrain(string path)
    {
        var t = Read<TerrainRecipe>(path);
        int cells = (int)MathF.Round(t.Size / t.Cell);
        if (t.Profile != TerrainProfile || string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 80 || t.Name.Any(char.IsControl) ||
            t.Size is < 64 or > 480 || t.Cell is < 2 or > 16 || MathF.Abs(cells * t.Cell - t.Size) > 1e-3f || cells > 96 ||
            t.HillHeight is < 2 or > 20 || t.WaterLevel is < -20 or > 10 || t.Spires is < 0 or > 8 ||
            t.DyingRadius is < 0 or > 160 || t.DyingRadius > t.Size / 2 || t.LavaLevel is < -20 or > 10 ||
            new[] { t.Size, t.Cell, t.HillHeight, t.WaterLevel, t.DyingRadius, t.LavaLevel }.Any(x => !float.IsFinite(x)) ||
            (t.TextureManifest == null) != (t.TextureManifestSha256 == null))
            throw new InvalidDataException("Unsupported or unsafe planet terrain dimensions.");
        if (t.TextureManifest == null) return t;
        string manifest = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, t.TextureManifest));
        t = t with { TextureManifest = manifest };
        NativeTextures(t);
        return t;
    }
    // Every texture of the manifest must still have its recorded bytes; the manifest itself is pinned by the recipe.
    private static NativeTexture[] NativeTextures(TerrainRecipe t)
    {
        string path = t.TextureManifest!; ContentSafety.NoLinks(path);
        if (!string.Equals(ContentSafety.Hash(path), t.TextureManifestSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Texture manifest hash differs from the recipe.");
        var m = JsonSerializer.Deserialize<NativeTextureManifest>(File.ReadAllBytes(path), ContentSafety.Json) ?? throw new InvalidDataException("Null texture manifest.");
        if (m.Schema != "assets-planet-native-textures-v1" || m.Textures.Select(x => x.Material).SequenceEqual(TerrainMaterials) == false) throw new InvalidDataException("Texture manifest does not list the terrain materials in order.");
        string dir = Path.GetDirectoryName(path)!;
        foreach (var x in m.Textures)
        {
            string file = Path.Combine(dir, x.File);
            if (x.File != x.Texture + ".bmp" || !x.Texture.StartsWith("orig_namek_") || new FileInfo(file).Length != 54 + 512 * 512 * 3 ||
                !string.Equals(ContentSafety.Hash(file), x.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Texture differs from its manifest: " + x.File);
        }
        return m.Textures;
    }
    private static string[] TextureNames(TerrainRecipe t) => t.TextureManifest == null ? TerrainTextures : NativeTextures(t).Select(x => x.Texture).ToArray();
    // Seeded value noise; no platform random source, so a recipe always yields the same bytes.
    private static float Hash(int seed, int x, int z)
    {
        uint n = unchecked((uint)(x * 374761393 + z * 668265263 + seed * 1442695041));
        n = unchecked((n ^ (n >> 13)) * 1274126177u); n ^= n >> 16;
        return (n & 0xFFFFFF) / (float)0xFFFFFF;
    }
    private static float Noise(int seed, float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z); float fx = x - ix, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
        float a = Hash(seed, ix, iz), b = Hash(seed, ix + 1, iz), c = Hash(seed, ix, iz + 1), d = Hash(seed, ix + 1, iz + 1);
        return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz;
    }
    private static float Fbm(int seed, float x, float z, float freq, int octaves)
    {
        float h = 0, amp = 1, sum = 0;
        for (int o = 0; o < octaves; o++) { h += Noise(seed + o, x * freq, z * freq) * amp; sum += amp; amp *= .5f; freq *= 2; }
        return h / sum;
    }
    private static (float X, float Z) DyingCentre(TerrainRecipe t) => (t.Size * .5f - t.DyingRadius * .8f, t.Size * .5f - t.DyingRadius * .8f);
    private static float Dying(TerrainRecipe t, float x, float z)
    {
        if (t.DyingRadius <= 0) return 0;
        var c = DyingCentre(t); float d = MathF.Sqrt((x - c.X) * (x - c.X) + (z - c.Z) * (z - c.Z));
        return Math.Clamp(1.25f - d / t.DyingRadius, 0, 1);
    }
    // Islands: low frequency land mask; the arrival plateau around the centre is always land.
    private static float Land(TerrainRecipe t, float x, float z)
    {
        float centre = Math.Clamp(1 - MathF.Sqrt(x * x + z * z) / (t.Size * .22f), 0, 1);
        return Fbm(t.Seed, x, z, 1 / 150f, 4) - .47f + centre * .22f;
    }
    private static float Height(TerrainRecipe t, float x, float z)
    {
        float land = Land(t, x, z), h;
        if (land < 0) h = t.WaterLevel - 2 + land * 30;                                   // sea floor, hidden under the sea
        else
        {
            // Terraces: flat tops, steep risers (the riser is the last 18% of each tier).
            float levels = land * 9, tier = MathF.Floor(levels), frac = levels - tier;
            float riser = Math.Clamp((frac - .82f) / .18f, 0, 1); riser = riser * riser * (3 - 2 * riser);
            h = t.WaterLevel + 1.2f + (tier + riser) * t.HillHeight + (Fbm(t.Seed + 17, x, z, 1 / 20f, 2) - .5f) * .8f;
        }
        float dying = Dying(t, x, z);
        if (dying > 0)
        {
            // Broken plates: Voronoi-like cells whose seams sink below the lava level.
            float cell = 14, cx = x / cell, cz = z / cell, best = 9, second = 9;
            for (int i = -1; i <= 1; i++) for (int k = -1; k <= 1; k++)
            {
                int gx = (int)MathF.Floor(cx) + i, gz = (int)MathF.Floor(cz) + k;
                float px = gx + Hash(t.Seed + 31, gx, gz), pz = gz + Hash(t.Seed + 37, gx, gz);
                float dd = (cx - px) * (cx - px) + (cz - pz) * (cz - pz);
                if (dd < best) { second = best; best = dd; } else if (dd < second) second = dd;
            }
            float seam = Math.Clamp((MathF.Sqrt(second) - MathF.Sqrt(best)) * 4, 0, 1);
            h = h * (1 - dying) + dying * MathF.Max(t.WaterLevel + .3f, t.LavaLevel + .8f + seam * 4 - (1 - seam) * 3);
        }
        return h;
    }
    private static int GroundMaterial(TerrainRecipe t, float x0, float z0, float rise, float low)
    {
        if (Dying(t, x0 + t.Cell / 2, z0 + t.Cell / 2) > .5f) return Cracked;
        if (rise > t.Cell * .75f) return Rock;
        return Land(t, x0 + t.Cell / 2, z0 + t.Cell / 2) < .025f ? Sand : Grass;   // narrow beach along the shore
    }
    private static List<Mesh> GenerateTerrain(TerrainRecipe t)
    {
        var meshes = new List<Mesh>(); var cache = new Dictionary<string, Mesh>();
        // Prelight stays near white so the textures carry the colour; lava glows.
        byte[][] prelight = [[235, 235, 235, 255], [235, 235, 235, 255], [235, 235, 235, 255], [210, 210, 210, 255], [255, 255, 255, 255], [235, 235, 235, 255]];
        string[] names = TextureNames(t);
        Mesh Part(string part, int material) { string key = part + "_" + material; if (!cache.TryGetValue(key, out var m)) { m = new Mesh(key, prelight[material]) { Texture = names[material] }; cache.Add(key, m); meshes.Add(m); } return m; }
        int n = (int)MathF.Round(t.Size / t.Cell); float half = t.Size / 2;
        // Flat surfaces tile in X/Z; cliffs use horizontal strata (V follows height).
        Vertex Flat(float x, float y, float z) => new(x, y, z, x / 8, z / 8);
        // Cliff cells: project along the dominant horizontal axis of the slope so the texture does not stretch.
        Vertex CliffX(float x, float y, float z) => new(x, y, z, x / 12, y / 12);
        Vertex CliffZ(float x, float y, float z) => new(x, y, z, z / 12, y / 12);
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++)
        {
            float x0 = -half + i * t.Cell, z0 = -half + k * t.Cell, x1 = x0 + t.Cell, z1 = z0 + t.Cell;
            float ya = Height(t, x0, z0), yb = Height(t, x1, z0), yc = Height(t, x1, z1), yd = Height(t, x0, z1);
            float high = MathF.Max(MathF.Max(ya, yb), MathF.Max(yc, yd)), low = MathF.Min(MathF.Min(ya, yb), MathF.Min(yc, yd));
            if (high < t.WaterLevel - .5f) continue;   // fully under the sea: the sea plane covers it
            int material = GroundMaterial(t, x0, z0, high - low, low);
            bool alongX = MathF.Abs((yb + yc) - (ya + yd)) < MathF.Abs((yd + yc) - (ya + yb));   // slope runs along Z: the wall spans X
            Func<float, float, float, Vertex> V = material != Rock ? Flat : alongX ? CliffX : CliffZ;
            Part("ground", material).Quad(V(x0, ya, z0), V(x0, yd, z1), V(x1, yc, z1), V(x1, yb, z0));   // +Y facing in Taiwan coordinates
        }
        // Green sea over the whole square.
        Part("water", Water).Quad(Flat(-half, t.WaterLevel, -half), Flat(-half, t.WaterLevel, half), Flat(half, t.WaterLevel, half), Flat(half, t.WaterLevel, -half));
        // Lava sheet under the broken plates of the dying area.
        if (t.DyingRadius > 0)
        {
            var c = DyingCentre(t); const int seg = 24; float r = t.DyingRadius * 1.05f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * MathF.Tau / seg, a1 = (i + 1) * MathF.Tau / seg;
                Vertex P(float a) => Flat(Math.Clamp(c.X + r * MathF.Cos(a), -half, half), t.LavaLevel, Math.Clamp(c.Z + r * MathF.Sin(a), -half, half));
                Part("lava", Lava).Triangle(Flat(c.X, t.LavaLevel, c.Z), P(a1), P(a0));
            }
        }
        foreach (var s in Spires(t))
        {
            // Rock tower: stacked irregular drums that narrow upwards; each step leaves a blue-grass ledge.
            const int seg = 10; float y = Height(t, s.X, s.Z) - 3, r = s.R, cx = s.X, cz = s.Z;
            Vertex Ring(float radius, float angle, float yy, bool cliff) => cliff ? new(cx + radius * MathF.Cos(angle), yy, cz + radius * MathF.Sin(angle), angle * s.R / 12, yy / 12) : Flat(cx + radius * MathF.Cos(angle), yy, cz + radius * MathF.Sin(angle));
            for (int level = 0; level < s.Levels; level++)
            {
                float top = y + s.H / s.Levels * (.8f + .4f * Hash(t.Seed + 113, s.Id, level));
                float next = level == s.Levels - 1 ? 0 : r * (.5f + .15f * Hash(t.Seed + 127, s.Id, level));   // stays inside the narrowest wall (1 - .225)
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i * MathF.Tau / seg, a1 = (i + 1) * MathF.Tau / seg;
                    float w0 = 1 + .45f * (Hash(t.Seed + 131, s.Id * 16 + level, i) - .5f), w1 = 1 + .45f * (Hash(t.Seed + 131, s.Id * 16 + level, (i + 1) % seg) - .5f);
                    Part("tower", Rock).Quad(Ring(r * w0, a0, y, true), Ring(r * w0, a0, top, true), Ring(r * w1, a1, top, true), Ring(r * w1, a1, y, true));
                    if (next > 0)
                        Part("tower_ledge", Grass).Quad(Ring(r * w0, a0, top, false), Ring(next, a0, top, false), Ring(next, a1, top, false), Ring(r * w1, a1, top, false));
                    else Part("tower_ledge", Grass).Triangle(Flat(cx, top, cz), Ring(r * w1, a1, top, false), Ring(r * w0, a0, top, false));
                }
                // The next drum leans a little off centre, inside this one's ledge.
                cx += (Hash(t.Seed + 137, s.Id, level) - .5f) * (r - next) * .8f; cz += (Hash(t.Seed + 139, s.Id, level) - .5f) * (r - next) * .8f;
                y = top; r = next;
            }
        }
        return meshes;
    }
    private static IEnumerable<(int Id, float X, float Z, float R, float H, int Levels)> Spires(TerrainRecipe t)
    {
        float half = t.Size / 2 - 30;
        for (int i = 0, tries = 0; i < t.Spires && tries < t.Spires * 60; tries++)
        {
            float x = (Hash(t.Seed + 101, tries, 0) * 2 - 1) * half, z = (Hash(t.Seed + 103, tries, 1) * 2 - 1) * half;
            if (Dying(t, x, z) > 0 || MathF.Sqrt(x * x + z * z) < t.Size * .25f) continue;
            yield return (i++, x, z, 9 + Hash(t.Seed + 107, tries, 2) * 7, 55 + Hash(t.Seed + 109, tries, 3) * 45, 5 + (int)(Hash(t.Seed + 111, tries, 4) * 3));
        }
    }
    private static object TerrainProof(List<Mesh> meshes, TerrainRecipe t)
    {
        int triangles = 0; double seaArea = 0;
        foreach (var m in meshes) foreach (var f in m.Faces)
        {
            var a = m.Vertices[(int)f[0]]; var b = m.Vertices[(int)f[1]]; var c = m.Vertices[(int)f[2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (nx * nx + ny * ny + nz * nz < 1e-10 || new[] { a, b, c }.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || MathF.Abs(v.X) > 512 || MathF.Abs(v.Y) > 512 || MathF.Abs(v.Z) > 512))
                throw new InvalidDataException("Degenerate, nonfinite or out of bounds terrain triangle.");
            if (m.Name.StartsWith("ground_") || m.Name.StartsWith("water_") || m.Name.StartsWith("lava_") || m.Name.StartsWith("tower_ledge_")) { if (ny <= 0) throw new InvalidDataException("Terrain winding must face Taiwan +Y."); }
            if (m.Name.StartsWith("water_")) seaArea += ny / 2;
            triangles++;
        }
        int vertices = meshes.Sum(m => m.Vertices.Count);
        if (triangles > 20000 || vertices > 60000) throw new InvalidDataException("Terrain exceeds the native map workshop budget (20000 triangles, 60000 vertices).");
        if (Math.Abs(seaArea - t.Size * t.Size) > t.Size * t.Size * 1e-4) throw new InvalidDataException("The sea does not cover the terrain square.");
        return new { meshes = meshes.Count, vertices, triangles, sizeMetres = t.Size, cellMetres = t.Cell, seaArea, towers = Spires(t).Count(),
            materials = meshes.Select(m => m.Texture).Distinct().Order(StringComparer.Ordinal).ToArray(), upwardWinding = true };
    }
    // Walkable ground: land cells that are not cliffs, not under the sea or lava, and not under a tower.
    private static void WriteTerrainGround(string path, TerrainRecipe t)
    {
        int n = (int)MathF.Round(t.Size / t.Cell); float half = t.Size / 2; var spires = Spires(t).ToArray();
        var cells = new List<(int I, int K)>();
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++)
        {
            float x0 = -half + i * t.Cell, z0 = -half + k * t.Cell, cx = x0 + t.Cell / 2, cz = z0 + t.Cell / 2;
            float[] y = [Height(t, x0, z0), Height(t, x0 + t.Cell, z0), Height(t, x0 + t.Cell, z0 + t.Cell), Height(t, x0, z0 + t.Cell)];
            float low = y.Min(), rise = y.Max() - low;
            bool dying = Dying(t, cx, cz) > .5f;
            if (low < t.WaterLevel || (dying && low < t.LavaLevel) || (!dying && rise > t.Cell * .75f) ||
                spires.Any(s => (cx - s.X) * (cx - s.X) + (cz - s.Z) * (cz - s.Z) < (s.R + t.Cell) * (s.R + t.Cell))) continue;
            cells.Add((i, k));
        }
        using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew)); w.Write("NCP1"u8.ToArray());
        w.Write((uint)((n + 1) * (n + 1))); w.Write((uint)(cells.Count * 2));
        for (int i = 0; i <= n; i++) for (int k = 0; k <= n; k++) { float x = -half + i * t.Cell, z = -half + k * t.Cell; w.Write(x); w.Write(Height(t, x, z)); w.Write(z); }
        foreach (var (i, k) in cells)
        {
            uint a = (uint)(i * (n + 1) + k), b = (uint)((i + 1) * (n + 1) + k), c = b + 1, d = a + 1;
            foreach (uint v in new[] { a, d, c, a, c, b }) w.Write(v);
        }
    }
    private static object[] TerrainMarkers(TerrainRecipe t)
    {
        var dying = DyingCentre(t);
        return [new { name = "Llegada propuesta", x = 0f, y = Height(t, 0, 0), z = 0f },
            new { name = "Namek moribundo", x = dying.X, y = Height(t, dying.X, dying.Z), z = dying.Z }];
    }
    private static void WriteTerrainTextures(string dir, TerrainRecipe t)
    {
        if (t.TextureManifest != null)
        {
            foreach (var x in NativeTextures(t)) File.Copy(Path.Combine(Path.GetDirectoryName(t.TextureManifest)!, x.File), Path.Combine(dir, x.File));
            return;
        }
        foreach (var (name, index) in TerrainTextures.Select((x, i) => (x, i)))
            WriteTextureBmp(Path.Combine(dir, name + ".bmp"), index);
    }
    private static void WriteTextureBmp(string path, int material)
    {
        const int size = 512, bytes = size * size * 3;
        using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew));
        w.Write((ushort)0x4d42); w.Write((uint)(54 + bytes)); w.Write(0u); w.Write(54u); w.Write(40u);
        w.Write(size); w.Write(size); w.Write((ushort)1); w.Write((ushort)24); w.Write(0u); w.Write((uint)bytes);
        w.Write(2835); w.Write(2835); w.Write(0u); w.Write(0u);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            // Tileable: every term repeats over the 512 pixel square.
            float u = x / (float)size, v = y / (float)size, grain = Hash(material, x, y);
            float soft = (MathF.Sin(u * MathF.Tau * 4 + MathF.Sin(v * MathF.Tau * 2) * 2) + MathF.Cos(v * MathF.Tau * 6)) * .25f;
            float r, g, b;
            switch (material)
            {
                case Grass:
                {
                    // Saturated anime blue with darker grass strokes.
                    float stroke = MathF.Abs(MathF.Sin((u * 11 + MathF.Sin(v * MathF.Tau * 3) * .6f) * MathF.Tau)) < .12f && Hash(9, x / 6, y / 3) > .45f ? 1 : 0;
                    r = 46 + soft * 14 + grain * 14 - stroke * 22; g = 118 + soft * 18 + grain * 16 - stroke * 38; b = 222 + soft * 12 + grain * 10 - stroke * 40;
                    break;
                }
                case Rock:
                {
                    // Horizontal strata: pink and ochre bands with thin dark lines (V follows height on cliffs).
                    float band = MathF.Sin(v * MathF.Tau * 5 + MathF.Sin(u * MathF.Tau * 2) * .8f) * .5f + .5f;
                    r = 214 - band * 12 + grain * 16; g = 150 + band * 28 + grain * 14; b = 140 - band * 40 + grain * 12;
                    if (MathF.Abs(MathF.Sin(v * MathF.Tau * 10 + MathF.Sin(u * MathF.Tau) * 1.2f)) < .07f) { r -= 50; g -= 45; b -= 40; }
                    break;
                }
                case Water:
                    r = 74 + soft * 18 + grain * 6; g = 196 + soft * 22 + grain * 8; b = 88 + soft * 14 + grain * 6;
                    if (MathF.Abs(MathF.Sin((u * 8 + MathF.Sin(v * MathF.Tau * 4) * .3f) * MathF.Tau)) < .05f) { r += 40; g += 30; b += 40; }
                    break;
                case Cracked:
                {
                    r = 62 + grain * 22; g = 54 + grain * 18; b = 52 + grain * 16;
                    float crack = MathF.Abs(MathF.Sin(u * MathF.Tau * 3 + MathF.Sin(v * MathF.Tau * 5) * 1.6f) * MathF.Cos(v * MathF.Tau * 2 + u * MathF.Tau));
                    if (crack < .06f) { r = 210; g = 70 + grain * 40; b = 20; }   // glowing fractures between the plates
                    break;
                }
                case Lava:
                    r = 230 + grain * 25; g = 96 + soft * 60 + grain * 40; b = 24 + grain * 20;
                    if (soft + grain * .5f < -.1f) { r *= .45f; g *= .35f; b *= .3f; }   // cooling crust
                    break;
                default:   // pale shore sand
                    r = 226 + soft * 10 + grain * 14; g = 206 + soft * 10 + grain * 12; b = 150 + soft * 8 + grain * 12;
                    break;
            }
            w.Write((byte)Math.Clamp((int)b, 0, 255)); w.Write((byte)Math.Clamp((int)g, 0, 255)); w.Write((byte)Math.Clamp((int)r, 0, 255));
        }
    }
}
