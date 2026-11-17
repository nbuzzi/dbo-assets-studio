#pragma once
// World Editor host · water tools. Included by world-host.cpp after world-host-edit.h / world-host-terrain.h.
//
// Water is one flat quad per sector (sSECTOR_WATER_ATTR in NtlWorldSector.h, serialized by
// CNtlWorldSectorInfo::SectorWaterSaveIntoFile, record 3 of every sector in wfif<N>). Everything here goes through the
// engine's own tool entry point CNtlWorldFieldManager::OnWaterProc(menu, sector, attr) (dNTL_WORLD_TOOL_MODE):
//   0 create (the engine places it at terrain + dWATER_DEFAULT_HEIGHT_FROM_TERRAIN), 1 update (keeps the depth map),
//   2 delete, 3 regenerate depth map (uniform alpha = attr alpha), 4 remove depth map.
// The water type (_IdxSequence) indexes script\waterproperty.xml (CNtlPLWaterProp, loaded by the engine).
//
// Commands (prefix water-, whitelisted for the UI):
//   water-types                         list of waterproperty.xml entries
//   water-get     {sector | x,z}        attributes of one sector's water (+ terrain heights of the sector)
//   water-apply   {op, sectors[] | x,z,radius, attrs...}   op: create|update|set|delete|depth|nodepth   (one undo step)
//   water-select  {sectors[] | x,z,radius, mode: replace|add|toggle|clear}
//   water-tool    {enabled}             clicks in the viewport select sectors (Shift = add/toggle)
//   water-terrain {sectors[]}           terrain height (min/avg/max) of the sectors, for "take height from terrain"
// Attributes: height, color [r,g,b] (0-255), alpha (0-255), blendSrc/blendDst (D3DBLEND), rotation (radians),
//             scale (UV wrap), depthSize, depthVar [a,b], type (index) or typeName.
#include "NtlPLWater.h"
#include "NtlPLWaterProp.h"

static bool gWaterTool = false;
static std::vector<int> gWaterSel;
static bool gWaterHover = false; static int gWaterHoverSector = -1;

static CNtlPLWater* WaterEntity() { CNtlWorldFieldManager* fm = FieldMgr(); return fm ? fm->GetNtlPLWater() : nullptr; }
static int WaterTypeCount() { CNtlPLWater* w = WaterEntity(); return (w && w->m_pWaterProp) ? (int)w->m_pWaterProp->m_vecWaterProp.size() : 0; }
static std::string WaterTypeName(int i) { return (i >= 0 && i < WaterTypeCount()) ? WaterEntity()->m_pWaterProp->m_vecWaterProp[i]._Name : std::string(); }
static bool SectorReady(int idx) {
    CNtlWorldFieldManager* fm = FieldMgr();
    return fm && idx >= 0 && idx < SectorCount() && fm->m_pSectors[idx].m_bLoaded && fm->m_pSectors[idx].m_pAtomic;
}

// Plain copy of the serialized attributes (sSECTOR_WATER_ATTR owns an atomic + texture and destroys them).
struct WaterParams {
    float height = 0; RwRGBA rgba = { 255, 255, 255, 160 }; DWORD src = D3DBLEND_SRCALPHA, dst = D3DBLEND_INVSRCALPHA;
    float rot = 0, scl = 1; int depthSize = 32; float var0 = 0, var1 = 255; int type = 0;
};
static WaterParams ParamsOf(const sSECTOR_WATER_ATTR& a) {
    WaterParams p; p.height = a._Height; p.rgba = a._RGBA; p.src = a._RenderstateSrc; p.dst = a._RenderstateDst; p.rot = a._RotTrans; p.scl = a._SclTrans;
    p.depthSize = a._DepthMapSize; p.var0 = a._DepthMapHeightVariation[0]; p.var1 = a._DepthMapHeightVariation[1]; p.type = a._IdxSequence; return p;
}
static void FillAttr(sSECTOR_WATER_ATTR& a, const WaterParams& p) {
    a._Height = p.height; a._RGBA = p.rgba; a._RenderstateSrc = p.src; a._RenderstateDst = p.dst; a._RotTrans = p.rot; a._SclTrans = p.scl;
    a._DepthMapSize = p.depthSize; a._DepthMapHeightVariation[0] = p.var0; a._DepthMapHeightVariation[1] = p.var1; a._IdxSequence = p.type;
}
static Value ParamsJson(const WaterParams& p) {
    Value v = Value::obj();
    v["height"] = (double)p.height;
    Value c = Value::arr(); c.array = { Value((int)p.rgba.red), Value((int)p.rgba.green), Value((int)p.rgba.blue) }; v["color"] = c;
    v["alpha"] = (int)p.rgba.alpha; v["blendSrc"] = (int)p.src; v["blendDst"] = (int)p.dst;
    v["rotation"] = (double)p.rot; v["scale"] = (double)p.scl; v["depthSize"] = p.depthSize;
    Value dv = Value::arr(); dv.array = { Value((double)p.var0), Value((double)p.var1) }; v["depthVar"] = dv;
    v["type"] = p.type; v["typeName"] = WaterTypeName(p.type);
    return v;
}
static int Clamp255(double x) { return (int)std::max(0.0, std::min(255.0, x)); }
// Overlay the attributes present in cmd on p.
static void ReadParams(const Value& cmd, WaterParams& p) {
    if (cmd.has("height")) p.height = (float)cmd.num("height", p.height);
    if (cmd.has("color") && cmd.at("color").array.size() >= 3) {
        const auto& c = cmd.at("color").array;
        p.rgba.red = (RwUInt8)Clamp255(c[0].number); p.rgba.green = (RwUInt8)Clamp255(c[1].number); p.rgba.blue = (RwUInt8)Clamp255(c[2].number);
    }
    if (cmd.has("alpha")) p.rgba.alpha = (RwUInt8)Clamp255(cmd.num("alpha", p.rgba.alpha));
    if (cmd.has("blendSrc")) p.src = (DWORD)std::max(1.0, std::min(17.0, cmd.num("blendSrc", p.src)));
    if (cmd.has("blendDst")) p.dst = (DWORD)std::max(1.0, std::min(17.0, cmd.num("blendDst", p.dst)));
    if (cmd.has("rotation")) p.rot = (float)cmd.num("rotation", p.rot);
    if (cmd.has("scale")) p.scl = (float)std::max(0.01, std::min(64.0, cmd.num("scale", p.scl)));
    if (cmd.has("depthSize")) { int s = (int)cmd.num("depthSize", p.depthSize); int pw = 8; while (pw < s && pw < 256) pw <<= 1; p.depthSize = pw; }
    if (cmd.has("depthVar") && cmd.at("depthVar").array.size() >= 2) { p.var0 = (float)cmd.at("depthVar").array[0].number; p.var1 = (float)cmd.at("depthVar").array[1].number; }
    if (cmd.has("type")) p.type = (int)cmd.num("type", p.type);
    if (cmd.has("typeName")) { std::string n = cmd.text("typeName"); for (int i = 0; i < WaterTypeCount(); ++i) if (_stricmp(WaterTypeName(i).c_str(), n.c_str()) == 0) p.type = i; }
    Require(WaterTypeCount() == 0 || (p.type >= 0 && p.type < WaterTypeCount()), "water type out of range (script\\waterproperty.xml)");
}

// ---- terrain helpers ----------------------------------------------------------------------------------------------
struct TerrainSpan { float minY = 0, avgY = 0, maxY = 0, centerY = 0; bool ok = false; };
static TerrainSpan SectorTerrain(int idx) {
    TerrainSpan t; if (!SectorReady(idx)) return t;
    CNtlWorldFieldManager* fm = FieldMgr(); RpGeometry* g = fm->m_pSectors[idx].m_pAtomic->geometry;
    RwV3d* v = RpMorphTargetGetVertices(g->morphTarget); int n = RpGeometryGetNumVertices(g); if (n <= 0) return t;
    t.minY = 1e30f; t.maxY = -1e30f; double sum = 0;
    for (int i = 0; i < n; ++i) { t.minY = std::min(t.minY, v[i].y); t.maxY = std::max(t.maxY, v[i].y); sum += v[i].y; }
    t.avgY = (float)(sum / n);
    RwV3d c = { fm->m_pSectors[idx].DatumPoint.x, 0, fm->m_pSectors[idx].DatumPoint.z }; t.centerY = fm->GetWorldSectorHeight(c);
    t.ok = true; return t;
}

// ---- undo ---------------------------------------------------------------------------------------------------------
struct WaterSnap { bool had = false; WaterParams p; bool hadDepth = false; int depthW = 0; std::vector<RwUInt8> depth; };
static WaterSnap SnapWater(int idx) {
    WaterSnap s; sSECTOR_WATER_ATTR* w = FieldMgr()->m_pSectors[idx].m_pWater; if (!w) return s;
    s.had = true; s.p = ParamsOf(*w);
    if (w->_pDepthMap && w->_pDepthMap->raster) {
        RwRaster* r = w->_pDepthMap->raster; int W = RwRasterGetWidth(r), H = RwRasterGetHeight(r);
        DWORD* bits = (DWORD*)RwRasterLock(r, 0, rwRASTERLOCKREAD);
        if (bits && W == H) { s.hadDepth = true; s.depthW = W; s.depth.resize(W * H); for (int i = 0; i < W * H; ++i) s.depth[i] = (RwUInt8)(bits[i] >> 24); }
        if (bits) RwRasterUnlock(r);
    }
    return s;
}
static void RestoreWater(int idx, const WaterSnap& s) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm || !SectorReady(idx)) return;
    CNtlWorldSector& sec = fm->m_pSectors[idx];
    if (!s.had) { if (sec.m_pWater) { sSECTOR_WATER_ATTR dummy; fm->OnWaterProc(2, idx, dummy); } return; }
    sSECTOR_WATER_ATTR a; FillAttr(a, s.p);
    WaterEntity()->OnCreate(&sec, a);   // replaces (and frees) whatever water the sector has now, depth map included
    if (s.hadDepth && sec.m_pWater) {
        RwTexture* t = CNtlPLResourceManager::GetInstance()->CreateTexture("DepthMap", s.depthW, s.depthW, 32, rwRASTERFORMAT8888);
        if (t) {
            RwTextureSetAddressing(t, rwTEXTUREADDRESSCLAMP); RwTextureSetFilterMode(t, rwFILTERLINEAR);
            std::vector<RwUInt8> copy = s.depth; CNtlPLResourceManager::GetInstance()->LoadAlphaBits(t, copy.data());
            sec.m_pWater->_pDepthMap = t;
        }
    }
}
static void SnapshotWater(int idx) {
    if (!gOpenStep || !gOpenStep->snapshots.insert((long long)idx * 16 + 8).second) return;
    WaterSnap s = SnapWater(idx);
    gOpenStep->fields.insert(FieldOfSector(idx));
    gOpenStep->restore.push_back([idx, s]() { RestoreWater(idx, s); });
}

// ---- operations ---------------------------------------------------------------------------------------------------
enum WaterOp { WO_CREATE, WO_UPDATE, WO_SET, WO_DELETE, WO_DEPTH, WO_NODEPTH };
static WaterOp ParseWaterOp(const std::string& s) {
    if (s == "create") return WO_CREATE; if (s == "update") return WO_UPDATE; if (s == "set") return WO_SET;
    if (s == "delete") return WO_DELETE; if (s == "depth") return WO_DEPTH; if (s == "nodepth") return WO_NODEPTH;
    throw std::runtime_error("water op: create|update|set|delete|depth|nodepth");
}
// Returns true when the sector changed.
static bool ApplyWater(int idx, WaterOp op, const Value& cmd) {
    CNtlWorldFieldManager* fm = FieldMgr(); CNtlWorldSector& sec = fm->m_pSectors[idx];
    bool has = sec.m_pWater != nullptr;
    if ((op == WO_UPDATE || op == WO_DELETE || op == WO_DEPTH || op == WO_NODEPTH) && !has) return false;
    if (op == WO_NODEPTH && !sec.m_pWater->_pDepthMap) return false;
    SnapshotWater(idx);
    sSECTOR_WATER_ATTR a;
    switch (op) {
    case WO_DELETE: fm->OnWaterProc(2, idx, a); break;
    case WO_NODEPTH: fm->OnWaterProc(4, idx, a); break;
    case WO_DEPTH: { WaterParams p = ParamsOf(*sec.m_pWater); ReadParams(cmd, p); FillAttr(a, p); fm->OnWaterProc(3, idx, a); break; }
    case WO_CREATE: case WO_UPDATE: case WO_SET: {
        bool create = op == WO_CREATE || (op == WO_SET && !has);
        WaterParams p = has ? ParamsOf(*sec.m_pWater) : WaterParams();   // re-create keeps the old look unless overridden
        ReadParams(cmd, p);
        FillAttr(a, p);
        if (create) {
            fm->OnWaterProc(0, idx, a);   // engine default height: terrain at the sector centre + 10 m
            if (cmd.has("height") && sec.m_pWater) { p.height = (float)cmd.num("height", p.height); sSECTOR_WATER_ATTR b; FillAttr(b, p); fm->OnWaterProc(1, idx, b); }
        } else fm->OnWaterProc(1, idx, a);
        break; }
    }
    MarkDirtySector(idx);
    return true;
}

static std::vector<int> WaterTargets(const Value& cmd) {
    std::vector<int> out;
    if (cmd.has("sectors")) { for (const auto& s : cmd.at("sectors").array) out.push_back((int)s.number); }
    else if (cmd.has("sector")) out.push_back((int)cmd.num("sector", -1));
    else if (cmd.has("x") && cmd.has("z") && cmd.num("radius", 0) > 0) out = SectorsInRadius((float)cmd.num("x", 0), (float)cmd.num("z", 0), (float)cmd.num("radius", 0));
    else if (cmd.has("x") && cmd.has("z")) { RwV3d p = { (RwReal)cmd.num("x", 0), 0, (RwReal)cmd.num("z", 0) }; out.push_back(FieldMgr()->GetSectorIdx(p)); }
    else if (cmd.flag("selection", true)) out = gWaterSel;
    std::sort(out.begin(), out.end()); out.erase(std::unique(out.begin(), out.end()), out.end());
    return out;
}

static Value WaterInfo(int idx) {
    Value v = Value::obj(); v["sector"] = idx; CNtlWorldFieldManager* fm = FieldMgr();
    if (!fm || idx < 0 || idx >= SectorCount()) { v["valid"] = false; return v; }
    CNtlWorldSector& s = fm->m_pSectors[idx];
    v["valid"] = true; v["loaded"] = (bool)(s.m_bLoaded != FALSE); v["field"] = FieldOfSector(idx);
    Value c = Value::arr(); c.array = { Value((double)s.DatumPoint.x), Value((double)s.DatumPoint.z) }; v["center"] = c;
    v["water"] = s.m_pWater != nullptr;
    if (s.m_pWater) { v["attr"] = ParamsJson(ParamsOf(*s.m_pWater)); v["depthMap"] = s.m_pWater->_pDepthMap != nullptr; }
    TerrainSpan t = SectorTerrain(idx);
    if (t.ok) { Value tj = Value::obj(); tj["min"] = (double)t.minY; tj["avg"] = (double)t.avgY; tj["max"] = (double)t.maxY; tj["center"] = (double)t.centerY; v["terrain"] = tj; }
    return v;
}

// ---- selection / mouse ------------------------------------------------------------------------------------------
static void WaterSelect(const std::vector<int>& ids, const std::string& mode) {
    if (mode == "clear") { gWaterSel.clear(); return; }
    if (mode == "replace") gWaterSel.clear();
    for (int id : ids) {
        if (id < 0) continue;
        auto it = std::find(gWaterSel.begin(), gWaterSel.end(), id);
        if (mode == "toggle" && it != gWaterSel.end()) gWaterSel.erase(it);
        else if (it == gWaterSel.end()) gWaterSel.push_back(id);
    }
}
static int SectorAtScreen(int x, int y) {
    CNtlWorldFieldManager* fm = FieldMgr(); RwV3d hit; if (!fm || !fm->PickTerrain(x, y, hit)) return -1;
    return fm->GetSectorIdx(hit);
}
// Called from WndProc: true when the click was consumed by the water tool.
static bool WaterClick(int x, int y, bool shift) {
    if (!gWaterTool || gTool != TT_NONE) return false;
    int idx = SectorAtScreen(x, y); if (idx < 0) return true;
    WaterSelect({ idx }, shift ? "toggle" : "replace");
    Value v = Event("water-pick"); v["sector"] = idx; v["shift"] = shift; Emit(v);
    return true;
}
static void WaterHover(int x, int y) { if (!gWaterTool || gTool != TT_NONE) { gWaterHover = false; return; } gWaterHoverSector = SectorAtScreen(x, y); gWaterHover = gWaterHoverSector >= 0; }

static void WaterStats(Value& v) {
    Value w = Value::obj(); w["tool"] = gWaterTool;
    Value sel = Value::arr(); for (int i : gWaterSel) sel.array.push_back(Value(i)); w["selected"] = sel;
    w["hover"] = gWaterHover ? gWaterHoverSector : -1;
    v["water"] = w;
}

// Sector outlines (selection cyan, hover white), drawn at the water surface or the terrain.
static void RenderWaterGizmos() {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return;
    if (gWaterSel.empty() && !(gWaterTool && gWaterHover)) return;
    static std::vector<RwIm3DVertex> verts; verts.clear();
    const float S = (float)dGET_WORLD_PARAM()->WorldSectorSize, step = S / 16.f;
    auto outline = [&](int idx, RwRGBA c) {
        if (!SectorReady(idx)) return;
        CNtlWorldSector& s = fm->m_pSectors[idx];
        float x0 = s.DatumPoint.x - S * 0.5f, z0 = s.DatumPoint.z - S * 0.5f;
        auto y = [&](float x, float z) -> float {
            if (s.m_pWater) return s.m_pWater->_Height + 0.2f;
            RwV3d q = { x, 0, z }; return (fm->GetHeight(q) ? q.y : s.DatumPoint.y) + 0.4f;
        };
        auto seg = [&](float ax, float az, float bx, float bz) {
            RwIm3DVertex a, b;
            RwIm3DVertexSetPos(&a, ax, y(ax, az), az); RwIm3DVertexSetRGBA(&a, c.red, c.green, c.blue, c.alpha);
            RwIm3DVertexSetPos(&b, bx, y(bx, bz), bz); RwIm3DVertexSetRGBA(&b, c.red, c.green, c.blue, c.alpha);
            verts.push_back(a); verts.push_back(b);
        };
        for (float t = 0; t < S - 0.01f; t += step) {
            seg(x0 + t, z0, x0 + t + step, z0); seg(x0 + t, z0 + S, x0 + t + step, z0 + S);
            seg(x0, z0 + t, x0, z0 + t + step); seg(x0 + S, z0 + t, x0 + S, z0 + t + step);
        }
    };
    for (int idx : gWaterSel) outline(idx, RwRGBA{ 60, 230, 255, 255 });
    if (gWaterTool && gWaterHover && std::find(gWaterSel.begin(), gWaterSel.end(), gWaterHoverSector) == gWaterSel.end()) outline(gWaterHoverSector, RwRGBA{ 255, 255, 255, 200 });
    if (verts.empty()) return;
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEFOGENABLE, (void*)FALSE);
    for (size_t i = 0; i < verts.size(); i += 2000) {
        RwInt32 n = (RwInt32)std::min<size_t>(2000, verts.size() - i);
        if (RwIm3DTransform(&verts[i], n, nullptr, rwIM3D_ALLOPAQUE)) { RwIm3DRenderPrimitive(rwPRIMTYPELINELIST); RwIm3DEnd(); }
    }
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
}

// ---- protocol ------------------------------------------------------------------------------------------------------
static bool WaterCommand(const std::string& c, const Value& cmd) {
    if (c.rfind("water-", 0) != 0) return false;
    if (c == "water-types") {
        Value v = Event("water-types"); Value arr = Value::arr(); CNtlPLWater* w = WaterEntity();
        for (int i = 0; i < WaterTypeCount(); ++i) {
            const auto& p = w->m_pWaterProp->m_vecWaterProp[i];
            Value t = Value::obj(); t["index"] = i; t["name"] = p._Name; t["fps"] = (double)p._FPS; t["frames"] = p._NumFrame; t["specular"] = (bool)(p._Specular != FALSE);
            arr.array.push_back(t);
        }
        v["types"] = arr; Reply(cmd, v); return true;
    }
    if (c == "water-get") {
        Value v = Event("water"); Value arr = Value::arr();
        for (int idx : WaterTargets(cmd)) arr.array.push_back(WaterInfo(idx));
        v["sectors"] = arr; Reply(cmd, v); return true;
    }
    if (c == "water-terrain") {
        Value v = Event("water-terrain"); auto ids = WaterTargets(cmd); float mn = 1e30f, mx = -1e30f; double sum = 0; int n = 0;
        for (int idx : ids) { TerrainSpan t = SectorTerrain(idx); if (!t.ok) continue; mn = std::min(mn, t.minY); mx = std::max(mx, t.maxY); sum += t.avgY; ++n; }
        v["count"] = n; if (n) { v["min"] = (double)mn; v["max"] = (double)mx; v["avg"] = sum / n; }
        Reply(cmd, v); return true;
    }
    if (c == "water-apply") {
        Require(gEditMode, "water edits need --edit (workspace)");
        WaterOp op = ParseWaterOp(cmd.text("op", "set"));
        auto ids = WaterTargets(cmd); Require(!ids.empty(), "water-apply: no sectors (sectors[], sector, x/z[/radius] or a selection)");
        BeginStep("water-" + cmd.text("op", "set"));
        Value changed = Value::arr(), skipped = Value::arr();
        try {
            for (int idx : ids) {
                if (!SectorReady(idx)) { skipped.array.push_back(Value(idx)); continue; }
                if (ApplyWater(idx, op, cmd)) changed.array.push_back(Value(idx)); else skipped.array.push_back(Value(idx));
            }
        } catch (...) { EndStep(); throw; }
        EndStep();
        if (changed.array.empty() && !gUndo.empty() && gUndo.back().restore.empty()) gUndo.pop_back();   // nothing to undo
        Value v = EditState("ack"); v["changed"] = changed; v["skipped"] = skipped; v["op"] = cmd.text("op", "set");
        Reply(cmd, v); return true;
    }
    if (c == "water-select") {
        std::string mode = cmd.text("mode", "replace");
        std::vector<int> ids; if (mode != "clear" && (cmd.has("sectors") || cmd.has("sector") || cmd.has("x"))) ids = WaterTargets(cmd);
        WaterSelect(ids, mode);
        Value v = Event("ack"); WaterStats(v); Reply(cmd, v); return true;
    }
    if (c == "water-tool") {
        gWaterTool = cmd.flag("enabled", !gWaterTool);
        if (gWaterTool) gTool = TT_NONE;   // one viewport tool at a time
        gWaterHover = false;
        Value v = Event("ack"); WaterStats(v); Reply(cmd, v); return true;
    }
    Value v = Event("error"); v["message"] = "unknown water command: " + c; Reply(cmd, v); return true;
}
