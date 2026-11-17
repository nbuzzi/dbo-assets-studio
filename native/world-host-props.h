#pragma once
// World Editor host · field properties, tile properties, game property volumes and sector PVS.
// Included by world-host.cpp after world-host-edit.h. Commands: prop-* and pvs-*.
//
// Field props (wfpf<N>): GetAFieldProp/SetAFieldProp, LOADED fields only (memory -> SaveField). The in-place "rb+"
//   branch of SetAFieldProp for unloaded fields is never used: only its SKY offsets were ever checked.
//   LIGHT and SOUND are read-only here (the light/sound tool owns them).
// Tile props: sNTL_FIELD_PROP::_pBitProp (DWORD per tile: low 16 bits = eDBO_WORLD_ATTR_GROUP flags read by the
//   client through GetWorldNormalAttribute, high 16 bits = "special" attribute) and _pMaterialProp (BYTE per tile,
//   foot-step material). Index = tx + tz * (tilesPerSector * 2), tx/tz counted from the field's SPos (min corner),
//   exactly as CNtlWorldFieldManager::GetMapTileProp.
// Game property: PLENTITY_GAME_PROPERTY volumes, only loaded for indoor (RpWorld) sectors. Needs the core fix in
//   CNtlPLGameProperty::Create (world tool-mode engine build) or uiProperty is lost and re-saved as 0.
// PVS: CNtlWorldSector::CreateSectorPVS per selected sector; the engine has no writer for P\------- so this module
//   rewrites it on save: only the recalculated sector records change, every other record is copied byte for byte.
#include "NtlWorldSectorPVS.h"
#include "NtlPLGameProperty.h"
#include "ntlworldsectorinfo.h"
#include <memory>
#include <chrono>
#include <map>

static CNtlWorldField* PropFields() { CNtlWorldFieldManager* fm = FieldMgr(); return fm ? const_cast<CNtlWorldField*>(fm->GetFields()) : nullptr; }
static int TilesPerField() { return dGET_WORLD_PARAM()->WorldSectorTileNum * dGET_WORLD_PARAM()->WorldFieldSectorNum; }
static RwV3d FieldCenter(int f) {
    RwV3d s = PropFields()[f].GetSPos(); float h = dGET_WORLD_PARAM()->WorldFieldSize * 0.5f;
    RwV3d c = { s.x + h, 0, s.z + h }; return c;
}
static int FieldAtXZ(double x, double z) { CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return -1; RwV3d p = { (RwReal)x, 0, (RwReal)z }; return fm->GetFieldIdx(p); }
static int FieldFromCmd(const Value& cmd) {
    if (cmd.has("field")) return (int)cmd.num("field", -1);
    return FieldAtXZ(cmd.num("x", gPos.x), cmd.num("z", gPos.z));
}
static bool FieldReady(int f) { CNtlWorldFieldManager* fm = FieldMgr(); return fm && f >= 0 && fm->IsFieldLoaded(f) && PropFields()[f].GetProp() != nullptr; }

// ---- JSON <-> sNTL_FIELD_PROP -------------------------------------------------------------------------------------
static Value Arr3(double a, double b, double c) { Value v = Value::arr(); v.array = { Value(a), Value(b), Value(c) }; return v; }
static Value Rgb(const RwRGBA& c) { return Arr3(c.red, c.green, c.blue); }
static Value RgbaReal(const RwRGBAReal& c) { Value v = Value::arr(); v.array = { Value((double)c.red), Value((double)c.green), Value((double)c.blue), Value((double)c.alpha) }; return v; }
static std::string Cstr(const char* s, size_t n) { size_t l = 0; while (l < n && s[l]) ++l; return std::string(s, l); }
static const Value& Need(const Value& v, const char* k) { return v.at(k); }
static double Num(const Value& v) { Require(v.type == Value::Number, "number expected"); return v.number; }
static void ToRgb(const Value& v, RwRGBA& c) {
    Require(v.type == Value::Array && v.array.size() >= 3, "color [r,g,b] expected");
    auto b = [](double d) { return (RwUInt8)std::max(0.0, std::min(255.0, d)); };
    c.red = b(Num(v.array[0])); c.green = b(Num(v.array[1])); c.blue = b(Num(v.array[2]));
    if (v.array.size() > 3) c.alpha = b(Num(v.array[3]));
}
static void ToVec(const Value& v, RwV3d& p) { Require(v.type == Value::Array && v.array.size() == 3, "vector [x,y,z] expected"); p.x = (RwReal)Num(v.array[0]); p.y = (RwReal)Num(v.array[1]); p.z = (RwReal)Num(v.array[2]); }
static void ToName(const Value& v, char* dst, size_t cap) {
    std::string s = v.text(); Require(s.size() < cap, "name too long for the engine field");
    for (char ch : s) Require((unsigned char)ch >= 32 && (unsigned char)ch < 127, "names must be printable ASCII");
    strcpy_s(dst, cap, s.c_str());
}

static Value FieldPropJson(sNTL_FIELD_PROP& p) {
    Value v = Value::obj();
    v["name"] = Cstr(p._Name, 64);
    Value fog = Value::obj(); fog["color"] = Rgb(p._FogColor); fog["near"] = (double)p._FogCamPlane[0]; fog["far"] = (double)p._FogCamPlane[1]; v["fog"] = fog;
    Value sky = Value::obj(); sky["mode"] = p._BaseSkyMode; sky["newSkyValue"] = p._NewSkyValue;
    Value sc = Value::arr(); for (int i = 0; i < 3; ++i) sc.array.push_back(Rgb(p._RGBSkyColor[i])); sky["colors"] = sc;
    sky["baseTexture"] = Cstr(p._BaseSkyTexName, 10); sky["baseSpeed"] = (double)p._BaseSkySpeed;
    Value bt = Value::arr(), bs = Value::arr();
    for (int i = 0; i < 2; ++i) { bt.array.push_back(Value(Cstr(p._BlendedTexName[i], 10))); bs.array.push_back(Value((double)p._BlendedTexSpeed[i])); }
    sky["blendTextures"] = bt; sky["blendSpeeds"] = bs; v["sky"] = sky;
    Value light = Value::obj(); light["ambient"] = RgbaReal(p._ClrLightAmbient); Value dirs = Value::arr();
    for (int i = 0; i < dNTL_PL_DIRECTIONAL_LIGHT_NUM; ++i) { Value d = Value::obj(); d["color"] = RgbaReal(p._ClrLightDirectional[i]); d["angle"] = Arr3(p._AngLightDirectional[i].x, p._AngLightDirectional[i].y, p._AngLightDirectional[i].z); dirs.array.push_back(d); }
    light["directional"] = dirs; light["readOnly"] = true; v["light"] = light;
    Value snd = Value::obj(); snd["bgm"] = p._IdxBGMTbl; snd["env"] = p._IdxEnvTbl; Value sh = Value::arr(); for (int i = 0; i < 5; ++i) sh.array.push_back(Value(p._IdxShareTbl[i])); snd["share"] = sh; snd["readOnly"] = true; v["sound"] = snd;
    Value bloom = Value::obj(); bloom["monoPower"] = (double)p._MonoPower; v["bloom"] = bloom;
    Value planets = Value::arr();
    for (int i = 0; i < 3; ++i) { sNTL_PLANET& pl = p._NtlPlanet[i]; Value o = Value::obj(); o["pos"] = Arr3(pl._RelativePos.x, pl._RelativePos.y, pl._RelativePos.z); o["scale"] = (double)pl._Scale; o["texture"] = Cstr(pl._TexName, 10); o["lensFlare"] = pl._LensFlareExistence != FALSE; o["empty"] = pl.IsEmpty() != FALSE; planets.array.push_back(o); }
    v["planet"] = planets;
    Value spec = Value::obj(); spec["enabled"] = p._NtlSpecular._SpecularExistence != FALSE; spec["direction"] = Arr3(p._NtlSpecular._Direction.x, p._NtlSpecular._Direction.y, p._NtlSpecular._Direction.z);
    Value ss = Value::arr(); ss.array = { Value((double)p._NtlSpecular._Scale.x), Value((double)p._NtlSpecular._Scale.y) }; spec["scale"] = ss; spec["power"] = (int)p._NtlSpecular._Power; v["specular"] = spec;
    v["fieldColor"] = Rgb(p._FieldColor);
    Value w = Value::arr(); for (int i = 0; i < 5; ++i) w.array.push_back(Value(p._IdxWeather[i])); v["weather"] = w;
    Value hh = Value::obj(); hh["enabled"] = p._NtlHeatHaze._HeatHazeExistence != FALSE; hh["metersPerSec"] = (double)p._NtlHeatHaze._MetersPerSec; hh["extent"] = (double)p._NtlHeatHaze._Extent;
    hh["extentRand"] = (double)p._NtlHeatHaze._Extent_Rand; hh["scale"] = (double)p._NtlHeatHaze._Scale; v["heatHaze"] = hh;
    return v;
}

// Applies the groups present in `in` to `p`; returns the engine property ids that changed group.
static std::vector<eNTL_FIELD_PROPID> ApplyFieldPropJson(sNTL_FIELD_PROP& p, const Value& in) {
    std::vector<eNTL_FIELD_PROPID> ids;
    Require(in.type == Value::Object, "props object expected");
    for (auto& kv : in.object) {
        const std::string& k = kv.first; const Value& g = kv.second;
        if (k == "name") { ToName(g, p._Name, 64); ids.push_back(eNFP_NAME); }
        else if (k == "fog") {
            if (g.has("color")) ToRgb(g.at("color"), p._FogColor);
            if (g.has("near")) p._FogCamPlane[0] = (RwReal)g.num("near");
            if (g.has("far")) p._FogCamPlane[1] = (RwReal)g.num("far");
            Require(p._FogCamPlane[0] >= 0 && p._FogCamPlane[1] > p._FogCamPlane[0], "fog: 0 <= near < far");
            ids.push_back(eNFP_FOG);
        } else if (k == "sky") {
            if (g.has("mode")) p._BaseSkyMode = (RwInt32)g.num("mode");
            if (g.has("newSkyValue")) p._NewSkyValue = (RwInt32)g.num("newSkyValue");
            if (g.has("colors")) { const Value& c = g.at("colors"); Require(c.array.size() == 3, "sky.colors: 3 colors"); for (int i = 0; i < 3; ++i) ToRgb(c.array[i], p._RGBSkyColor[i]); }
            if (g.has("baseTexture")) ToName(g.at("baseTexture"), p._BaseSkyTexName, 10);
            if (g.has("baseSpeed")) p._BaseSkySpeed = (RwReal)g.num("baseSpeed");
            if (g.has("blendTextures")) { const Value& t = g.at("blendTextures"); Require(t.array.size() == 2, "sky.blendTextures: 2 names"); for (int i = 0; i < 2; ++i) ToName(t.array[i], p._BlendedTexName[i], 10); }
            if (g.has("blendSpeeds")) { const Value& t = g.at("blendSpeeds"); Require(t.array.size() == 2, "sky.blendSpeeds: 2 numbers"); for (int i = 0; i < 2; ++i) p._BlendedTexSpeed[i] = (RwReal)Num(t.array[i]); }
            ids.push_back(eNFP_SKY);
        } else if (k == "bloom") { p._MonoPower = (RwReal)std::max(0.0, std::min(20.0, g.num("monoPower", p._MonoPower))); ids.push_back(eNFP_BLOOM); }
        else if (k == "planet") {
            Require(g.type == Value::Array && g.array.size() <= 3, "planet: up to 3 entries");
            for (size_t i = 0; i < g.array.size(); ++i) {
                const Value& o = g.array[i]; if (o.type != Value::Object) continue;   // null keeps the slot
                sNTL_PLANET& pl = p._NtlPlanet[i];
                if (o.has("pos")) ToVec(o.at("pos"), pl._RelativePos);
                if (o.has("scale")) pl._Scale = (RwReal)o.num("scale");
                if (o.has("texture")) ToName(o.at("texture"), pl._TexName, 10);
                if (o.has("lensFlare")) pl._LensFlareExistence = o.flag("lensFlare") ? TRUE : FALSE;
            }
            ids.push_back(eNFP_PLANET);
        } else if (k == "specular") {
            if (g.has("enabled")) p._NtlSpecular._SpecularExistence = g.flag("enabled") ? TRUE : FALSE;
            if (g.has("direction")) ToVec(g.at("direction"), p._NtlSpecular._Direction);
            if (g.has("scale")) { const Value& s = g.at("scale"); Require(s.array.size() == 2, "specular.scale: [x,y]"); p._NtlSpecular._Scale.x = (RwReal)Num(s.array[0]); p._NtlSpecular._Scale.y = (RwReal)Num(s.array[1]); }
            if (g.has("power")) p._NtlSpecular._Power = (RwUInt8)std::max(0.0, std::min(255.0, g.num("power")));
            ids.push_back(eNFP_SPECULAR);
        } else if (k == "fieldColor") { ToRgb(g, p._FieldColor); ids.push_back(eNFP_FIELDCOLOR); }
        else if (k == "weather") { Require(g.type == Value::Array && g.array.size() == 5, "weather: 5 indices (-1 = none)"); for (int i = 0; i < 5; ++i) p._IdxWeather[i] = (RwInt32)Num(g.array[i]); ids.push_back(eNFP_WEATHER); }
        else if (k == "heatHaze") {
            sNTL_HEATHAZE& h = p._NtlHeatHaze;
            if (g.has("enabled")) h._HeatHazeExistence = g.flag("enabled") ? TRUE : FALSE;
            if (g.has("metersPerSec")) h._MetersPerSec = (RwReal)g.num("metersPerSec");
            if (g.has("extent")) h._Extent = (RwReal)g.num("extent");
            if (g.has("extentRand")) h._Extent_Rand = (RwReal)g.num("extentRand");
            if (g.has("scale")) h._Scale = (RwReal)g.num("scale");
            ids.push_back(eNFP_FIELDHEATHAZE);
        } else if (k == "light" || k == "sound") throw std::runtime_error(k + " is read-only in the props tool (light/sound tool)");
        else throw std::runtime_error("unknown field prop group: " + k);
    }
    return ids;
}

// Full snapshot of a loaded field's properties (tile arrays included) for undo.
static std::shared_ptr<sNTL_FIELD_PROP> SnapshotFieldProp(int f) {
    auto snap = std::make_shared<sNTL_FIELD_PROP>(); *snap = *PropFields()[f].GetProp(); return snap;
}
static void RestoreFieldProp(int f, const std::shared_ptr<sNTL_FIELD_PROP>& snap, const std::vector<eNTL_FIELD_PROPID>& ids) {
    if (!FieldReady(f)) return;
    RwV3d c = FieldCenter(f);
    for (auto id : ids) FieldMgr()->SetAFieldProp(c, *snap, id);
}

static Value FieldPropReply(int f, const char* name = "field-prop") {
    Value v = Event(name); v["field"] = f;
    CNtlWorldFieldManager* fm = FieldMgr(); int perSide = dGET_WORLD_PARAM()->WorldSize / dGET_WORLD_PARAM()->WorldFieldSize; Require(fm && f >= 0 && f < perSide * perSide, "invalid field");
    v["loaded"] = fm->IsFieldLoaded(f) != FALSE;
    RwV3d s = PropFields()[f].GetSPos(); v["spos"] = Arr3(s.x, s.y, s.z); v["size"] = dGET_WORLD_PARAM()->WorldFieldSize;
    sNTL_FIELD_PROP p; RwV3d c = FieldCenter(f);
    bool ok = fm->GetAFieldProp(c, p) != FALSE; v["ok"] = ok;
    if (ok) v["props"] = FieldPropJson(p);
    return v;
}

// ---- tile props (bit / material) ---------------------------------------------------------------------------------
enum PropTool { PRT_NONE, PRT_TILE };
static PropTool gPropTool = PRT_NONE;
static bool gTileMaterial = false;              // target: false = _pBitProp, true = _pMaterialProp
static DWORD gTileMask = 1u << 4;   // DBO_WORLD_ATTR_GROUP_FLAG_FORBID_ALL_MOVE
static bool gTileClear = false;                 // bit target: set or clear the mask
static int gTileValue = 0;                      // material target: value to write
static float gTileRadius = 6.f;
static bool gTileDown = false, gTileHover = false; static RwV3d gTilePos = { 0, 0, 0 };
// overlay
static int gTileOverlay = 0;                    // 0 none, 1 bits (mask), 2 material
static DWORD gTileOverlayMask = 0xFFFFFFFF;
static float gTileOverlayRadius = 96.f;

static void SnapshotFieldTiles(int f) {
    if (!gOpenStep) return;
    if (!gOpenStep->snapshots.insert((1LL << 40) + (long long)f).second) return;
    auto snap = SnapshotFieldProp(f);
    gOpenStep->fields.insert(f);
    gOpenStep->restore.push_back([f, snap]() { RestoreFieldProp(f, snap, { eNFP_BITPROP, eNFP_MATERIALPROP }); });
}

// Tile containing world x,z: field, tile x/z inside the field, flat index.
struct TileRef { int field = -1, tx = 0, tz = 0, index = 0; };
static TileRef TileAt(float x, float z) {
    TileRef t; t.field = FieldAtXZ(x, z); if (t.field < 0) return t;
    RwV3d s = PropFields()[t.field].GetSPos(); int ts = dGET_WORLD_PARAM()->WorldSectorTileSize, n = TilesPerField();
    t.tx = std::min(n - 1, std::max(0, (int)(fabsf(x - s.x)) / ts)); t.tz = std::min(n - 1, std::max(0, (int)(fabsf(z - s.z)) / ts));
    t.index = t.tx + t.tz * n; return t;
}

// Applies the tile brush to every tile whose centre lies within the radius. Loaded fields only.
static int ApplyTiles(float cx, float cz, float radius, bool material, DWORD mask, bool clear, int value) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return 0;
    int ts = dGET_WORLD_PARAM()->WorldSectorTileSize, n = TilesPerField(), changed = 0;
    std::set<int> fields;
    for (float z = cz - radius - ts; z <= cz + radius + ts; z += (float)ts)
        for (float x = cx - radius - ts; x <= cx + radius + ts; x += (float)ts) {
            TileRef t = TileAt(x, z); if (t.field < 0 || !FieldReady(t.field)) continue;
            RwV3d s = PropFields()[t.field].GetSPos();
            float tcx = s.x + (t.tx + 0.5f) * ts, tcz = s.z + (t.tz + 0.5f) * ts;
            if ((tcx - cx) * (tcx - cx) + (tcz - cz) * (tcz - cz) > radius * radius) continue;
            sNTL_FIELD_PROP* p = PropFields()[t.field].GetProp();
            if (material) {
                if (!p->_pMaterialProp || p->_pMaterialProp[t.index] == (BYTE)value) continue;
                SnapshotFieldTiles(t.field); p->_pMaterialProp[t.index] = (BYTE)value;
            } else {
                if (!p->_pBitProp) continue;
                DWORD nv = clear ? (p->_pBitProp[t.index] & ~mask) : (p->_pBitProp[t.index] | mask);
                if (nv == p->_pBitProp[t.index]) continue;
                SnapshotFieldTiles(t.field); p->_pBitProp[t.index] = nv;
            }
            fields.insert(t.field); ++changed;
        }
    for (int f : fields) MarkDirtyField(f);
    (void)n; return changed;
}

static Value TileInfo(float x, float z) {
    Value v = Event("tile"); TileRef t = TileAt(x, z); v["field"] = t.field; v["tx"] = t.tx; v["tz"] = t.tz; v["index"] = t.index;
    if (t.field >= 0 && FieldReady(t.field)) {
        sNTL_FIELD_PROP* p = PropFields()[t.field].GetProp();
        DWORD b = p->_pBitProp ? p->_pBitProp[t.index] : 0; v["bits"] = (double)b; v["group"] = (int)(b & 0xFFFF); v["special"] = (int)(b >> 16);
        v["material"] = p->_pMaterialProp ? (int)p->_pMaterialProp[t.index] : 0;
        RwV3d q = { x, 0, z }; v["engineBits"] = (double)FieldMgr()->GetMapTileProp(q); v["engineMaterial"] = (int)FieldMgr()->GetMaterialTileProp(q);
        v["loaded"] = true;
    } else v["loaded"] = false;
    return v;
}

static Value TileStats(int f) {
    Value v = Event("tile-stats"); v["field"] = f; Require(FieldReady(f), "field not loaded");
    sNTL_FIELD_PROP* p = PropFields()[f].GetProp(); int n = TilesPerField() * TilesPerField();
    int bitCount[32] = {}; std::map<int, int> mats; int nonZero = 0;
    for (int i = 0; i < n; ++i) {
        DWORD b = p->_pBitProp ? p->_pBitProp[i] : 0; if (b) ++nonZero;
        for (int k = 0; k < 32; ++k) if (b & (1u << k)) ++bitCount[k];
        mats[p->_pMaterialProp ? p->_pMaterialProp[i] : 0]++;
    }
    Value bits = Value::obj(); for (int k = 0; k < 32; ++k) if (bitCount[k]) bits[std::to_string(k)] = bitCount[k];
    Value m = Value::obj(); for (auto& kv : mats) m[std::to_string(kv.first)] = kv.second;
    v["tiles"] = n; v["nonZeroBits"] = nonZero; v["bitCounts"] = bits; v["materials"] = m;
    return v;
}

// Overlay colours: one hue per material value / per lowest set bit of the masked attribute.
static RwRGBA OverlayColor(int k) {
    static const RwRGBA pal[] = { {255,80,70,110}, {70,170,255,110}, {255,215,60,110}, {80,230,120,110}, {200,90,255,110}, {255,140,40,110}, {60,230,230,110}, {240,240,240,110} };
    return pal[k % 8];
}
static std::vector<RwIm3DVertex> gTileVerts; static RwV3d gTileVertsAt = { 1e9f, 0, 1e9f }; static int gTileVertsGen = -1; static int gTileVertsMode = -1; static DWORD gTileVertsMask = 0;
static void BuildTileOverlay(const RwV3d& c) {
    gTileVerts.clear(); CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return;
    int ts = dGET_WORLD_PARAM()->WorldSectorTileSize; float r = gTileOverlayRadius;
    float x0 = floorf((c.x - r) / ts) * ts, z0 = floorf((c.z - r) / ts) * ts;
    auto h = [&](float x, float z) { RwV3d q = { x, 0, z }; int si = fm->GetSectorIdx(q); if (si < 0 || !fm->m_pSectors[si].m_bLoaded) return c.y; fm->GetHeight(q); return q.y + 0.3f; };
    for (float z = z0; z < c.z + r; z += ts)
        for (float x = x0; x < c.x + r; x += ts) {
            TileRef t = TileAt(x + ts * 0.5f, z + ts * 0.5f); if (t.field < 0 || !FieldReady(t.field)) continue;
            sNTL_FIELD_PROP* p = PropFields()[t.field].GetProp(); int key = -1;
            if (gTileOverlay == 2) { int m = p->_pMaterialProp ? p->_pMaterialProp[t.index] : 0; if (m) key = m; }
            else { DWORD b = (p->_pBitProp ? p->_pBitProp[t.index] : 0) & gTileOverlayMask; if (b) { key = 0; while (!(b & 1u)) { b >>= 1; ++key; } } }
            if (key < 0) continue;
            RwRGBA col = OverlayColor(key);
            float y00 = h(x, z), y10 = h(x + ts, z), y01 = h(x, z + ts), y11 = h(x + ts, z + ts);
            RwV3d q[6] = { {x, y00, z}, {x + ts, y10, z}, {x + ts, y11, z + ts}, {x, y00, z}, {x + ts, y11, z + ts}, {x, y01, z + ts} };
            for (auto& pt : q) { RwIm3DVertex v; RwIm3DVertexSetPos(&v, pt.x, pt.y, pt.z); RwIm3DVertexSetRGBA(&v, col.red, col.green, col.blue, col.alpha); gTileVerts.push_back(v); }
        }
}
static void RenderTris(std::vector<RwIm3DVertex>& v) {
    if (v.empty()) return;
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEVERTEXALPHAENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATESRCBLEND, (void*)rwBLENDSRCALPHA);
    RwRenderStateSet(rwRENDERSTATEDESTBLEND, (void*)rwBLENDINVSRCALPHA);
    RwRenderStateSet(rwRENDERSTATEFOGENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATECULLMODE, (void*)rwCULLMODECULLNONE);
    for (size_t i = 0; i < v.size(); i += 2997) {
        RwInt32 n = (RwInt32)std::min<size_t>(2997, v.size() - i);
        if (RwIm3DTransform(&v[i], n, nullptr, rwIM3D_VERTEXRGBA | rwIM3D_VERTEXXYZ)) { RwIm3DRenderPrimitive(rwPRIMTYPETRILIST); RwIm3DEnd(); }
    }
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)TRUE);
}

// ---- game property volumes --------------------------------------------------------------------------------------
static Value GamePropJson(int sector, int i, CNtlPLGameProperty* g) {
    Value o = Value::obj(); o["sector"] = sector; o["index"] = i; o["shape"] = (int)g->GetShapeType();
    RwV3d p = g->GetPosition(), s = g->GetSize(); o["pos"] = Arr3(p.x, p.y, p.z); o["size"] = Arr3(s.x, s.y, s.z); o["property"] = (double)g->GetGameProperty();
    return o;
}
// Engine-level round trip of the volume record: CreateEntity(uiProperty) -> GamePropertySaveIntoFile -> GamePropertyLoadFromFile.
static Value GamePropSelfTest(RwUInt32 value) {
    Value v = Event("gameprop-selftest");
    SPLGamePropertyCreateParam cp; RwV3d pos = { gPos.x, gPos.y, gPos.z }; cp.pPos = &pos; cp.eShapeType = ENTL_PL_GAME_PROPERTY_SHAPE_PLANE; cp.vSize = { 8, 4, 8 }; cp.uiProperty = value;
    auto* g = static_cast<CNtlPLGameProperty*>(GetSceneManager()->CreateEntity(PLENTITY_GAME_PROPERTY, "NULL", &cp));
    Require(g != nullptr, "CreateEntity(PLENTITY_GAME_PROPERTY)");
    v["created"] = (double)g->GetGameProperty();
    char tmp[MAX_PATH]; GetTempPathA(MAX_PATH, tmp); std::string path = std::string(tmp) + "world-host-gameprop-" + std::to_string(GetCurrentProcessId()) + ".bin";
    FILE* f = fopen(path.c_str(), "wb"); Require(f != nullptr, "temp file");
    GetNtlWorldSectorInfo()->GamePropertySaveIntoFile(f, g); long bytes = ftell(f); fclose(f);
    f = fopen(path.c_str(), "rb"); Require(f != nullptr, "temp file");
    RwUInt32 raw[8] = {}; fread(raw, 1, std::min<long>(bytes, sizeof(raw)), f); fseek(f, 0, SEEK_SET);
    auto* back = GetNtlWorldSectorInfo()->GamePropertyLoadFromFile(f); fclose(f); DeleteFileA(path.c_str());
    Require(back != nullptr, "GamePropertyLoadFromFile");
    v["recordBytes"] = (int)bytes; v["savedProperty"] = (double)raw[7]; v["loaded"] = (double)back->GetGameProperty();
    v["shapeBack"] = (int)back->GetShapeType();
    v["ok"] = g->GetGameProperty() == value && raw[7] == value && back->GetGameProperty() == value;
    GetSceneManager()->DeleteEntity(back); GetSceneManager()->DeleteEntity(g);
    return v;
}

// ---- PVS -----------------------------------------------------------------------------------------------------------
static const int kPvsRecord = dPVS_TOT_CELL_CNT * dPVS_LAYER_CNT;   // 169 cells x 6 layers
static std::set<int> gPvsDirty;          // recalculated since the last save (written on save)
static std::set<int> gPvsEverWritten;    // for the audit (pvs-edits.json in the workspace root)
static bool gPvsOverlay = false; static int gPvsLayer = 0; static int gPvsPinned = -1;
// The job keeps its own undo step (pushed when it finishes) so a brush stroke during the build cannot interleave.
struct PvsJob { std::vector<int> queue; size_t next = 0; float eps = 10.f; std::string replyId; bool hasReply = false; double ms = 0; UndoStep step{ "pvs" }; };
static std::unique_ptr<PvsJob> gPvsJob;
static void PropsHeightCache(bool on);

static void SnapshotPvs(UndoStep& st, int idx) {
    if (!st.snapshots.insert((2LL << 40) + idx).second) return;
    CNtlWorldSectorPVS* pvs = FieldMgr()->m_pSectors[idx].m_pNtlWorldSectorPVS;
    bool had = pvs && pvs->GetVisibilityNeighbor() != nullptr; bool wasDirty = gPvsDirty.count(idx) > 0;
    std::vector<BYTE> buf; if (had) buf.assign(pvs->GetVisibilityNeighbor(), pvs->GetVisibilityNeighbor() + kPvsRecord);
    st.restore.push_back([idx, had, buf, wasDirty]() {
        CNtlWorldSectorPVS* p = FieldMgr()->m_pSectors[idx].m_pNtlWorldSectorPVS; if (!p) return;
        if (had) p->SetVisibilityNeighbor(const_cast<BYTE*>(buf.data())); else p->DestroyPVS(idx);
        if (!wasDirty) gPvsDirty.erase(idx);
        ++gEditGeneration;
    });
}

// Sectors of the selected area: explicit list, a radius around x,z, or the field at x,z (4 sectors).
static std::vector<int> PvsArea(const Value& cmd) {
    std::vector<int> out; CNtlWorldFieldManager* fm = FieldMgr(); Require(fm != nullptr, "no world");
    if (cmd.has("sectors")) { for (auto& s : cmd.at("sectors").array) out.push_back((int)s.number); }
    else if (cmd.has("radius")) out = SectorsInRadius((float)cmd.num("x", gPos.x), (float)cmd.num("z", gPos.z), (float)cmd.num("radius"));
    else { int f = FieldFromCmd(cmd); Require(f >= 0, "no field at position"); RwInt32 idx[4]; fm->GetFieldSectorIndices(f, idx); out.assign(idx, idx + 4); }
    int total = dGET_WORLD_PARAM()->WorldSectorNum * dGET_WORLD_PARAM()->WorldSectorNum;
    for (int s : out) { Require(s >= 0 && s < total, "invalid sector index"); Require(fm->m_pSectors[s].m_bLoaded != FALSE, "PVS is only rebuilt for loaded sectors (move the camera to the area)"); }
    Require(out.size() <= 64, "at most 64 sectors per PVS build");
    std::sort(out.begin(), out.end()); out.erase(std::unique(out.begin(), out.end()), out.end());
    return out;
}

static Value PvsSector(int idx, int layer) {
    Value v = Event("pvs"); v["sector"] = idx; CNtlWorldFieldManager* fm = FieldMgr();
    if (!fm || idx < 0) { v["enabled"] = false; return v; }
    CNtlWorldSectorPVS* p = fm->m_pSectors[idx].m_pNtlWorldSectorPVS; BYTE* buf = p ? p->GetVisibilityNeighbor() : nullptr;
    v["enabled"] = buf != nullptr; v["dirty"] = gPvsDirty.count(idx) > 0; v["layer"] = layer;
    if (!buf) return v;
    Value culled = Value::arr(); Value counts = Value::arr();
    for (int l = 0; l < dPVS_LAYER_CNT; ++l) { int c = 0; for (int i = 0; i < dPVS_TOT_CELL_CNT; ++i) c += buf[i + l * dPVS_TOT_CELL_CNT] == 0; counts.array.push_back(Value(c)); }
    const int cells = dPVS_SECTOR_EXTENT_CNT * 2 + 1; const float ss = (float)dGET_WORLD_PARAM()->WorldSectorSize;
    const auto& dp = fm->m_pSectors[idx].DatumPoint;
    for (int i = 0; i < cells; ++i) for (int j = 0; j < cells; ++j) {
        if (buf[i * cells + j + layer * dPVS_TOT_CELL_CNT]) continue;
        RwV3d q = { dp.x + (j - dPVS_SECTOR_EXTENT_CNT) * ss, 0, dp.z + (i - dPVS_SECTOR_EXTENT_CNT) * ss };
        culled.array.push_back(Value(fm->GetSectorIdx(q)));
    }
    std::string cellStr(dPVS_TOT_CELL_CNT, '0'); for (int i = 0; i < dPVS_TOT_CELL_CNT; ++i) cellStr[i] = buf[i + layer * dPVS_TOT_CELL_CNT] ? '1' : '0';
    v["cells"] = cellStr;
    v["culledPerLayer"] = counts; v["culled"] = culled;
    return v;
}

static bool PvsTick() {   // one sector per frame so the viewer keeps rendering
    if (!gPvsJob) return false;
    if (gPvsJob->next < gPvsJob->queue.size()) {
        int idx = gPvsJob->queue[gPvsJob->next++];
        auto t0 = std::chrono::steady_clock::now();
        SnapshotPvs(gPvsJob->step, idx);
        FieldMgr()->m_pSectors[idx].CreateSectorPVS(idx, gPvsJob->eps);
        gPvsJob->ms += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
        gPvsDirty.insert(idx); ++gEditGeneration;
        return true;
    }
    PropsHeightCache(FALSE);
    gUndo.push_back(std::move(gPvsJob->step)); if (gUndo.size() > 40) gUndo.pop_front();
    Value v = EditState("pvs-built"); Value secs = Value::arr(); for (int s : gPvsJob->queue) secs.array.push_back(Value(s));
    v["sectors"] = secs; v["ms"] = gPvsJob->ms; v["pvsDirty"] = (int)gPvsDirty.size(); v["eps"] = (double)gPvsJob->eps;
    if (gPvsJob->hasReply) v["id"] = gPvsJob->replyId;
    gPvsJob.reset(); Emit(v);
    return true;
}

// Height memo of the tool-mode engine build: unloaded-sector GetHeight reads each wfif once per build
// instead of once per sample (~57 s -> ~0.2 s per sector). WORLD_HOST_PROPS_NO_CORE_EXT builds against older cores.
static void PropsHeightCache(bool on) {
#ifndef WORLD_HOST_PROPS_NO_CORE_EXT
    CNtlWorldFieldManager::SetHeightFileCache(on ? TRUE : FALSE);
#else
    (void)on;
#endif
}

// Rewrites P\------- : records of gPvsDirty come from memory, every other record is copied byte for byte.
static Value WritePvsFile() {
    Value v = Value::obj(); CNtlWorldFieldManager* fm = FieldMgr();
    std::string path = std::string(dGET_WORLD_PARAM()->WorldProjectFolderName) + "\\-------";
    std::vector<BYTE> src; { FILE* f = fopen(path.c_str(), "rb"); if (f) { fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET); src.resize(n); if (n) fread(src.data(), 1, n, f); fclose(f); } }
    const int total = dGET_WORLD_PARAM()->WorldSectorNum * dGET_WORLD_PARAM()->WorldSectorNum;
    std::vector<BYTE> out; out.reserve(src.size() + gPvsDirty.size() * (kPvsRecord + 1));
    size_t at = 0; bool truncated = false; int written = 0;
    for (int i = 0; i < total; ++i) {
        size_t begin = at, end = at;   // original record [begin,end)
        if (at < src.size()) { end = at + 1 + (src[at] ? kPvsRecord : 0); if (end > src.size()) { truncated = true; end = src.size(); } at = end; }
        if (gPvsDirty.count(i)) {
            BYTE* buf = fm->m_pSectors[i].m_pNtlWorldSectorPVS ? fm->m_pSectors[i].m_pNtlWorldSectorPVS->GetVisibilityNeighbor() : nullptr;
            out.push_back(buf ? 1 : 0); if (buf) out.insert(out.end(), buf, buf + kPvsRecord); ++written;
        } else if (end > begin) out.insert(out.end(), src.begin() + begin, src.begin() + end);
        else out.push_back(0);   // file missing/short: sector without PVS
    }
    Require(!truncated, "existing ------- is truncated; refusing to rewrite it");
    if (at < src.size()) out.insert(out.end(), src.begin() + at, src.end());   // trailing bytes, kept verbatim
    std::string tmp = path + ".tmp";
    FILE* f = fopen(tmp.c_str(), "wb"); Require(f != nullptr, "cannot write ------- (temp)");
    size_t w = out.empty() ? 0 : fwrite(out.data(), 1, out.size(), f); fclose(f);
    Require(w == out.size(), "short write of -------");
    Require(MoveFileExA(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING) != FALSE, "replace -------");
    for (int s : gPvsDirty) gPvsEverWritten.insert(s);
    // Sidecar for the candidate audit: which sector records this workspace rewrote on purpose.
    std::string side = std::string(dGET_WORLD_PARAM()->CurWorkingFolderName) + "\\pvs-edits.json";
    if (FILE* sf = fopen(side.c_str(), "wb")) {
        Value j = Value::obj(); Value a = Value::arr(); for (int s : gPvsEverWritten) a.array.push_back(Value(s));
        j["file"] = "-------"; j["recordBytes"] = kPvsRecord + 1; j["sectorCount"] = total; j["sectors"] = a;
        std::string d = j.dump(); fwrite(d.data(), 1, d.size(), sf); fclose(sf);
    }
    v["file"] = path; v["sizeBefore"] = (double)src.size(); v["sizeAfter"] = (double)out.size(); v["sectorsWritten"] = written;
    Value a = Value::arr(); for (int s : gPvsDirty) a.array.push_back(Value(s)); v["sectors"] = a;
    gPvsDirty.clear();
    return v;
}

// PVS overlay: 13x13 sector grid around the current (or pinned) sector; green visible, red culled, yellow self.
static std::vector<RwIm3DVertex> gPvsVerts; static int gPvsVertsKey = -2, gPvsVertsGen = -1, gPvsVertsLayer = -1;
static void BuildPvsOverlay(int center) {
    gPvsVerts.clear(); CNtlWorldFieldManager* fm = FieldMgr(); if (!fm || center < 0) return;
    CNtlWorldSectorPVS* p = fm->m_pSectors[center].m_pNtlWorldSectorPVS; BYTE* buf = p ? p->GetVisibilityNeighbor() : nullptr;
    const int cells = dPVS_SECTOR_EXTENT_CNT * 2 + 1; const float ss = (float)dGET_WORLD_PARAM()->WorldSectorSize, inset = ss * 0.06f;
    const auto& dp = fm->m_pSectors[center].DatumPoint;
    float y = gPos.y + 1.f;
    for (int i = 0; i < cells; ++i) for (int j = 0; j < cells; ++j) {
        float cx = dp.x + (j - dPVS_SECTOR_EXTENT_CNT) * ss, cz = dp.z + (i - dPVS_SECTOR_EXTENT_CNT) * ss;
        RwV3d q = { cx, 0, cz }; int si = fm->GetSectorIdx(q); if (si < 0) continue;
        float yy = y; if (fm->m_pSectors[si].m_bLoaded) { RwV3d hq = q; if (fm->GetHeight(hq)) yy = hq.y + 2.f; }
        bool self = si == center, vis = !buf || buf[i * cells + j + gPvsLayer * dPVS_TOT_CELL_CNT] != 0;
        RwRGBA c = self ? RwRGBA{ 255, 220, 60, 40 } : !buf ? RwRGBA{ 150, 150, 150, 40 } : vis ? RwRGBA{ 60, 210, 100, 30 } : RwRGBA{ 235, 40, 40, 150 };
        float x0 = cx - ss * 0.5f + inset, x1 = cx + ss * 0.5f - inset, z0 = cz - ss * 0.5f + inset, z1 = cz + ss * 0.5f - inset;
        RwV3d t[6] = { {x0, yy, z0}, {x1, yy, z0}, {x1, yy, z1}, {x0, yy, z0}, {x1, yy, z1}, {x0, yy, z1} };
        for (auto& pt : t) { RwIm3DVertex v; RwIm3DVertexSetPos(&v, pt.x, pt.y, pt.z); RwIm3DVertexSetRGBA(&v, c.red, c.green, c.blue, c.alpha); gPvsVerts.push_back(v); }
    }
}

// ---- interactive tile brush ----------------------------------------------------------------------------------------
static bool PropsMouseDown(int x, int y) {
    if (gPropTool == PRT_NONE || gTool != TT_NONE) return false;
    RwV3d hit; gTileHover = gApp.world && FieldMgr()->PickTerrain(x, y, hit); if (gTileHover) gTilePos = hit;
    if (!gEditMode) return true;
    gTileDown = true; BeginStep("tiles");
    if (gTileHover) ApplyTiles(gTilePos.x, gTilePos.z, gTileRadius, gTileMaterial, gTileMask, gTileClear, gTileValue);
    return true;
}
static void PropsMouseMove(int x, int y) {
    if (gPropTool == PRT_NONE) return;
    RwV3d hit; gTileHover = gApp.world && FieldMgr()->PickTerrain(x, y, hit); if (gTileHover) gTilePos = hit;
    if (gTileDown && gTileHover && gEditMode) ApplyTiles(gTilePos.x, gTilePos.z, gTileRadius, gTileMaterial, gTileMask, gTileClear, gTileValue);
}
static bool PropsMouseUp() { if (gPropTool == PRT_NONE) return false; if (gTileDown) { gTileDown = false; EndStep(); Emit(EditState()); } return true; }

static void RenderPropsOverlay() {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return;
    if (gTileOverlay) {
        RwV3d c = gPropTool != PRT_NONE && gTileHover ? gTilePos : gPos;
        float dx = c.x - gTileVertsAt.x, dz = c.z - gTileVertsAt.z;
        if (dx * dx + dz * dz > 16.f || gTileVertsGen != gEditGeneration || gTileVertsMode != gTileOverlay || gTileVertsMask != gTileOverlayMask) {
            BuildTileOverlay(c); gTileVertsAt = c; gTileVertsGen = gEditGeneration; gTileVertsMode = gTileOverlay; gTileVertsMask = gTileOverlayMask;
        }
        RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);   // tiles under object meshes (plazas, bridges) stay visible
        RenderTris(gTileVerts);
        RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
    }
    if (gPvsOverlay) {
        RwV3d p = gPos; int center = gPvsPinned >= 0 ? gPvsPinned : fm->GetSectorIdx(p);
        if (center != gPvsVertsKey || gPvsVertsGen != gEditGeneration || gPvsVertsLayer != gPvsLayer) { BuildPvsOverlay(center); gPvsVertsKey = center; gPvsVertsGen = gEditGeneration; gPvsVertsLayer = gPvsLayer; }
        RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
        RenderTris(gPvsVerts);
        RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
    }
    if (gPropTool != PRT_NONE && gTool == TT_NONE && gTileHover) {   // brush cursor (the terrain tool wins when both are set)
        const int seg = 48; RwIm3DVertex verts[seg * 2]; RwRGBA c = gEditMode ? RwRGBA{ 255, 120, 220, 255 } : RwRGBA{ 160, 160, 160, 255 };
        RwV3d prev = { 0, 0, 0 };
        for (int i = 0; i <= seg; ++i) {
            float a = 2 * 3.14159265f * i / seg; RwV3d p = { gTilePos.x + cosf(a) * gTileRadius, gTilePos.y, gTilePos.z + sinf(a) * gTileRadius };
            RwV3d q = p; if (fm->GetHeight(q)) p.y = q.y; p.y += 0.3f;
            if (i > 0) { RwIm3DVertexSetPos(&verts[(i - 1) * 2], prev.x, prev.y, prev.z); RwIm3DVertexSetRGBA(&verts[(i - 1) * 2], c.red, c.green, c.blue, c.alpha);
                RwIm3DVertexSetPos(&verts[(i - 1) * 2 + 1], p.x, p.y, p.z); RwIm3DVertexSetRGBA(&verts[(i - 1) * 2 + 1], c.red, c.green, c.blue, c.alpha); }
            prev = p;
        }
        RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr); RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
        if (RwIm3DTransform(verts, seg * 2, nullptr, rwIM3D_ALLOPAQUE)) { RwIm3DRenderPrimitive(rwPRIMTYPELINELIST); RwIm3DEnd(); }
        RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
    }
}

// Screen-space PVS grid (13x13 sectors around the current/pinned one) drawn in the main camera pass: the whole grid
// spans 1664 m, beyond the camera's far plane, so the 3D overlay alone can only show the nearest cells.
static void RenderPropsOverlay2D() {
    if (!gPvsOverlay) return;
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return;
    RwV3d p = gPos; int center = gPvsPinned >= 0 ? gPvsPinned : fm->GetSectorIdx(p); if (center < 0) return;
    CNtlWorldSectorPVS* pv = fm->m_pSectors[center].m_pNtlWorldSectorPVS; BYTE* buf = pv ? pv->GetVisibilityNeighbor() : nullptr;
    const int cells = dPVS_SECTOR_EXTENT_CNT * 2 + 1, cs = 14, pad = 2, size = cells * cs;
    RwCamera* cam = CNtlPLGlobal::m_RwCamera ? CNtlPLGlobal::m_RwCamera : gApp.camera->GetCamera();
    float z = RwIm2DGetNearScreenZ(), rz = 1.f / RwCameraGetNearClipPlane(cam);
    float ox = (float)(gWidth - size - 12), oy = 12.f;
    std::vector<RwIm2DVertex> v; v.reserve((cells * cells + 1) * 6);
    auto quad = [&](float x0, float y0, float x1, float y1, RwRGBA c) {
        float xs[6] = { x0, x1, x1, x0, x1, x0 }, ys[6] = { y0, y0, y1, y0, y1, y1 };
        for (int k = 0; k < 6; ++k) { RwIm2DVertex q; RwIm2DVertexSetScreenX(&q, xs[k]); RwIm2DVertexSetScreenY(&q, ys[k]); RwIm2DVertexSetScreenZ(&q, z);
            RwIm2DVertexSetRecipCameraZ(&q, rz); RwIm2DVertexSetIntRGBA(&q, c.red, c.green, c.blue, c.alpha); v.push_back(q); }
    };
    quad(ox - 4, oy - 4, ox + size + 4, oy + size + 4, RwRGBA{ 10, 12, 18, 190 });
    // row i grows with world +z, column j with world +x; the top view shows +x to the left and +z up
    for (int i = 0; i < cells; ++i) for (int j = 0; j < cells; ++j) {
        bool self = i == dPVS_SECTOR_EXTENT_CNT && j == dPVS_SECTOR_EXTENT_CNT;
        bool vis = !buf || buf[i * cells + j + gPvsLayer * dPVS_TOT_CELL_CNT] != 0;
        RwRGBA c = self ? RwRGBA{ 255, 220, 60, 255 } : !buf ? RwRGBA{ 120, 120, 120, 220 } : vis ? RwRGBA{ 60, 200, 100, 230 } : RwRGBA{ 230, 50, 50, 240 };
        float x0 = ox + (cells - 1 - j) * cs, y0 = oy + (cells - 1 - i) * cs;
        quad(x0 + pad * 0.5f, y0 + pad * 0.5f, x0 + cs - pad * 0.5f, y0 + cs - pad * 0.5f, c);
    }
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEVERTEXALPHAENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATESRCBLEND, (void*)rwBLENDSRCALPHA);
    RwRenderStateSet(rwRENDERSTATEDESTBLEND, (void*)rwBLENDINVSRCALPHA);
    RwIm2DRenderPrimitive(rwPRIMTYPETRILIST, v.data(), (RwInt32)v.size());
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)TRUE);
    if (gApp.charset) {
        char line[96]; snprintf(line, sizeof(line), "PVS %d L%d %s%s", center, gPvsLayer, buf ? "" : "(none)", gPvsDirty.count(center) ? " *" : "");
        RtCharsetPrint(gApp.charset, line, (RwInt32)ox, (RwInt32)(oy + size + 8));
    }
}

// Called by SaveDirty (world-host-edit.h, gSaveHooks) after the dirty fields are written.
static void PropsSaveHook(Value& v) {
    if (!gPvsDirty.empty()) v["pvs"] = WritePvsFile();
    Value all = Value::arr(); for (int s : gPvsEverWritten) all.array.push_back(Value(s)); v["pvsSectors"] = all;
}
static const bool gPropsSaveHookRegistered = (gSaveHooks.push_back(PropsSaveHook), true);
static Value PropsEditState(Value v) { v["pvsDirty"] = (int)gPvsDirty.size(); v["pvsBuilding"] = gPvsJob != nullptr; return v; }

// ---- protocol ------------------------------------------------------------------------------------------------------
static DWORD ParseMask(const Value& cmd, const char* key, DWORD fallback) {
    if (!cmd.has(key)) return fallback;
    const Value& m = cmd.at(key);
    if (m.type == Value::Number) return (DWORD)(long long)m.number;
    if (m.type == Value::String) return (DWORD)strtoul(m.string.c_str(), nullptr, 0);
    throw std::runtime_error("mask: number or \"0x...\" string");
}

static bool PropsCommand(const std::string& c, const Value& cmd) {
    if (c.rfind("prop-", 0) != 0 && c.rfind("pvs-", 0) != 0) return false;
    CNtlWorldFieldManager* fm = FieldMgr();
    Require(fm != nullptr, "no world");
    if (c == "prop-get") { Reply(cmd, FieldPropReply(FieldFromCmd(cmd))); return true; }
    if (c == "prop-fields") {   // fields of the streaming ring (loaded = editable)
        Value v = Event("fields"); Value a = Value::arr(); std::set<int> seen;
        for (int i = 0; i < 36; ++i) { int f = fm->m_Fields6x6[1][i]; if (f < 0 || !seen.insert(f).second) continue;
            Value o = Value::obj(); o["field"] = f; o["loaded"] = fm->IsFieldLoaded(f) != FALSE; sNTL_FIELD_PROP* p = PropFields()[f].GetProp();
            o["name"] = p ? Cstr(p->_Name, 64) : std::string(); a.array.push_back(o); }
        v["fields"] = a; v["current"] = FieldAtXZ(gPos.x, gPos.z); Reply(cmd, v); return true;
    }
    if (c == "prop-set") {
        Require(gEditMode, "field props need --edit (workspace)");
        std::vector<int> fields;
        if (cmd.has("fields")) for (auto& f : cmd.at("fields").array) fields.push_back((int)f.number); else fields.push_back(FieldFromCmd(cmd));
        Require(!fields.empty() && fields.size() <= 64, "1..64 fields");
        for (int f : fields) Require(FieldReady(f), "field not loaded: props are only written to loaded fields (move the camera there)");
        BeginStep("field-props");
        Value done = Value::arr();
        for (int f : fields) {
            auto before = SnapshotFieldProp(f);
            sNTL_FIELD_PROP edit; edit = *before;
            std::vector<eNTL_FIELD_PROPID> ids = ApplyFieldPropJson(edit, cmd.at("props"));
            RwV3d center = FieldCenter(f);
            for (auto id : ids) Require(fm->SetAFieldProp(center, edit, id) != FALSE, "SetAFieldProp failed");
            gOpenStep->fields.insert(f);
            gOpenStep->restore.push_back([f, before, ids]() { RestoreFieldProp(f, before, ids); });
            MarkDirtyField(f); done.array.push_back(Value(f));
        }
        EndStep();
        if (cmd.flag("now", true)) fm->SetAnotherField(TRUE);
        Value v = EditState("ack"); v["fields"] = done; v["props"] = FieldPropReply(fields[0]).at("props"); Reply(cmd, v); return true;
    }
    if (c == "prop-tool") {
        std::string t = cmd.text("tool", "none"); gPropTool = t == "tile" ? PRT_TILE : PRT_NONE;
        if (gPropTool != PRT_NONE) gTool = TT_NONE;   // one brush at a time (terrain tool off)
        if (cmd.has("target")) gTileMaterial = cmd.text("target") == "material";
        gTileMask = ParseMask(cmd, "mask", gTileMask);
        if (cmd.has("op")) gTileClear = cmd.text("op") == "clear";
        if (cmd.has("value")) gTileValue = (int)std::max(0.0, std::min(255.0, cmd.num("value")));
        if (cmd.has("radius")) gTileRadius = (float)std::max(1.0, std::min(64.0, cmd.num("radius")));
        if (gPropTool != PRT_NONE && !gTileOverlay) gTileOverlay = gTileMaterial ? 2 : 1;
        Value v = EditState("ack"); v["tool"] = t; v["target"] = gTileMaterial ? "material" : "bit"; v["mask"] = (double)gTileMask; v["op"] = gTileClear ? "clear" : "set";
        v["value"] = gTileValue; v["radius"] = (double)gTileRadius; Reply(cmd, v); return true;
    }
    if (c == "prop-overlay") {
        std::string m = cmd.text("mode", "none"); gTileOverlay = m == "bit" ? 1 : m == "material" ? 2 : 0;
        gTileOverlayMask = ParseMask(cmd, "mask", gTileOverlayMask);
        if (cmd.has("radius")) gTileOverlayRadius = (float)std::max(8.0, std::min(256.0, cmd.num("radius")));
        Value v = Event("ack"); v["mode"] = m; v["mask"] = (double)gTileOverlayMask; Reply(cmd, v); return true;
    }
    if (c == "prop-tile-paint") {   // scripted stroke (CLI)
        Require(gEditMode, "tile props need --edit (workspace)");
        BeginStep("tiles");
        bool material = cmd.text("target", "bit") == "material";
        int n = ApplyTiles((float)cmd.num("x", gPos.x), (float)cmd.num("z", gPos.z), (float)cmd.num("radius", gTileRadius), material,
            ParseMask(cmd, "mask", gTileMask), cmd.text("op", "set") == "clear", (int)cmd.num("value", gTileValue));
        EndStep();
        Value v = EditState("ack"); v["tiles"] = n; Reply(cmd, v); return true;
    }
    if (c == "prop-tile-get") { Value v = TileInfo((float)cmd.num("x", gPos.x), (float)cmd.num("z", gPos.z)); Reply(cmd, v); return true; }
    if (c == "prop-tile-stats") { Reply(cmd, TileStats(FieldFromCmd(cmd))); return true; }
    if (c == "prop-gameprop-list") {
        Value v = Event("gameprops"); Value a = Value::arr(); int total = 0;
        // Every loaded sector (indoor sectors have no heightfield atomic, so SectorsInRadius would skip them); the
        // radius filters by the volume's own position.
        const float cx = (float)cmd.num("x", gPos.x), cz = (float)cmd.num("z", gPos.z), r = (float)cmd.num("radius", 256);
        const int sectorCount = dGET_WORLD_PARAM()->WorldSectorNum * dGET_WORLD_PARAM()->WorldSectorNum;
        for (int s = 0; s < sectorCount; ++s) {
            if (!fm->m_pSectors[s].m_bLoaded) continue;
            for (int i = 0; i < (int)fm->m_pSectors[s].m_vecNtlPLEntityGameProperty.size(); ++i) {
                auto* g = static_cast<CNtlPLGameProperty*>(fm->m_pSectors[s].m_vecNtlPLEntityGameProperty[i]);
                RwV3d gp = g->GetPosition(); if ((gp.x - cx) * (gp.x - cx) + (gp.z - cz) * (gp.z - cz) > r * r) continue;
                ++total; if (a.array.size() < 500) a.array.push_back(GamePropJson(s, i, g));
            }
        }
        v["volumes"] = a; v["total"] = total; v["worldType"] = dGET_WORLD_PARAM()->WorldType;
        v["note"] = "game property volumes are only loaded for indoor (RpWorld) sectors"; Reply(cmd, v); return true;
    }
    if (c == "prop-gameprop-set") {
        Require(gEditMode, "game property edits need --edit (workspace)");
        int s = (int)cmd.num("sector", -1), i = (int)cmd.num("index", -1);
        Require(s >= 0 && fm->m_pSectors[s].m_bLoaded && i >= 0 && i < (int)fm->m_pSectors[s].m_vecNtlPLEntityGameProperty.size(), "invalid sector/index");
        auto* g = static_cast<CNtlPLGameProperty*>(fm->m_pSectors[s].m_vecNtlPLEntityGameProperty[i]);
        RwUInt32 oldProp = g->GetGameProperty(); RwV3d oldPos = g->GetPosition(), oldSize = g->GetSize(); RwUInt32 oldShape = g->GetShapeType();
        BeginStep("gameprop");
        gOpenStep->fields.insert(FieldOfSector(s));
        gOpenStep->restore.push_back([s, i, oldProp, oldPos, oldSize, oldShape]() {
            CNtlWorldFieldManager* m = FieldMgr(); if (!m || !m->m_pSectors[s].m_bLoaded || i >= (int)m->m_pSectors[s].m_vecNtlPLEntityGameProperty.size()) return;
            auto* gg = static_cast<CNtlPLGameProperty*>(m->m_pSectors[s].m_vecNtlPLEntityGameProperty[i]);
            gg->SetGameProperty(oldProp); gg->SetPosition(&oldPos); gg->SetSize(&oldSize); gg->SetShapeType(oldShape);
        });
        if (cmd.has("property")) g->SetGameProperty((RwUInt32)ParseMask(cmd, "property", oldProp));
        if (cmd.has("pos")) { RwV3d p; ToVec(cmd.at("pos"), p); g->SetPosition(&p); }
        if (cmd.has("size")) { RwV3d p; ToVec(cmd.at("size"), p); g->SetSize(&p); }
        if (cmd.has("shape")) { int sh = (int)cmd.num("shape"); Require(sh == ENTL_PL_GAME_PROPERTY_SHAPE_CIRCLE || sh == ENTL_PL_GAME_PROPERTY_SHAPE_PLANE, "shape: 1 circle, 2 plane"); g->SetShapeType(sh); }
        EndStep(); MarkDirtySector(s);
        Value v = EditState("ack"); v["volume"] = GamePropJson(s, i, g); Reply(cmd, v); return true;
    }
    if (c == "prop-gameprop-selftest") { Reply(cmd, GamePropSelfTest((RwUInt32)ParseMask(cmd, "value", 0x00012345))); return true; }
    if (c == "pvs-build") {
        Require(gEditMode, "PVS builds need --edit (workspace)");
        Require(!gPvsJob, "a PVS build is already running");
        std::vector<int> area = PvsArea(cmd);
        PropsHeightCache(TRUE);
        gPvsJob.reset(new PvsJob); gPvsJob->queue = area; gPvsJob->eps = (float)cmd.num("eps", 10.0);
        if (cmd.flag("wait", false) && cmd.has("id")) { gPvsJob->hasReply = true; gPvsJob->replyId = cmd.at("id").type == Value::String ? cmd.at("id").string : cmd.at("id").dump(); }
        else { Value v = EditState("ack"); Value secs = Value::arr(); for (int s : area) secs.array.push_back(Value(s)); v["queued"] = secs; Reply(cmd, v); }
        return true;
    }
    if (c == "pvs-get") {
        int idx = cmd.has("sector") ? (int)cmd.num("sector") : [&] { RwV3d p = { (RwReal)cmd.num("x", gPos.x), 0, (RwReal)cmd.num("z", gPos.z) }; return fm->GetSectorIdx(p); }();
        Reply(cmd, PvsSector(idx, (int)std::max(0.0, std::min(5.0, cmd.num("layer", gPvsLayer))))); return true;
    }
    if (c == "pvs-overlay") {
        gPvsOverlay = cmd.flag("enabled", !gPvsOverlay);
        if (cmd.has("layer")) gPvsLayer = (int)std::max(0.0, std::min(5.0, cmd.num("layer")));
        if (cmd.has("sector")) gPvsPinned = (int)cmd.num("sector", -1);
        gPvsVertsKey = -2; Value v = Event("ack"); v["enabled"] = gPvsOverlay; v["layer"] = gPvsLayer; v["pinned"] = gPvsPinned; Reply(cmd, v); return true;
    }
    if (c == "pvs-state") { Reply(cmd, PropsEditState(EditState("pvs-state"))); return true; }
    Value v = Event("error"); v["message"] = "unknown command: " + c; Reply(cmd, v); return true;
}
