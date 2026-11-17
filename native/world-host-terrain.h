#pragma once
// World Editor host · terrain tools (height brush + texture layer paint). Included by world-host.cpp after
// world-host-edit.h. The engine has no brush logic (it lived in the Saber tool); this applies the same primitive
// the engine uses in CNtlWorldFieldManager::SetWorldSectorHeightInRectangle (lock vertices -> edit -> unlock ->
// bounding sphere -> OnSetSlopeLighting) and edits the per-sector alpha maps (1 texel per tile) that
// SectorMaterialSaveIntoFile serializes.

enum TerrainTool { TT_NONE, TT_HEIGHT, TT_PAINT };
enum HeightMode { HM_RAISE, HM_LOWER, HM_SMOOTH, HM_FLATTEN };
static TerrainTool gTool = TT_NONE;
static HeightMode gHeightMode = HM_RAISE;
static float gBrushRadius = 12.f, gBrushStrength = 1.5f, gFlattenTarget = 0.f;   // strength: m/s for height, alpha/s (0..1) for paint
static int gPaintLayer = 0; static bool gPaintErase = false; static std::string gPaintTexture;
static bool gBrushDown = false, gBrushHover = false; static RwV3d gBrushPos = { 0, 0, 0 };
static bool gFlattenCaptured = false;
// Slope lighting recomputes vertex colours from per-sector SL params (swap\sl, often missing in the paks) and
// darkens edited ground; off by default so edits keep the baked prelights.
static bool gSlopeLighting = false;

static float Falloff(float d, float r) { if (d >= r) return 0.f; float t = d / r; return 0.5f * (1.f + cosf(3.14159265f * t)); }

// ---- height --------------------------------------------------------------------------------------------------------
static void SnapshotSectorHeights(int idx) {
    if (!gOpenStep) return;
    CNtlWorldFieldManager* fm = FieldMgr(); RpGeometry* g = fm->m_pSectors[idx].m_pAtomic->geometry;
    RwV3d* verts = RpMorphTargetGetVertices(g->morphTarget); int n = RpGeometryGetNumVertices(g);
    // one snapshot per sector per step
    if (!gOpenStep->snapshots.insert((long long)idx * 16).second) return;
    std::vector<float> ys(n); for (int i = 0; i < n; ++i) ys[i] = verts[i].y;
    gOpenStep->fields.insert(FieldOfSector(idx));
    gOpenStep->restore.push_back([idx, ys]() {
        CNtlWorldFieldManager* fm2 = FieldMgr(); if (!fm2 || !fm2->m_pSectors[idx].m_bLoaded || !fm2->m_pSectors[idx].m_pAtomic) return;
        RpGeometry* g2 = fm2->m_pSectors[idx].m_pAtomic->geometry;
        RpGeometryLock(g2, rpGEOMETRYLOCKVERTICES);
        RwV3d* v2 = RpMorphTargetGetVertices(g2->morphTarget); int n2 = RpGeometryGetNumVertices(g2);
        for (int i = 0; i < n2 && i < (int)ys.size(); ++i) v2[i].y = ys[i];
        RpGeometryUnlock(g2);
        RwSphere bs; RpMorphTargetCalcBoundingSphere(g2->morphTarget, &bs); RpMorphTargetSetBoundingSphere(g2->morphTarget, &bs);
        std::vector<sTARGET_VERT_SL_INFO> sl; for (int i = 0; i < n2; ++i) { sTARGET_VERT_SL_INFO t; t.pNtlWorldSector = &fm2->m_pSectors[idx]; t.IdxVert = i; sl.push_back(t); }
        if (gSlopeLighting) fm2->OnSetSlopeLighting(sl);
    });
}

static int ApplyHeight(HeightMode mode, float cx, float cz, float radius, float amount, float target) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return 0;
    std::vector<sTARGET_VERT_SL_INFO> sl; int touched = 0;
    for (int idx : SectorsInRadius(cx, cz, radius)) {
        RpGeometry* g = fm->m_pSectors[idx].m_pAtomic->geometry;
        RwV3d* v = RpMorphTargetGetVertices(g->morphTarget); int n = RpGeometryGetNumVertices(g);
        int cross = (int)lroundf(sqrtf((float)n)); if (cross * cross != n) continue;
        bool any = false;
        for (int i = 0; i < n && !any; ++i) { float dx = v[i].x - cx, dz = v[i].z - cz; any = dx * dx + dz * dz < radius * radius; }
        if (!any) continue;
        SnapshotSectorHeights(idx);
        std::vector<float> src; if (mode == HM_SMOOTH) { src.resize(n); for (int i = 0; i < n; ++i) src[i] = v[i].y; }
        RpGeometryLock(g, rpGEOMETRYLOCKVERTICES);
        for (int i = 0; i < n; ++i) {
            float dx = v[i].x - cx, dz = v[i].z - cz, d = sqrtf(dx * dx + dz * dz);
            float w = Falloff(d, radius); if (w <= 0) continue;
            switch (mode) {
            case HM_RAISE: v[i].y += amount * w; break;
            case HM_LOWER: v[i].y -= amount * w; break;
            case HM_FLATTEN: v[i].y += (target - v[i].y) * std::min(1.f, w * amount * 0.25f); break;
            case HM_SMOOTH: {
                int r = i / cross, c = i % cross; float sum = 0; int cnt = 0;
                for (int dr = -1; dr <= 1; ++dr) for (int dc = -1; dc <= 1; ++dc) {
                    int rr = r + dr, cc = c + dc; if (rr < 0 || cc < 0 || rr >= cross || cc >= cross) continue;
                    sum += src[rr * cross + cc]; ++cnt;
                }
                v[i].y += (sum / cnt - v[i].y) * std::min(1.f, w * amount * 0.25f);
                break; }
            }
            sTARGET_VERT_SL_INFO t; t.pNtlWorldSector = &fm->m_pSectors[idx]; t.IdxVert = i; sl.push_back(t);
        }
        RpGeometryUnlock(g);
        RwSphere bs; RpMorphTargetCalcBoundingSphere(g->morphTarget, &bs); RpMorphTargetSetBoundingSphere(g->morphTarget, &bs);
        MarkDirtySector(idx); ++touched;
    }
    if (gSlopeLighting && !sl.empty()) fm->OnSetSlopeLighting(sl);
    return touched;
}

// ---- texture layers ---------------------------------------------------------------------------------------------
static bool AssignLayer(CNtlWorldSector& s, int layer, const std::string& texture) {
    if (s.m_pTextureSplat[layer] && s.m_pTextureAlpha[layer]) return true;
    if (texture.empty()) return false;
    RwTexture* splat = CNtlPLResourceManager::GetInstance()->LoadTexture(texture.c_str(), dGET_WORLD_PARAM()->CurDefaultTexPath);
    if (!splat) return false;
    int tiles = dGET_WORLD_PARAM()->WorldSectorTileNum;
    RwTexture* alpha = CNtlPLResourceManager::GetInstance()->CreateTexture(texture.c_str(), tiles, tiles, 32, rwRASTERFORMAT8888);
    if (!alpha) { CNtlPLResourceManager::GetInstance()->UnLoadTexture(splat); return false; }
    RwTextureSetAddressing(alpha, rwTEXTUREADDRESSCLAMP); RwTextureSetFilterMode(alpha, rwFILTERLINEAR);
    std::vector<RwUInt8> zero(tiles * tiles, 0); CNtlPLResourceManager::GetInstance()->LoadAlphaBits(alpha, zero.data());
    if (s.m_pTextureSplat[layer]) CNtlPLResourceManager::GetInstance()->UnLoadTexture(s.m_pTextureSplat[layer]);
    s.m_pTextureSplat[layer] = splat; s.m_pTextureAlpha[layer] = alpha;
    s.m_TextureMat[layer][0] = s.m_TextureMatBase[0]; s.m_TextureMat[layer][1] = s.m_TextureMatBase[1];
    s.m_TextureMatAng[layer] = s.m_TextureMatAngBase; s.m_TextureMatAdd[layer] = s.m_TextureMatAddBase;
    return true;
}

static void SnapshotSectorAlpha(int idx, int layer) {
    if (!gOpenStep) return;
    if (!gOpenStep->snapshots.insert((long long)idx * 16 + 1 + layer).second) return;
    CNtlWorldSector& s = FieldMgr()->m_pSectors[idx];
    int tiles = dGET_WORLD_PARAM()->WorldSectorTileNum;
    std::vector<DWORD> px(tiles * tiles, 0); bool had = s.m_pTextureAlpha[layer] != nullptr;
    if (had) { DWORD* bits = (DWORD*)RwRasterLock(s.m_pTextureAlpha[layer]->raster, 0, rwRASTERLOCKREAD); if (bits) { memcpy(px.data(), bits, px.size() * 4); RwRasterUnlock(s.m_pTextureAlpha[layer]->raster); } }
    gOpenStep->fields.insert(FieldOfSector(idx));
    gOpenStep->restore.push_back([idx, layer, px, had]() {
        CNtlWorldFieldManager* fm = FieldMgr(); if (!fm || !fm->m_pSectors[idx].m_bLoaded) return;
        CNtlWorldSector& s2 = fm->m_pSectors[idx];
        if (!had) {   // layer was created by the stroke: remove it again
            if (s2.m_pTextureAlpha[layer]) { CNtlPLResourceManager::GetInstance()->UnLoadTexture(s2.m_pTextureAlpha[layer]); s2.m_pTextureAlpha[layer] = nullptr; }
            if (s2.m_pTextureSplat[layer]) { CNtlPLResourceManager::GetInstance()->UnLoadTexture(s2.m_pTextureSplat[layer]); s2.m_pTextureSplat[layer] = nullptr; }
            return;
        }
        if (!s2.m_pTextureAlpha[layer]) return;
        DWORD* bits = (DWORD*)RwRasterLock(s2.m_pTextureAlpha[layer]->raster, 0, rwRASTERLOCKWRITE);
        if (bits) { memcpy(bits, px.data(), px.size() * 4); RwRasterUnlock(s2.m_pTextureAlpha[layer]->raster); }
    });
}

// Alpha texel (col,row) covers the tile between vertices (row,col) and (row+1,col+1) of the sector grid.
static int ApplyPaint(int layer, float cx, float cz, float radius, float amount, bool erase, const std::string& texture) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm || layer < 0 || layer >= dNTL_SPLAT_LAYER_NUM) return 0;
    int tiles = dGET_WORLD_PARAM()->WorldSectorTileNum, touched = 0;
    for (int idx : SectorsInRadius(cx, cz, radius)) {
        CNtlWorldSector& s = fm->m_pSectors[idx];
        RpGeometry* g = s.m_pAtomic->geometry; RwV3d* v = RpMorphTargetGetVertices(g->morphTarget); int n = RpGeometryGetNumVertices(g);
        int cross = tiles + 1; if (n != cross * cross) continue;
        bool inside = false;
        for (int i = 0; i < n && !inside; ++i) { float dx = v[i].x - cx, dz = v[i].z - cz; inside = dx * dx + dz * dz < radius * radius; }
        if (!inside) continue;
        SnapshotSectorAlpha(idx, layer);
        if (!s.m_pTextureAlpha[layer]) { if (erase || !AssignLayer(s, layer, texture)) continue; }
        DWORD* bits = (DWORD*)RwRasterLock(s.m_pTextureAlpha[layer]->raster, 0, rwRASTERLOCKREADWRITE);
        if (!bits) continue;
        int w = RwRasterGetWidth(s.m_pTextureAlpha[layer]->raster), h = RwRasterGetHeight(s.m_pTextureAlpha[layer]->raster);
        // Texel <-> ground mapping exactly as the renderer samples it: vertex UV set 0 transformed by
        // m_TextureMat[layer][0] (D3DTS_TEXTURE0, 2D homogeneous: translation in row 2). Each tile's centre
        // (average of its 4 corners) gives the texel it shows.
        RwTexCoords* uv = RpGeometryGetVertexTexCoords(g, rwTEXTURECOORDINATEINDEX0);
        const D3DXMATRIX& M = s.m_TextureMat[layer][0];
        auto texel = [&](int vi, float& tu, float& tv) {
            float u = uv ? uv[vi].u : 0.f, vv = uv ? uv[vi].v : 0.f;
            tu = u * M._11 + vv * M._21 + M._31; tv = u * M._12 + vv * M._22 + M._32;
        };
        std::vector<char> done(w * h, 0);
        for (int row = 0; row < tiles; ++row)
            for (int col = 0; col < tiles; ++col) {
                int i00 = row * cross + col, i11 = (row + 1) * cross + col + 1, i01 = row * cross + col + 1, i10 = (row + 1) * cross + col;
                float cxw = (v[i00].x + v[i11].x + v[i01].x + v[i10].x) * 0.25f, czw = (v[i00].z + v[i11].z + v[i01].z + v[i10].z) * 0.25f;
                float d = sqrtf((cxw - cx) * (cxw - cx) + (czw - cz) * (czw - cz));
                float wgt = Falloff(d, radius); if (wgt <= 0) continue;
                float u0, v0, u1, v1, u2, v2, u3, v3; texel(i00, u0, v0); texel(i11, u1, v1); texel(i01, u2, v2); texel(i10, u3, v3);
                float tu = (u0 + u1 + u2 + u3) * 0.25f, tv = (v0 + v1 + v2 + v3) * 0.25f;
                tu -= floorf(tu); tv -= floorf(tv);   // clamp addressing in the renderer; texcoords stay in [0,1)
                int tx = std::min(w - 1, std::max(0, (int)(tu * w))), ty = std::min(h - 1, std::max(0, (int)(tv * h)));
                if (done[ty * w + tx]) continue; done[ty * w + tx] = 1;
                DWORD& px = bits[ty * w + tx]; int alpha = (int)(px >> 24);
                alpha += (int)lroundf((erase ? -255.f : 255.f) * std::min(1.f, amount * wgt));
                alpha = std::max(0, std::min(255, alpha));
                px = ((DWORD)alpha) << 24;
            }
        RwRasterUnlock(s.m_pTextureAlpha[layer]->raster);
        MarkDirtySector(idx); ++touched;
    }
    return touched;
}

static Value SectorLayers(float x, float z) {
    Value v = Value::obj(); CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return v;
    RwV3d p = { x, 0, z }; int idx = fm->GetSectorIdx(p); v["sector"] = idx; v["field"] = fm->GetFieldIdx(p);
    if (idx < 0 || !fm->m_pSectors[idx].m_bLoaded) return v;
    CNtlWorldSector& s = fm->m_pSectors[idx];
    v["base"] = std::string(s.m_pTextureBase ? RwTextureGetName(s.m_pTextureBase) : "");
    Value layers = Value::arr();
    for (int i = 0; i < dNTL_SPLAT_LAYER_NUM; ++i) layers.array.push_back(Value(std::string(s.m_pTextureSplat[i] ? RwTextureGetName(s.m_pTextureSplat[i]) : "")));
    v["layers"] = layers;
    return v;
}

// ---- interactive brush -------------------------------------------------------------------------------------------
static void BrushAt(int x, int y) {
    RwV3d hit; gBrushHover = gApp.world && FieldMgr()->PickTerrain(x, y, hit);
    if (gBrushHover) gBrushPos = hit;
}
static void BrushTick(float dt, bool invert) {
    if (gTool == TT_NONE || !gBrushDown || !gBrushHover || !gEditMode) return;
    if (gTool == TT_HEIGHT) {
        HeightMode m = gHeightMode; if (invert && m == HM_RAISE) m = HM_LOWER; else if (invert && m == HM_LOWER) m = HM_RAISE;
        if (m == HM_FLATTEN && !gFlattenCaptured) { gFlattenTarget = gBrushPos.y; gFlattenCaptured = true; }
        ApplyHeight(m, gBrushPos.x, gBrushPos.z, gBrushRadius, gBrushStrength * dt * (m == HM_SMOOTH || m == HM_FLATTEN ? 4.f : 1.f), gFlattenTarget);
    } else if (gTool == TT_PAINT) {
        ApplyPaint(gPaintLayer, gBrushPos.x, gBrushPos.z, gBrushRadius, gBrushStrength * dt, gPaintErase != invert, gPaintTexture);
    }
}
static void BrushBegin() { if (gTool == TT_NONE || !gEditMode) return; gBrushDown = true; gFlattenCaptured = false; BeginStep(gTool == TT_HEIGHT ? "height" : "paint"); }
static void BrushEnd() { if (!gBrushDown) return; gBrushDown = false; EndStep(); Emit(EditState()); }

static void RenderBrushCursor() {
    if (gTool == TT_NONE || !gBrushHover) return;
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return;
    const int seg = 64; RwIm3DVertex verts[seg * 2];
    RwRGBA c = gTool == TT_HEIGHT ? RwRGBA{ 255, 200, 60, 255 } : RwRGBA{ 90, 220, 255, 255 };
    if (!gEditMode) c = RwRGBA{ 160, 160, 160, 255 };
    RwV3d prev = { 0, 0, 0 };
    for (int i = 0; i <= seg; ++i) {
        float a = 2 * 3.14159265f * i / seg;
        RwV3d p = { gBrushPos.x + cosf(a) * gBrushRadius, gBrushPos.y, gBrushPos.z + sinf(a) * gBrushRadius };
        RwV3d q = p; if (fm->GetHeight(q)) p.y = q.y; p.y += 0.25f;
        if (i > 0) {
            RwIm3DVertexSetPos(&verts[(i - 1) * 2], prev.x, prev.y, prev.z); RwIm3DVertexSetRGBA(&verts[(i - 1) * 2], c.red, c.green, c.blue, c.alpha);
            RwIm3DVertexSetPos(&verts[(i - 1) * 2 + 1], p.x, p.y, p.z); RwIm3DVertexSetRGBA(&verts[(i - 1) * 2 + 1], c.red, c.green, c.blue, c.alpha);
        }
        prev = p;
    }
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEFOGENABLE, (void*)FALSE);
    if (RwIm3DTransform(verts, seg * 2, nullptr, rwIM3D_ALLOPAQUE)) { RwIm3DRenderPrimitive(rwPRIMTYPELINELIST); RwIm3DEnd(); }
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
}

// ---- protocol ------------------------------------------------------------------------------------------------------
static HeightMode ParseHeightMode(const std::string& s) { return s == "lower" ? HM_LOWER : s == "smooth" ? HM_SMOOTH : s == "flatten" ? HM_FLATTEN : HM_RAISE; }
static const char* HeightModeName(HeightMode m) { static const char* n[] = { "raise", "lower", "smooth", "flatten" }; return n[m]; }

// Returns true when the command belonged to this module (reply already emitted).
static bool TerrainCommand(const std::string& c, const Value& cmd) {
    if (c == "tool") {
        std::string t = cmd.text("tool", "none");
        gTool = t == "height" ? TT_HEIGHT : t == "paint" ? TT_PAINT : TT_NONE;
        if (cmd.has("mode")) gHeightMode = ParseHeightMode(cmd.text("mode"));
        if (cmd.has("radius")) gBrushRadius = (float)std::max(1.0, std::min(200.0, cmd.num("radius", gBrushRadius)));
        if (cmd.has("strength")) gBrushStrength = (float)std::max(0.01, std::min(50.0, cmd.num("strength", gBrushStrength)));
        if (cmd.has("layer")) gPaintLayer = (int)std::max(0.0, std::min((double)dNTL_SPLAT_LAYER_NUM - 1, cmd.num("layer", gPaintLayer)));
        if (cmd.has("erase")) gPaintErase = cmd.flag("erase", false);
        if (cmd.has("texture")) gPaintTexture = cmd.text("texture");
        if (cmd.has("slopeLighting")) gSlopeLighting = cmd.flag("slopeLighting", false);
        Value v = EditState("ack"); v["tool"] = t; v["mode"] = HeightModeName(gHeightMode); v["radius"] = (double)gBrushRadius; v["strength"] = (double)gBrushStrength;
        v["layer"] = gPaintLayer; v["texture"] = gPaintTexture; Reply(cmd, v); return true;
    }
    if (c == "terrain-height") {   // scripted stroke (CLI/tests): one application at x,z
        Require(gEditMode, "terrain edits need --edit (workspace)");
        BeginStep("height");
        if (cmd.has("slopeLighting")) gSlopeLighting = cmd.flag("slopeLighting", false);
        int n = ApplyHeight(ParseHeightMode(cmd.text("mode", "raise")), (float)cmd.num("x", gPos.x), (float)cmd.num("z", gPos.z),
            (float)cmd.num("radius", gBrushRadius), (float)cmd.num("amount", 1.0), (float)cmd.num("target", 0));
        EndStep();
        Value v = EditState("ack"); v["sectors"] = n; Reply(cmd, v); return true;
    }
    if (c == "terrain-paint") {
        Require(gEditMode, "terrain edits need --edit (workspace)");
        BeginStep("paint");
        int n = ApplyPaint((int)cmd.num("layer", 0), (float)cmd.num("x", gPos.x), (float)cmd.num("z", gPos.z), (float)cmd.num("radius", gBrushRadius),
            (float)cmd.num("amount", 1.0), cmd.flag("erase", false), cmd.text("texture"));
        EndStep();
        Value v = EditState("ack"); v["sectors"] = n; Reply(cmd, v); return true;
    }
    if (c == "alpha-stats") {   // diagnostics: per-layer alpha sum / non-zero texels of the sector at x,z
        CNtlWorldFieldManager* fm = FieldMgr(); RwV3d p = { (RwReal)cmd.num("x", gPos.x), 0, (RwReal)cmd.num("z", gPos.z) };
        int idx = fm ? fm->GetSectorIdx(p) : -1; Value v = Event("alpha-stats"); v["sector"] = idx; Value arr = Value::arr();
        if (idx >= 0 && fm->m_pSectors[idx].m_bLoaded)
            for (int i = 0; i < dNTL_SPLAT_LAYER_NUM; ++i) {
                Value l = Value::obj(); RwTexture* t = fm->m_pSectors[idx].m_pTextureAlpha[i];
                if (t) { int w = RwRasterGetWidth(t->raster), h = RwRasterGetHeight(t->raster); DWORD* b = (DWORD*)RwRasterLock(t->raster, 0, rwRASTERLOCKREAD);
                    double sum = 0; int nz = 0; if (b) { for (int k = 0; k < w * h; ++k) { int a = (int)(b[k] >> 24); sum += a; nz += a > 0; } RwRasterUnlock(t->raster); }
                    l["w"] = w; l["h"] = h; l["sum"] = sum; l["nonZero"] = nz; l["format"] = (int)RwRasterGetFormat(t->raster); }
                arr.array.push_back(l);
            }
        v["layers"] = arr; Reply(cmd, v); return true;
    }
    if (c == "terrain-layers") { Value v = SectorLayers((float)cmd.num("x", gPos.x), (float)cmd.num("z", gPos.z)); v["event"] = "layers"; Reply(cmd, v); return true; }
    if (c == "height-at") { RwV3d p = { (RwReal)cmd.num("x", gPos.x), 0, (RwReal)cmd.num("z", gPos.z) }; Value v = Event("height"); bool ok = FieldMgr() && FieldMgr()->GetHeight(p); v["ok"] = ok; v["y"] = (double)p.y; Reply(cmd, v); return true; }
    if (c == "undo") { Value v = EditState("ack"); v["undone"] = Undo(); Reply(cmd, v); return true; }
    if (c == "save") { Value v = SaveDirty(); Reply(cmd, v); return true; }
    if (c == "edit-state") { Reply(cmd, EditState()); return true; }
    return false;
}
