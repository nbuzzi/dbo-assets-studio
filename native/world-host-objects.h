#pragma once
// World Editor host · objects (ornaments from obj.pak / property\objectproperty.rdf). Included by world-host.cpp after
// world-host-edit.h and world-host-terrain.h.
//
// The engine keeps every placed object in CNtlWorldSector::m_vecNtlPLEntityOrnament of the sector that loaded it and
// serializes that vector into wfif (SectorObjectSaveIntoFile -> ObjectSaveIntoFile -> CNtlPLObject::SaveIntoFile).
// The library itself never buckets objects (the Saber tool did), so this module owns sector membership:
//   create  -> CreateEntity(PLENTITY_OBJECT, name, SPLObjectCreateParam) + InsertIn2World(obj, 0) on GetSectorIdx(pos)
//   move    -> SetPosition/SetRotate/SetScale; when the position leaves its sector: DeleteEntity(obj, 0) on the old
//              sector + InsertIn2World(obj, 0) on the new one; both fields are marked dirty.
//   delete  -> DeleteEntity(obj, 0) on its sector; the entity is kept hidden (graveyard) so undo can put it back intact
//              (PS map, flags, light attachments...).
// R6: an object whose name is not in objectproperty.rdf cannot be re-created on load and desynchronizes the sector
// stream, so names are validated against CNtlPLPropertyContainer before anything is created.
// Object pointers die when their sector leaves the streaming ring (MaterialDestructor deletes the ornaments), so every
// stored pointer is re-validated against the sector vector before it is touched.
#include "NtlPLObject.h"
#include "NtlPLObjectProperty.h"
#include <rpcollis.h>
#include <map>
#include <memory>

enum ObjectTool { OT_NONE, OT_SELECT, OT_PLACE };
static ObjectTool gObjTool = OT_NONE;
static std::string gObjPlaceName;
static float gObjPlaceYaw = 0.f, gObjPlaceScale = 1.f;
static bool gObjPlaceRandomYaw = false;
static CNtlPLObject* gObjSel = nullptr;           // selected object (validated every use)
static int gObjSelSector = -1;
static std::vector<CNtlPLObject*> gObjGraveyard;  // deleted but kept alive for undo (hidden, in no sector)
static int gObjGeneration = 0;                    // bumps on every object change / selection (UI refresh)
// mouse drag (select tool): move on terrain, Shift = vertical, Ctrl = rotate around Y
enum ObjDrag { OD_NONE, OD_PENDING, OD_MOVE, OD_LIFT, OD_ROTATE };
static ObjDrag gObjDrag = OD_NONE;
static int gObjDownX = 0, gObjDownY = 0, gObjLastX = 0, gObjLastY = 0;
static RwV3d gObjDragGrab = { 0, 0, 0 };          // object position - terrain hit at mouse down
static float gObjDragGroundOffset = 0.f;          // object y - ground y at mouse down
static bool gObjHover = false; static RwV3d gObjHoverPos = { 0, 0, 0 };
static bool gObjSerialPatched = false; static std::string gObjSerialNote;
// Errors from mouse actions have no command id; an id-less "error" event means "host failed" to the Studio, so they
// travel in the stats instead (objectError / objectErrorGeneration) and the UI shows them as a toast.
static std::string gObjError; static int gObjErrorGen = 0;

static bool SectorLoaded(int idx) {
    CNtlWorldFieldManager* fm = FieldMgr();
    return fm && idx >= 0 && idx < SectorCount() && fm->m_pSectors[idx].m_bLoaded;
}
static int ObjSectorAt(const RwV3d& p) { CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return -1; RwV3d q = p; return fm->GetSectorIdx(q); }
static bool InSector(CNtlPLObject* o, int idx) {
    if (!o || !SectorLoaded(idx)) return false;
    const auto& v = FieldMgr()->m_pSectors[idx].m_vecNtlPLEntityOrnament;
    return std::find(v.begin(), v.end(), (CNtlPLEntity*)o) != v.end();
}
// Where the object lives now: the hint first, then the 3x3 sectors around it (no dereference of a possibly dead pointer).
static int Locate(CNtlPLObject* o, int hint) {
    if (InSector(o, hint)) return hint;
    if (hint < 0) return -1;
    const int n = dGET_WORLD_PARAM()->WorldSectorNum;
    for (int dz = -1; dz <= 1; ++dz) for (int dx = -1; dx <= 1; ++dx) { int i = hint + dz * n + dx; if (InSector(o, i)) return i; }
    return -1;
}
static bool IsGraveyard(CNtlPLObject* o) { return std::find(gObjGraveyard.begin(), gObjGraveyard.end(), o) != gObjGraveyard.end(); }

// ---- catalog (objectproperty.rdf as loaded by the engine) ---------------------------------------------------------
static CNtlPLObjectProperty* CatalogEntry(const std::string& name) {
    if (name.empty() || !CNtlPLPropertyContainer::GetInstance()) return nullptr;
    auto& m = CNtlPLPropertyContainer::GetInstance()->Gets(PLENTITY_OBJECT);
    auto it = m.find(name);
    return it == m.end() ? nullptr : static_cast<CNtlPLObjectProperty*>(it->second);
}
static std::string Lower(std::string s) { for (auto& c : s) c = (char)tolower((unsigned char)c); return s; }
static Value Catalog(const std::string& filter, int limit) {
    Value v = Event("objects-catalog"); Value names = Value::arr(); int total = 0, matched = 0;
    if (CNtlPLPropertyContainer::GetInstance()) {
        std::string f = Lower(filter);
        for (auto& kv : CNtlPLPropertyContainer::GetInstance()->Gets(PLENTITY_OBJECT)) {
            if (kv.first.empty()) continue;   // the rdf has one unnamed record
            ++total;
            if (!f.empty() && Lower(kv.first).find(f) == std::string::npos) continue;
            ++matched;
            if ((int)names.array.size() >= limit) continue;
            auto* p = static_cast<CNtlPLObjectProperty*>(kv.second);
            Value e = Value::obj(); e["name"] = kv.first; e["dff"] = std::string(p ? p->GetDffName() : ""); e["type"] = p ? (int)p->GetObjectType() : -1;
            names.array.push_back(e);
        }
    }
    v["total"] = total; v["matched"] = matched; v["items"] = names; v["source"] = "property\\objectproperty.rdf (CNtlPLPropertyContainer)";
    return v;
}

// ---- describe -----------------------------------------------------------------------------------------------------
static Value Vec(const RwV3d& p) { Value a = Value::arr(); a.array = { Value((double)p.x), Value((double)p.y), Value((double)p.z) }; return a; }
static float ObjRadius(CNtlPLObject* o) { RwSphere* s = o->GetBoundingSphere(); return s && s->radius > 0.1f ? s->radius : 1.5f; }
static RwV3d ObjCenter(CNtlPLObject* o) { RwSphere* s = o->GetBoundingSphere(); return s && s->radius > 0.1f ? s->center : o->GetPosition(); }
static Value Describe(CNtlPLObject* o, int sector, int index = -1) {
    Value v = Value::obj();
    v["name"] = std::string(o->GetName() ? o->GetName() : "");
    v["serial"] = (double)o->GetObjectSerialID(); v["sid"] = (double)o->GetSID();
    v["sector"] = sector; v["field"] = FieldOfSector(sector); if (index >= 0) v["index"] = index;
    v["pos"] = Vec(o->GetPosition()); v["rot"] = Vec(*o->GetRotate()); v["scale"] = Vec(*o->GetScale());
    v["radius"] = (double)ObjRadius(o); v["type"] = (int)o->GetObjectType(); v["loaded"] = o->GetClump() != nullptr;
    return v;
}
static int IndexIn(CNtlPLObject* o, int sector) {
    const auto& vec = FieldMgr()->m_pSectors[sector].m_vecNtlPLEntityOrnament;
    for (int i = 0; i < (int)vec.size(); ++i) if (vec[i] == (CNtlPLEntity*)o) return i;
    return -1;
}
static Value SelectionValue() {
    if (gObjSel && (gObjSelSector = Locate(gObjSel, gObjSelSector)) < 0) gObjSel = nullptr;
    if (!gObjSel) return Value();
    return Describe(gObjSel, gObjSelSector, IndexIn(gObjSel, gObjSelSector));
}
static void Select(CNtlPLObject* o, int sector) { gObjSel = o; gObjSelSector = o ? sector : -1; ++gObjGeneration; }

// Resolve a command's target: "serial" (unique for objects placed here; most of the base data), else sector+index,
// else the current selection.
static CNtlPLObject* Target(const Value& cmd, int& sector) {
    CNtlWorldFieldManager* fm = FieldMgr(); Require(fm != nullptr, "no world");
    if (cmd.has("sector") && cmd.has("index")) {
        int s = (int)cmd.num("sector", -1), i = (int)cmd.num("index", -1);
        Require(SectorLoaded(s), "sector not loaded");
        const auto& vec = fm->m_pSectors[s].m_vecNtlPLEntityOrnament; Require(i >= 0 && i < (int)vec.size(), "object index out of range");
        sector = s; return static_cast<CNtlPLObject*>(vec[i]);
    }
    if (cmd.has("serial")) {
        RwUInt32 serial = (RwUInt32)cmd.num("serial", 0); Require(serial != 0, "serial 0 is not unique; use sector+index");
        // search near the hinted position first, then every loaded sector
        std::vector<int> order;
        if (cmd.has("x") && cmd.has("z")) { RwV3d p = { (RwReal)cmd.num("x", 0), 0, (RwReal)cmd.num("z", 0) }; int s = ObjSectorAt(p); if (s >= 0) order.push_back(s); }
        for (int s = 0, n = SectorCount(); s < n; ++s) if (fm->m_pSectors[s].m_bLoaded) order.push_back(s);
        for (int s : order) {
            if (!SectorLoaded(s)) continue;
            for (CNtlPLEntity* e : fm->m_pSectors[s].m_vecNtlPLEntityOrnament)
                if (static_cast<CNtlPLObject*>(e)->GetObjectSerialID() == serial) { sector = s; return static_cast<CNtlPLObject*>(e); }
        }
        throw std::runtime_error("no loaded object with that serial");
    }
    Require(gObjSel != nullptr && (gObjSelSector = Locate(gObjSel, gObjSelSector)) >= 0, "no object selected");
    sector = gObjSelSector; return gObjSel;
}

// ---- serials: new objects take the world counter (gwp WorldObjectSerialID), persisted in the workspace gwp -------------
// The gwp counter is not authoritative for the shipped data (dbo_main_world: counter 3, serials up to ~130k: the merge
// tool resets it), and only the streamed-in sectors can be scanned. New objects therefore take serials from a range
// reserved for this editor, far above the legacy ones, and the counter in the workspace gwp keeps it monotonic.
static const RwUInt32 kEditorSerialBase = 10000000u, kEditorSerialCeiling = 0x7FFFFFFFu;
static RwUInt32 MaxLoadedSerial() {
    CNtlWorldFieldManager* fm = FieldMgr(); RwUInt32 m = 0; if (!fm) return 0;
    for (int s = 0, n = SectorCount(); s < n; ++s) if (fm->m_pSectors[s].m_bLoaded)
        for (CNtlPLEntity* e : fm->m_pSectors[s].m_vecNtlPLEntityOrnament) {
            RwUInt32 id = static_cast<CNtlPLObject*>(e)->GetObjectSerialID();
            if (id < kEditorSerialCeiling) m = std::max(m, id);   // the base data has 0xFFFFFFFF placeholders
        }
    return m;
}
// The engine's SaveCurWorldState would also rewrite runtime values (WorldPolyCnt...), so only the counter is patched:
// for version 1.4 descriptors it is the second to last dword (followed by WorldResourceID).
static void PersistSerialCounter(RwUInt32 before, RwUInt32 after) {
    std::string path = std::string(dGET_WORLD_PARAM()->WorldProjectFolderName) + "\\#######.gwp";
    FILE* f = fopen(path.c_str(), "r+b"); if (!f) { gObjSerialNote = "gwp not writable"; return; }
    fseek(f, 0, SEEK_END); long size = ftell(f); RwUInt32 cur = 0; bool ok = false;
    if (size >= 8 && dNTL_WORLD_VERSION_COMPARE(dGET_WORLD_PARAM()->WorldVer, dNTL_WORLD_VERSION)) {
        fseek(f, size - 8, SEEK_SET);
        if (fread(&cur, 4, 1, f) == 1 && cur == before) { fseek(f, size - 8, SEEK_SET); ok = fwrite(&after, 4, 1, f) == 1; }
    }
    fclose(f);
    gObjSerialPatched = gObjSerialPatched || ok;
    gObjSerialNote = ok ? "gwp WorldObjectSerialID updated" : "gwp counter not patched (layout/value mismatch)";
}
static RwUInt32 NextSerial() {
    sNtlWorldParam* wp = dGET_WORLD_PARAM();
    RwUInt32 before = wp->WorldObjectSerialID;
    RwUInt32 serial = std::max(std::max(before, kEditorSerialBase), MaxLoadedSerial() + 1);
    wp->WorldObjectSerialID = serial + 1;
    PersistSerialCounter(before, wp->WorldObjectSerialID);
    return serial;
}

// ---- engine operations --------------------------------------------------------------------------------------------
struct ObjSRT { RwV3d s, r, t; };
static ObjSRT GetSRT(CNtlPLObject* o) { return ObjSRT{ *o->GetScale(), *o->GetRotate(), o->GetPosition() }; }

// Membership goes through the engine's own CNtlWorldSector::DeleteEntity / InsertIn2World (menu 0 = ornaments); the
// index is kept so undo puts an object back where it was and the rewritten wfif is byte-identical to the original.
static int Detach(CNtlPLObject* o, int sector) {
    int at = IndexIn(o, sector);
    FieldMgr()->m_pSectors[sector].DeleteEntity(o, 0); MarkDirtySector(sector);
    return at;
}
static void MoveToIndex(CNtlPLObject* o, int sector, int at) {
    auto& v = FieldMgr()->m_pSectors[sector].m_vecNtlPLEntityOrnament;
    int cur = IndexIn(o, sector);
    if (cur < 0 || at < 0 || at >= (int)v.size() || at == cur) return;
    v.erase(v.begin() + cur); v.insert(v.begin() + at, (CNtlPLEntity*)o);
}
static void Attach(CNtlPLObject* o, int sector, int at = -1) {
    FieldMgr()->m_pSectors[sector].InsertIn2World(o, 0); MarkDirtySector(sector);
    MoveToIndex(o, sector, at);
}

// Applies SRT and re-buckets the object; returns its (possibly new) sector. Throws before touching anything if the
// destination sector is not loaded (it would be saved nowhere).
static int ApplySRT(CNtlPLObject* o, int sector, const ObjSRT& v) {
    int dst = ObjSectorAt(v.t);
    Require(dst >= 0 && SectorLoaded(dst), "destination sector is not loaded (move the camera closer)");
    o->SetScale(&v.s);
    o->SetRotate(v.r.x, v.r.y, v.r.z);
    o->SetPosition(&v.t);
    if (dst != sector) { Detach(o, sector); Attach(o, dst); }
    else MarkDirtySector(sector);
    ++gObjGeneration;
    return dst;
}

// Undo entries share a tracker so later steps can find an object after earlier steps moved it.
struct ObjTrack { CNtlPLObject* obj; int sector; };
static std::map<CNtlPLObject*, std::shared_ptr<ObjTrack>> gObjTracks;
static std::shared_ptr<ObjTrack> Track(CNtlPLObject* o, int sector) {
    auto& t = gObjTracks[o]; if (!t) t = std::make_shared<ObjTrack>(ObjTrack{ o, sector }); else t->sector = sector;
    return t;
}
static void ForgetTrack(CNtlPLObject* o) { gObjTracks.erase(o); }

static void PushTransformUndo(CNtlPLObject* o, int sector, const ObjSRT& old) {
    if (!gOpenStep) return;
    auto t = Track(o, sector);
    const int home = sector, at = IndexIn(o, sector);
    gOpenStep->fields.insert(FieldOfSector(sector));
    gOpenStep->restore.push_back([t, old, home, at]() {
        int s = Locate(t->obj, t->sector); if (s < 0) return;   // sector streamed out: the edit is already on disk
        try { t->sector = ApplySRT(t->obj, s, old); if (t->sector == home) MoveToIndex(t->obj, home, at); } catch (...) {}
    });
}

static CNtlPLObject* CreateObject(const std::string& name, const ObjSRT& v, int& sectorOut) {
    Require(gEditMode, "object edits need --edit (workspace)");
    Require(CatalogEntry(name) != nullptr, "unknown object name (not in objectproperty.rdf): refusing to create it, it would break the sector stream (R6)");
    int sector = ObjSectorAt(v.t);
    Require(sector >= 0 && SectorLoaded(sector), "target sector is not loaded (move the camera closer)");
    RwV3d pos = v.t;
    SPLObjectCreateParam param; param.pPos = &pos; param.bLoadMap = TRUE;
    CNtlPLResourceManager* rm = CNtlPLResourceManager::GetInstance();
    RwBool scheduling = rm->IsLoadScheduling(); rm->SetLoadScheduling(FALSE);   // build the clump now (bounding sphere, picking)
    CNtlPLObject* o = static_cast<CNtlPLObject*>(GetSceneManager()->CreateEntity(PLENTITY_OBJECT, name.c_str(), &param));
    rm->SetLoadScheduling(scheduling);
    Require(o != nullptr, "CreateEntity(PLENTITY_OBJECT) failed");
    // same order as CNtlWorldSectorInfo::ObjectLoadFromFile
    o->SetScale(&v.s); o->SetRotate(v.r.x, v.r.y, v.r.z); o->SetPosition(&pos);
    o->SetObjectType(EPL_OBJECT_TYPE_OUTDOOR_OBJECT);
    if (o->GetPlayAnim()) o->SetPlayAnimTime(NtlRandomNumber(0.0f, 1.0f));
    o->SetFadeEnable(TRUE);
    o->SetObjectSerialID(NextSerial());
    Attach(o, sector);
    ++gObjGeneration;
    if (gOpenStep) {
        auto t = Track(o, sector);
        gOpenStep->fields.insert(FieldOfSector(sector));
        gOpenStep->restore.push_back([t]() {
            int s = Locate(t->obj, t->sector); if (s < 0) return;
            if (gObjSel == t->obj) Select(nullptr, -1);
            Detach(t->obj, s); ForgetTrack(t->obj);
            GetSceneManager()->DeleteEntity(t->obj);
            ++gObjGeneration;
        });
    }
    sectorOut = sector;
    return o;
}

static void DeleteObject(CNtlPLObject* o, int sector) {
    Require(gEditMode, "object edits need --edit (workspace)");
    const int at = Detach(o, sector);
    o->SetVisible(FALSE);
    gObjGraveyard.push_back(o);
    if (gObjSel == o) Select(nullptr, -1);
    ++gObjGeneration;
    if (gOpenStep) {
        auto t = Track(o, sector);
        gOpenStep->fields.insert(FieldOfSector(sector));
        gOpenStep->restore.push_back([t, sector, at]() {
            if (!IsGraveyard(t->obj)) return;
            int s = ObjSectorAt(t->obj->GetPosition());
            if (!SectorLoaded(s)) return;   // its sector streamed out meanwhile: the deletion stays
            gObjGraveyard.erase(std::find(gObjGraveyard.begin(), gObjGraveyard.end(), t->obj));
            t->obj->SetVisible(TRUE);
            Attach(t->obj, s, s == sector ? at : -1); t->sector = s;
            ++gObjGeneration;
        });
    }
}

// ---- picking ------------------------------------------------------------------------------------------------------
// Camera ray against each object's bounding sphere (CNtlPLObject::GetBoundingSphere) as a broad phase, then the ray
// against the clump's triangles (RpAtomicForAllIntersections, rpcollis) so a big tree sphere does not hide the house
// behind it. Objects whose clump is not built yet fall back to the sphere entry distance.
struct TriHit { float best; };
static RpCollisionTriangle* TriHitCB(RpIntersection*, RpCollisionTriangle* tri, RwReal distance, void* data) {
    TriHit* h = (TriHit*)data; if (distance < h->best) h->best = distance; return tri;
}
struct AtomicRay { RpIntersection is; TriHit hit; };
static RpAtomic* AtomicRayCB(RpAtomic* a, void* data) {
    AtomicRay* r = (AtomicRay*)data;
    if (RpAtomicGetFlags(a) & rpATOMICRENDER) RpAtomicForAllIntersections(a, &r->is, TriHitCB, &r->hit);
    return a;
}
static CNtlPLObject* PickObject(int x, int y, int& sectorOut, float& tOut) {
    sectorOut = -1; tOut = 1e30f; CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return nullptr;
    RwV2d px = { (RwReal)x, (RwReal)y }; RwLine ray;
    if (!RwCameraCalcPixelRay(ActiveCamera(), &ray, &px)) return nullptr;
    RwV3d d = { ray.end.x - ray.start.x, ray.end.y - ray.start.y, ray.end.z - ray.start.z };
    float len = sqrtf(d.x * d.x + d.y * d.y + d.z * d.z); if (len <= 0) return nullptr; d.x /= len; d.y /= len; d.z /= len;
    CNtlPLObject* best = nullptr; float bestT = 1e30f;          // precise (triangle) hits
    CNtlPLObject* coarse = nullptr; float coarseT = 1e30f; int coarseS = -1;   // sphere-only fallback
    for (int s = 0, n = SectorCount(); s < n; ++s) {
        if (!fm->m_pSectors[s].m_bLoaded) continue;
        for (CNtlPLEntity* e : fm->m_pSectors[s].m_vecNtlPLEntityOrnament) {
            CNtlPLObject* o = static_cast<CNtlPLObject*>(e);
            if (!o->IsVisible()) continue;
            RwV3d c = ObjCenter(o); float r = ObjRadius(o);
            RwV3d w = { c.x - ray.start.x, c.y - ray.start.y, c.z - ray.start.z };
            float tc = w.x * d.x + w.y * d.y + w.z * d.z;
            float d2 = w.x * w.x + w.y * w.y + w.z * w.z - tc * tc; if (d2 > r * r) continue;
            float entry = tc - sqrtf(std::max(0.f, r * r - d2));
            if (tc + r < 0 || entry > bestT) continue;
            if (o->GetClump()) {
                AtomicRay ar; ar.is.type = rpINTERSECTLINE; ar.is.t.line = ray; ar.hit.best = 2.f;
                RpClumpForAllAtomics(o->GetClump(), AtomicRayCB, &ar);
                if (ar.hit.best <= 1.f) { float t = ar.hit.best * len; if (t < bestT) { bestT = t; best = o; sectorOut = s; } }
            } else if (entry > 0 && entry < coarseT) { coarseT = entry; coarse = o; coarseS = s; }
        }
    }
    if (!best && coarse) { best = coarse; bestT = coarseT; sectorOut = coarseS; }
    tOut = bestT;
    return best;
}

// ---- interactive tool -----------------------------------------------------------------------------------------------
static bool ObjectsActive() { return gObjTool != OT_NONE && gTool == TT_NONE; }
static void ObjHoverAt(int x, int y) { RwV3d hit; gObjHover = gApp.world && FieldMgr()->PickTerrain(x, y, hit); if (gObjHover) gObjHoverPos = hit; }
static void EmitSelection() { Value v = Event("object-selected"); v["object"] = SelectionValue(); v["generation"] = gObjGeneration; Emit(v); }

static bool ObjectsMouseDown(int x, int y, WPARAM w) {
    if (!ObjectsActive()) return false;
    gObjDownX = gObjLastX = x; gObjDownY = gObjLastY = y; gObjDrag = OD_NONE;
    if (gObjTool == OT_SELECT && gEditMode && gObjSel && (gObjSelSector = Locate(gObjSel, gObjSelSector)) >= 0) {
        int s; float t; CNtlPLObject* hit = PickObject(x, y, s, t);
        if (hit == gObjSel) {   // grab the selected object: drag edits it
            gObjDrag = OD_PENDING;
            RwV3d p = gObjSel->GetPosition(), g = p, th;
            gObjDragGroundOffset = FieldMgr()->GetHeight(g) ? p.y - g.y : 0.f;
            if (FieldMgr()->PickTerrain(x, y, th)) { gObjDragGrab = { p.x - th.x, 0, p.z - th.z }; } else gObjDragGrab = { 0, 0, 0 };
        }
    }
    return true;
}
static void ObjectsMouseMove(int x, int y, WPARAM w) {
    if (!ObjectsActive()) return;
    ObjHoverAt(x, y);
    if (gObjDrag == OD_NONE) return;
    if (!gObjSel || (gObjSelSector = Locate(gObjSel, gObjSelSector)) < 0) { gObjDrag = OD_NONE; EndStep(); return; }
    if (gObjDrag == OD_PENDING) {
        if (abs(x - gObjDownX) < 4 && abs(y - gObjDownY) < 4) return;
        gObjDrag = (w & MK_CONTROL) ? OD_ROTATE : (w & MK_SHIFT) ? OD_LIFT : OD_MOVE;
        BeginStep(gObjDrag == OD_ROTATE ? "object-rotate" : "object-move");
        PushTransformUndo(gObjSel, gObjSelSector, GetSRT(gObjSel));
    }
    ObjSRT v = GetSRT(gObjSel);
    if (gObjDrag == OD_MOVE) {
        RwV3d th; if (!FieldMgr()->PickTerrain(x, y, th)) return;
        v.t.x = th.x + gObjDragGrab.x; v.t.z = th.z + gObjDragGrab.z;
        RwV3d g = v.t; v.t.y = FieldMgr()->GetHeight(g) ? g.y + gObjDragGroundOffset : th.y;
    } else if (gObjDrag == OD_LIFT) {
        v.t.y -= (y - gObjLastY) * std::max(0.02f, gDist * 0.004f);
    } else if (gObjDrag == OD_ROTATE) {
        v.r.y = fmodf(v.r.y + (x - gObjLastX) * 0.5f + 360.f, 360.f);
    }
    gObjLastX = x; gObjLastY = y;
    try { gObjSelSector = ApplySRT(gObjSel, gObjSelSector, v); } catch (...) {}   // leaving the loaded ring: stay put
}
static bool ObjectsMouseUp(int x, int y, WPARAM w) {
    if (!ObjectsActive()) return false;
    if (gObjDrag == OD_MOVE || gObjDrag == OD_LIFT || gObjDrag == OD_ROTATE) {
        gObjDrag = OD_NONE; EndStep(); Emit(EditState()); EmitSelection(); return true;
    }
    gObjDrag = OD_NONE;
    if (abs(x - gObjDownX) >= 4 || abs(y - gObjDownY) >= 4) return true;
    if (gObjTool == OT_PLACE) {
        RwV3d hit;
        if (!gEditMode) { gObjError = "placing objects needs the edit workspace"; ++gObjErrorGen; return true; }
        if (!FieldMgr()->PickTerrain(x, y, hit)) { gObjError = "no terrain under the cursor"; ++gObjErrorGen; return true; }
        float yaw = gObjPlaceRandomYaw ? NtlRandomNumber(0.f, 360.f) : gObjPlaceYaw;
        ObjSRT v{ { gObjPlaceScale, gObjPlaceScale, gObjPlaceScale }, { 0, yaw, 0 }, hit };
        try {
            BeginStep("object-create"); int s; CNtlPLObject* o = CreateObject(gObjPlaceName, v, s); EndStep();
            Select(o, s);
        } catch (const std::exception& e) { EndStep(); if (!gUndo.empty() && gUndo.back().restore.empty()) gUndo.pop_back(); gObjError = e.what(); ++gObjErrorGen; }
        Emit(EditState()); EmitSelection();
        return true;
    }
    int s; float t; CNtlPLObject* o = PickObject(x, y, s, t);
    Select(o, s); EmitSelection();
    return true;
}
static void ObjectsMouseCancel() { if (gObjDrag >= OD_MOVE) EndStep(); gObjDrag = OD_NONE; }
static bool ObjectsKey(WPARAM key) {
    if (!ObjectsActive() || !gEditMode || key != VK_DELETE || !gObjSel) return false;
    int s = Locate(gObjSel, gObjSelSector); if (s < 0) return false;
    BeginStep("object-delete"); DeleteObject(gObjSel, s); EndStep();
    Emit(EditState()); EmitSelection();
    return true;
}

// ---- gizmo ----------------------------------------------------------------------------------------------------------
static void RenderObjectGizmo() {
    if (gObjTool == OT_NONE && !gObjSel) return;
    static std::vector<RwIm3DVertex> verts; verts.clear();
    auto add = [&](const RwV3d& a, const RwV3d& b, RwRGBA c) {
        RwIm3DVertex v0, v1;
        RwIm3DVertexSetPos(&v0, a.x, a.y, a.z); RwIm3DVertexSetRGBA(&v0, c.red, c.green, c.blue, c.alpha);
        RwIm3DVertexSetPos(&v1, b.x, b.y, b.z); RwIm3DVertexSetRGBA(&v1, c.red, c.green, c.blue, c.alpha);
        verts.push_back(v0); verts.push_back(v1);
    };
    auto ring = [&](const RwV3d& c, float r, RwRGBA col, bool vertical) {
        const int seg = 48; RwV3d prev = c;
        for (int i = 0; i <= seg; ++i) {
            float a = 2 * 3.14159265f * i / seg;
            RwV3d p = vertical ? RwV3d{ c.x + cosf(a) * r, c.y + sinf(a) * r, c.z } : RwV3d{ c.x + cosf(a) * r, c.y, c.z + sinf(a) * r };
            if (i) add(prev, p, col); prev = p;
        }
    };
    if (gObjSel && (gObjSelSector = Locate(gObjSel, gObjSelSector)) >= 0) {
        RwV3d p = gObjSel->GetPosition(); float r = ObjRadius(gObjSel), L = std::max(2.f, r * 1.2f);
        float yaw = gObjSel->GetRotate()->y * kDeg;
        RwV3d px = { p.x + cosf(yaw) * L, p.y, p.z - sinf(yaw) * L }, pz = { p.x + sinf(yaw) * L, p.y, p.z + cosf(yaw) * L }, py = { p.x, p.y + L, p.z };
        add(p, px, RwRGBA{ 255, 70, 70, 255 }); add(p, py, RwRGBA{ 70, 255, 90, 255 }); add(p, pz, RwRGBA{ 80, 140, 255, 255 });
        RwRGBA sel = gEditMode ? RwRGBA{ 80, 240, 255, 255 } : RwRGBA{ 170, 170, 170, 255 };
        ring({ p.x, p.y + 0.1f, p.z }, r, sel, false);
        RwV3d c = ObjCenter(gObjSel); ring(c, r, RwRGBA{ 80, 240, 255, 120 }, true);
    }
    if (ObjectsActive() && gObjTool == OT_PLACE && gObjHover) {
        RwRGBA c = gEditMode ? RwRGBA{ 255, 210, 70, 255 } : RwRGBA{ 160, 160, 160, 255 };
        RwV3d p = gObjHoverPos; ring({ p.x, p.y + 0.15f, p.z }, 1.5f * gObjPlaceScale, c, false);
        add(p, RwV3d{ p.x, p.y + 4.f, p.z }, c);
    }
    if (verts.empty()) return;
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEFOGENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEVERTEXALPHAENABLE, (void*)TRUE);
    if (RwIm3DTransform(verts.data(), (RwInt32)verts.size(), nullptr, rwIM3D_ALLOPAQUE)) { RwIm3DRenderPrimitive(rwPRIMTYPELINELIST); RwIm3DEnd(); }
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
}

// ---- stats (for the Studio poll) -------------------------------------------------------------------------------------
static void ObjectsStats(Value& v) {
    v["objectTool"] = gObjTool == OT_SELECT ? "select" : gObjTool == OT_PLACE ? "place" : "none";
    v["objectPlace"] = gObjPlaceName; v["objectGeneration"] = gObjGeneration;
    v["objectSelection"] = SelectionValue();
    v["objectError"] = gObjError; v["objectErrorGeneration"] = gObjErrorGen;
}

// ---- protocol ------------------------------------------------------------------------------------------------------
static ObjSRT SRTFrom(const Value& cmd, ObjSRT v) {
    auto vec3 = [&](const char* key, RwV3d& out) {
        if (!cmd.has(key)) return; const Value& a = cmd.at(key);
        if (a.type == Value::Array && a.array.size() == 3) { out.x = (RwReal)a.array[0].number; out.y = (RwReal)a.array[1].number; out.z = (RwReal)a.array[2].number; }
        else if (a.type == Value::Number) out.x = out.y = out.z = (RwReal)a.number;   // uniform scale
    };
    vec3("pos", v.t); vec3("rot", v.r); vec3("scale", v.s);
    if (cmd.has("x")) v.t.x = (RwReal)cmd.num("x", v.t.x);
    if (cmd.has("y")) v.t.y = (RwReal)cmd.num("y", v.t.y);
    if (cmd.has("z")) v.t.z = (RwReal)cmd.num("z", v.t.z);
    if (cmd.has("dx")) v.t.x += (RwReal)cmd.num("dx", 0);
    if (cmd.has("dy")) v.t.y += (RwReal)cmd.num("dy", 0);
    if (cmd.has("dz")) v.t.z += (RwReal)cmd.num("dz", 0);
    if (cmd.has("yaw")) v.r.y = (RwReal)cmd.num("yaw", v.r.y);
    if (cmd.has("ground") && cmd.flag("ground", false)) { RwV3d g = v.t; if (FieldMgr()->GetHeight(g)) v.t.y = g.y + (RwReal)cmd.num("lift", 0); }
    return v;
}

static bool ObjectsCommand(const std::string& c, const Value& cmd) {
    if (c.rfind("objects-", 0) != 0 && c.rfind("object-", 0) != 0) return false;
    if (c == "objects-tool") {
        std::string t = cmd.text("tool", "none");
        gObjTool = t == "select" ? OT_SELECT : t == "place" ? OT_PLACE : OT_NONE;
        if (gObjTool != OT_NONE) gTool = TT_NONE;   // one active tool: the terrain brush is released
        if (cmd.has("name")) {
            std::string n = cmd.text("name");
            Require(n.empty() || CatalogEntry(n) != nullptr, "unknown object name (not in objectproperty.rdf)");
            gObjPlaceName = n;
        }
        if (gObjTool == OT_PLACE) Require(!gObjPlaceName.empty(), "place tool needs a catalog name");
        if (cmd.has("yaw")) gObjPlaceYaw = (float)cmd.num("yaw", 0);
        if (cmd.has("scale")) gObjPlaceScale = (float)std::max(0.05, std::min(50.0, cmd.num("scale", 1)));
        if (cmd.has("randomYaw")) gObjPlaceRandomYaw = cmd.flag("randomYaw", false);
        Value v = EditState("ack"); ObjectsStats(v); Reply(cmd, v); return true;
    }
    if (c == "objects-catalog") { Reply(cmd, Catalog(cmd.text("filter"), (int)std::max(1.0, std::min(5000.0, cmd.num("limit", 200))))); return true; }
    if (c == "objects-validate") {   // R6 check for a batch of names (CLI / candidate validation)
        Value v = Event("objects-validate"); Value bad = Value::arr(); int n = 0;
        if (cmd.has("names")) for (const auto& x : cmd.at("names").array) { ++n; if (!CatalogEntry(x.string)) bad.array.push_back(Value(x.string)); }
        v["checked"] = n; v["unknown"] = bad; v["ok"] = bad.array.empty(); Reply(cmd, v); return true;
    }
    if (c == "objects-list") {
        CNtlWorldFieldManager* fm = FieldMgr(); Require(fm != nullptr, "no world");
        float cx = (float)cmd.num("x", gPos.x), cz = (float)cmd.num("z", gPos.z), r = (float)std::max(1.0, std::min(2000.0, cmd.num("radius", 64)));
        std::string filter = Lower(cmd.text("filter")); int limit = (int)cmd.num("limit", 500);
        std::vector<int> sectors;
        if (cmd.has("sector")) sectors.push_back((int)cmd.num("sector", -1));
        else for (int s = 0, n = SectorCount(); s < n; ++s) if (fm->m_pSectors[s].m_bLoaded) sectors.push_back(s);
        Value items = Value::arr(); int total = 0;
        for (int s : sectors) {
            if (!SectorLoaded(s)) continue;
            const auto& vec = fm->m_pSectors[s].m_vecNtlPLEntityOrnament;
            for (int i = 0; i < (int)vec.size(); ++i) {
                CNtlPLObject* o = static_cast<CNtlPLObject*>(vec[i]); RwV3d p = o->GetPosition();
                if (!cmd.has("sector") && (p.x - cx) * (p.x - cx) + (p.z - cz) * (p.z - cz) > r * r) continue;
                if (!filter.empty() && Lower(o->GetName() ? o->GetName() : "").find(filter) == std::string::npos) continue;
                ++total; if ((int)items.array.size() < limit) items.array.push_back(Describe(o, s, i));
            }
        }
        Value v = Event("objects"); v["count"] = total; v["objects"] = items; v["center"] = Vec(RwV3d{ cx, 0, cz }); v["radius"] = (double)r;
        Reply(cmd, v); return true;
    }
    if (c == "objects-pick") {   // screen pick (CLI / automation): same as a click with the select tool
        int s; float t; CNtlPLObject* o = PickObject((int)cmd.num("x", gWidth / 2), (int)cmd.num("y", gHeight / 2), s, t);
        if (cmd.flag("select", true)) Select(o, s);
        Value v = Event("objects-pick"); v["object"] = o ? Describe(o, s, IndexIn(o, s)) : Value(); if (o) v["distance"] = (double)t;
        Reply(cmd, v); return true;
    }
    if (c == "objects-select") {
        if (cmd.flag("clear", false)) Select(nullptr, -1);
        else { int s; CNtlPLObject* o = Target(cmd, s); Select(o, s); if (cmd.flag("focus", false)) { gPos = o->GetPosition(); } }
        Value v = Event("ack"); v["object"] = SelectionValue(); Reply(cmd, v); return true;
    }
    if (c == "objects-selected") { Value v = Event("objects-selected"); v["object"] = SelectionValue(); v["generation"] = gObjGeneration; Reply(cmd, v); return true; }
    if (c == "objects-create") {
        Require(gEditMode, "object edits need --edit (workspace)");
        std::string name = cmd.text("name", gObjPlaceName);
        ObjSRT v{ { 1, 1, 1 }, { 0, 0, 0 }, { (RwReal)cmd.num("x", gPos.x), (RwReal)cmd.num("y", gPos.y), (RwReal)cmd.num("z", gPos.z) } };
        if (!cmd.has("y") && !cmd.has("pos")) { RwV3d g = v.t; if (FieldMgr()->GetHeight(g)) v.t.y = g.y; }
        v = SRTFrom(cmd, v);
        BeginStep("object-create"); int s = -1; CNtlPLObject* o = nullptr;
        try { o = CreateObject(name, v, s); } catch (...) { EndStep(); gUndo.pop_back(); throw; }
        EndStep();
        if (cmd.flag("select", true)) Select(o, s);
        Value r = EditState("ack"); r["object"] = Describe(o, s, IndexIn(o, s)); if (!gObjSerialNote.empty()) r["serialNote"] = gObjSerialNote;
        Reply(cmd, r); return true;
    }
    if (c == "objects-transform" || c == "object-move") {
        Require(gEditMode, "object edits need --edit (workspace)");
        int s; CNtlPLObject* o = Target(cmd, s);
        ObjSRT old = GetSRT(o), v = SRTFrom(cmd, old);
        const int from = s;
        BeginStep("object-transform"); PushTransformUndo(o, s, old);
        try { s = ApplySRT(o, s, v); } catch (...) { EndStep(); gUndo.pop_back(); throw; }
        EndStep();
        if (o == gObjSel) gObjSelSector = s;
        Value r = EditState("ack"); r["object"] = Describe(o, s, IndexIn(o, s)); r["fromSector"] = from; r["rebucketed"] = from != s;
        Reply(cmd, r); return true;
    }
    if (c == "objects-delete") {
        Require(gEditMode, "object edits need --edit (workspace)");
        int s; CNtlPLObject* o = Target(cmd, s); Value before = Describe(o, s, IndexIn(o, s));
        BeginStep("object-delete"); DeleteObject(o, s); EndStep();
        Value r = EditState("ack"); r["deleted"] = before; Reply(cmd, r); return true;
    }
    Value v = Event("error"); v["message"] = "unknown objects command: " + c; Reply(cmd, v); return true;
}
