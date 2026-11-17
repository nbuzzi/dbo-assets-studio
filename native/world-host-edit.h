#pragma once
// World Editor host · shared edit infrastructure (included once by world-host.cpp).
//
// Edit mode (--edit) runs on a *workspace*: a private copy of world\<name>\ whose root the Studio created
// (shared folders are junctions to the read-only cache). Only there g_bNtlWorldToolSaveOnUnload is TRUE, so
// fields that leave the streaming ring keep their edits on disk. The viewer (no --edit) never writes.
//
// Tool modules (terrain, objects, water, ...) call MarkDirtySector()/MarkDirtyField() for anything they change
// and push undo steps with PushUndo(). "save" writes every dirty, loaded field with the engine's own
// CNtlWorldFieldManager::SaveField (wfif + wfpf + tool swap files) into the workspace; the Studio then diffs the
// workspace against the cache and exports only the files that really changed as a candidate.
#include <set>
#include <functional>

static bool gEditMode = false;                 // --edit: workspace root, saving allowed
static std::set<int> gDirtyFields;             // field indices changed since the last save
static std::set<int> gEverDirtyFields;         // every field edited in this session (for the audit)
static int gEditGeneration = 0;                // bumps on every edit (Studio shows "unsaved changes")

static int SectorCount() { int n = dGET_WORLD_PARAM()->WorldSectorNum; return n * n; }   // WorldSectorNum is per side
static CNtlWorldFieldManager* FieldMgr() { return gApp.world ? gApp.world->GetWorldFieldMgr() : nullptr; }

static int FieldOfSector(int sectorIdx) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm || sectorIdx < 0) return -1;
    const auto& dp = fm->m_pSectors[sectorIdx].DatumPoint;
    RwV3d p = { dp.x, 0, dp.z };
    return fm->GetFieldIdx(p);
}
static void MarkDirtyField(int fieldIdx) { if (fieldIdx >= 0) { gDirtyFields.insert(fieldIdx); gEverDirtyFields.insert(fieldIdx); ++gEditGeneration; } }
static void MarkDirtySector(int sectorIdx) { MarkDirtyField(FieldOfSector(sectorIdx)); }

// Sectors whose square overlaps the circle (cx,cz,r) and are loaded with geometry.
static std::vector<int> SectorsInRadius(float cx, float cz, float r) {
    std::vector<int> out; CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return out;
    const float s = (float)dGET_WORLD_PARAM()->WorldSectorSize, half = (float)dGET_WORLD_PARAM()->WorldSizeHalf;
    std::set<int> seen;
    for (float z = cz - r - s; z <= cz + r + s; z += s * 0.5f)
        for (float x = cx - r - s; x <= cx + r + s; x += s * 0.5f) {
            if (x < -half || x >= half || z < -half || z >= half) continue;
            RwV3d p = { x, 0, z }; int idx = fm->GetSectorIdx(p);
            if (idx < 0 || !seen.insert(idx).second) continue;
            CNtlWorldSector& sec = fm->m_pSectors[idx];
            if (!sec.m_bLoaded || !sec.m_pAtomic) continue;
            // circle vs sector AABB
            RwSphere bs; (void)bs;
            float minx = sec.DatumPoint.x - s * 0.5f, maxx = sec.DatumPoint.x + s * 0.5f, minz = sec.DatumPoint.z - s * 0.5f, maxz = sec.DatumPoint.z + s * 0.5f;
            float qx = std::max(minx, std::min(cx, maxx)), qz = std::max(minz, std::min(cz, maxz));
            if ((qx - cx) * (qx - cx) + (qz - cz) * (qz - cz) <= r * r + s * s * 0.25f) out.push_back(idx);
        }
    return out;
}

// ---- undo: each step restores what a stroke changed --------------------------------------------------------------
struct UndoStep { std::string label; std::vector<std::function<void()>> restore; std::set<int> fields; std::set<long long> snapshots; };
static std::deque<UndoStep> gUndo;
static UndoStep* gOpenStep = nullptr;
static void BeginStep(const std::string& label) { gUndo.push_back(UndoStep{ label }); if (gUndo.size() > 40) gUndo.pop_front(); gOpenStep = &gUndo.back(); }
static void EndStep() { gOpenStep = nullptr; }
static bool Undo() {
    if (gUndo.empty()) return false;
    UndoStep step = std::move(gUndo.back()); gUndo.pop_back();
    for (auto it = step.restore.rbegin(); it != step.restore.rend(); ++it) (*it)();
    for (int f : step.fields) MarkDirtyField(f);
    return true;
}

// Tool modules whose data lives outside the fields (e.g. the PVS file) register here; SaveDirty calls them after
// writing the dirty fields and they may add their own keys to the "saved" reply.
static std::vector<std::function<void(Value&)>> gSaveHooks;

// ---- save --------------------------------------------------------------------------------------------------------
static Value SaveDirty() {
    Value v = Event("saved");
    Require(gEditMode, "save requires --edit (a workspace copy); the viewer never writes");
    CNtlWorldFieldManager* fm = FieldMgr(); Require(fm != nullptr, "no world");
    Value saved = Value::arr(), skipped = Value::arr();
    for (int f : gDirtyFields) {
        if (fm->IsFieldLoaded(f)) {
            char dir[MAX_PATH]; snprintf(dir, sizeof(dir), "%s\\fields\\wfif%d", dGET_WORLD_PARAM()->WorldProjectFolderName, f);
            CreateDirectoryA((std::string(dGET_WORLD_PARAM()->WorldProjectFolderName) + "\\fields").c_str(), nullptr);
            CreateDirectoryA(dir, nullptr);
            if (fm->SaveField(f)) saved.array.push_back(Value(f)); else skipped.array.push_back(Value(f));
        } else skipped.array.push_back(Value(f));   // already written to the workspace when it left the streaming ring
    }
    _chdir(dGET_WORLD_PARAM()->CurWorkingFolderName);
    for (auto& hook : gSaveHooks) hook(v);
    gDirtyFields.clear();
    Value ever = Value::arr(); for (int f : gEverDirtyFields) ever.array.push_back(Value(f));
    v["saved"] = saved; v["savedOnUnload"] = skipped; v["editedFields"] = ever; v["workspace"] = std::string(dGET_WORLD_PARAM()->WorldProjectFolderName);
    return v;
}
static Value EditState(const char* name = "edit-state") {
    Value v = Event(name);
    v["editMode"] = gEditMode; v["dirty"] = (int)gDirtyFields.size(); v["edited"] = (int)gEverDirtyFields.size();
    v["undo"] = (int)gUndo.size(); v["generation"] = gEditGeneration;
    return v;
}
