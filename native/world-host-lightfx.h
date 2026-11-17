#pragma once
// World Editor host · light, effects and sound (included by world-host.cpp after world-host-edit.h).
//
// Entities the engine already serializes per sector (OutdoorSectorSaveIntoFile, ntlworldsectorinfo.cpp):
//   light objects   PLENTITY_LIGHT_OBJECT  -> m_vecNtlPLEntityLightObject        (InsertIn2World menu 8)
//   world lights    PLENTITY_WORLD_LIGHT   -> m_vecNtlPLEntityWorldLight_Sphere/Box (menu 5)
//   effects         PLENTITY_EFFECT        -> m_vecNtlPLEntityEffect              (menu 1)
//   sounds          PLENTITY_SOUND / _BGM  -> m_vecNtlPLEntitySound / SoundBGM   (menu 2)
// and the field properties eNFP_LIGHT / eNFP_SOUND (wfpf, SetAFieldProp, only on loaded fields).
//
// Creation mirrors the engine loaders (LightObjectLoadFromFile, WorldLightSphere/BoxLoadFromFile, EffectLoadFromFile,
// SoundEffect(BGM)LoadFromFile) so that what SaveField writes reloads identically. Notes:
//  * SectorLightObjectSaveIntoFile only writes m_vecNtlPLEntityLightObject, while InsertIn2World routes non-real
//    lights to the (unsaved) dummy vector. The loader puts every light object in the saved vector, so the host does
//    the same for non-real lights instead of losing them on save.
//  * An effect whose name is not in the property container makes EffectLoadFromFile dereference NULL (client and
//    tool), so effect names are validated against the .eff catalog before anything is created.
//  * Sound names are saved through a 32-byte buffer (strcpy_s): names must be shorter than 32 characters.
//  * The tool dummies (tool\saber\mesh\*.dff, object\mesh\*light*.dff) are not in the paks (R10): gizmos are drawn
//    here with Im3D, like the spawn markers.
#include "NtlPLLightObject.h"
#include "NtlPLWorldLight_Base.h"
#include "NtlPLWorldLight_Sphere.h"
#include "NtlPLWorldLight_Box.h"
#include "NtlPLSound.h"
#include "NtlInstanceEffect.h"
#include "NtlWorldSector.h"
#include "NtlWorldField.h"
#include <map>

enum LfxKind { LK_LIGHT = 0, LK_WL_SPHERE, LK_WL_BOX, LK_EFFECT, LK_SOUND, LK_BGM, LK_COUNT };
static const char* LfxKindName(int k) { static const char* n[] = { "light", "worldlight-sphere", "worldlight-box", "effect", "sound", "bgm" }; return k >= 0 && k < LK_COUNT ? n[k] : "?"; }
static int ParseLfxKind(const std::string& s) {
    for (int k = 0; k < LK_COUNT; ++k) if (s == LfxKindName(k)) return k;
    if (s == "sphere") return LK_WL_SPHERE; if (s == "box") return LK_WL_BOX; if (s == "light-object") return LK_LIGHT;
    return -1;
}
static const char* LfxFamily(int k) { return k <= LK_WL_BOX ? "light" : k == LK_EFFECT ? "effect" : "sound"; }

// Full description of one entity: enough to recreate it (undo of delete) or to report it to the UI.
struct LfxSpec {
    int kind = LK_LIGHT;
    std::string name;                         // effect / sound file name
    RwV3d pos = { 0, 0, 0 }, rot = { 0, 0, 0 }, scale = { 1, 1, 1 };
    RwRGBAReal color = { 1, 1, 1, 1 };        // light object (float) / world light (stored as RwRGBA)
    float radius = 10, angle = 45, volume = 1, rmin = 5, rmax = 30, escale = 1;
    RpLightType ltype = rpLIGHTPOINT;
    bool real = true, decal = false, worldLight = false;
};

struct LfxItem { int kind; CNtlPLEntity* ent; int sector; };

static int gLfxGeneration = 0;                       // bumps on every change (UI refreshes its list)
static CNtlPLEntity* gLfxSelected = nullptr; static int gLfxSelectedKind = -1;
static std::map<CNtlPLEntity*, CNtlPLEntity*> gLfxRemap;   // entity recreated by undo: old pointer -> new
enum LfxTool { LT_NONE, LT_SELECT, LT_PLACE };
static LfxTool gLfxTool = LT_NONE;
static LfxSpec gLfxTemplate;                         // what "place" creates
static bool gLfxShow[3] = { true, true, true };      // gizmos per family: light, effect, sound
static float gLfxGizmoRange = 320.f;

static float Clamp01(double v) { return (float)std::max(0.0, std::min(1.0, v)); }
static Value V3(const RwV3d& v) { Value a = Value::arr(); a.array = { Value((double)v.x), Value((double)v.y), Value((double)v.z) }; return a; }
static RwV3d ReadV3(const Value& cmd, const char* key, RwV3d def) {
    if (!cmd.has(key) || cmd.at(key).array.size() < 3) return def;
    const auto& a = cmd.at(key).array; return { (RwReal)a[0].number, (RwReal)a[1].number, (RwReal)a[2].number };
}
// Colors: [r,g,b] in 0..255 (or 0..1 when every component <= 1) or "#rrggbb".
static bool ReadColor(const Value& cmd, const char* key, RwRGBAReal& out) {
    if (!cmd.has(key)) return false;
    const Value& c = cmd.at(key);
    if (c.type == Value::String) {
        unsigned r = 255, g = 255, b = 255; std::string s = c.string; if (!s.empty() && s[0] == '#') s = s.substr(1);
        Require(s.size() == 6 && sscanf(s.c_str(), "%02x%02x%02x", &r, &g, &b) == 3, "color: #rrggbb");
        out = { r / 255.f, g / 255.f, b / 255.f, 1.f }; return true;
    }
    Require(c.array.size() >= 3, "color: [r,g,b]");
    double r = c.array[0].number, g = c.array[1].number, b = c.array[2].number;
    double k = (r <= 1 && g <= 1 && b <= 1) ? 1.0 : 1.0 / 255.0;
    out = { Clamp01(r * k), Clamp01(g * k), Clamp01(b * k), 1.f }; return true;
}
static Value ColorValue(const RwRGBAReal& c) {
    Value a = Value::arr(); a.array = { Value((int)lroundf(c.red * 255)), Value((int)lroundf(c.green * 255)), Value((int)lroundf(c.blue * 255)) }; return a;
}
static RpLightType ParseLightType(const std::string& s) {
    if (s == "spot") return rpLIGHTSPOT; if (s == "soft" || s == "spotsoft" || s == "softspot") return rpLIGHTSPOTSOFT;
    if (s == "directional") return rpLIGHTDIRECTIONAL; return rpLIGHTPOINT;
}
static const char* LightTypeName(RpLightType t) {
    switch (t) { case rpLIGHTSPOT: return "spot"; case rpLIGHTSPOTSOFT: return "soft"; case rpLIGHTDIRECTIONAL: return "directional"; case rpLIGHTAMBIENT: return "ambient"; default: return "point"; }
}

// ---- sector bookkeeping ------------------------------------------------------------------------------------------
static std::vector<CNtlPLEntity*>* LfxVector(CNtlWorldSector& s, int kind, int which = 0) {
    switch (kind) {
    case LK_LIGHT: return which ? &s.m_vecNtlPLEntityLightObjectDummy : &s.m_vecNtlPLEntityLightObject;
    case LK_WL_SPHERE: return &s.m_vecNtlPLEntityWorldLight_Sphere;
    case LK_WL_BOX: return &s.m_vecNtlPLEntityWorldLight_Box;
    case LK_EFFECT: return &s.m_vecNtlPLEntityEffect;
    case LK_SOUND: return &s.m_vecNtlPLEntitySound;
    case LK_BGM: return &s.m_vecNtlPLEntitySoundBGM;
    }
    return nullptr;
}
static int MenuOf(int kind) { return kind == LK_LIGHT ? 8 : kind <= LK_WL_BOX ? 5 : kind == LK_EFFECT ? 1 : 2; }

template <class F> static void ForEachLfx(F f, bool nearOnly = false, float cx = 0, float cz = 0, float range = 0) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return;
    auto visit = [&](int idx) {
        CNtlWorldSector& s = fm->m_pSectors[idx]; if (!s.m_bLoaded) return;
        for (int k = 0; k < LK_COUNT; ++k)
            for (int w = 0; w < (k == LK_LIGHT ? 2 : 1); ++w)
                for (CNtlPLEntity* e : *LfxVector(s, k, w)) if (e) f(LfxItem{ k, e, idx });
    };
    if (nearOnly) { for (int idx : SectorsInRadius(cx, cz, range)) visit(idx); }
    else { int n = SectorCount(); for (int i = 0; i < n; ++i) visit(i); }
}
static CNtlPLEntity* Resolve(CNtlPLEntity* p) { for (int i = 0; i < 64; ++i) { auto it = gLfxRemap.find(p); if (it == gLfxRemap.end()) break; p = it->second; } return p; }
static bool FindLfx(CNtlPLEntity* p, LfxItem& out) {
    p = Resolve(p); bool found = false;
    ForEachLfx([&](const LfxItem& it) { if (!found && it.ent == p) { out = it; found = true; } });
    return found;
}

static RwV3d LfxPosition(const LfxItem& it) {
    switch (it.kind) {
    case LK_LIGHT: return static_cast<CNtlPLLightObject*>(it.ent)->GetPosition();
    case LK_WL_SPHERE: case LK_WL_BOX: return static_cast<CNtlPLWorldLight_Base*>(it.ent)->GetPosition();
    case LK_EFFECT: return static_cast<CNtlInstanceEffect*>(it.ent)->GetPosition();
    default: return static_cast<CNtlPLSound*>(it.ent)->GetPosition();
    }
}
static LfxSpec Describe(const LfxItem& it) {
    LfxSpec s; s.kind = it.kind; s.pos = LfxPosition(it);
    switch (it.kind) {
    case LK_LIGHT: {
        auto* l = static_cast<CNtlPLLightObject*>(it.ent);
        s.ltype = l->GetLightType(); s.real = l->IsRealLight() != FALSE; s.rot = *l->GetRotate(); s.color = l->GetColor();
        s.radius = l->GetRadius(); s.angle = l->GetAngle(); s.decal = l->IsEnabledDecal() != FALSE; s.worldLight = l->IsEnabledWorldLight() != FALSE;
        break; }
    case LK_WL_SPHERE: case LK_WL_BOX: {
        auto* w = static_cast<CNtlPLWorldLight_Base*>(it.ent);
        RwRGBA c = w->GetColor(); s.color = { c.red / 255.f, c.green / 255.f, c.blue / 255.f, 1.f };
        if (it.kind == LK_WL_SPHERE) s.radius = w->GetRadius();
        else { auto* b = static_cast<CNtlPLWorldLight_Box*>(it.ent); s.rot = *b->GetRotate(); s.scale = *b->GetScaleVector(); }
        break; }
    case LK_EFFECT: {
        auto* e = static_cast<CNtlInstanceEffect*>(it.ent);
        s.name = e->GetName() ? e->GetName() : ""; s.rot = *e->GetRotate(); s.escale = e->GetScale();
        break; }
    case LK_SOUND: {
        auto* snd = static_cast<CNtlPLSound*>(it.ent);
        s.name = snd->GetName(); s.volume = snd->GetVolume(); s.rmin = snd->GetRadius(TRUE); s.rmax = snd->GetRadius(FALSE);
        break; }
    case LK_BGM: {
        auto* b = static_cast<CNtlPLSoundBGM*>(it.ent);
        s.name = b->GetName(); s.radius = b->GetRadius(); s.volume = b->GetVolume();
        break; }
    }
    return s;
}
static Value ItemValue(const LfxItem& it) {
    LfxSpec s = Describe(it);
    Value v = Value::obj();
    v["ref"] = (double)(uintptr_t)it.ent; v["kind"] = LfxKindName(it.kind); v["family"] = LfxFamily(it.kind);
    v["sector"] = it.sector; v["field"] = FieldOfSector(it.sector); v["pos"] = V3(s.pos);
    switch (it.kind) {
    case LK_LIGHT: v["type"] = LightTypeName(s.ltype); v["real"] = s.real; v["rot"] = V3(s.rot); v["color"] = ColorValue(s.color);
        v["radius"] = (double)s.radius; v["angle"] = (double)s.angle; v["decal"] = s.decal; v["worldLight"] = s.worldLight; break;
    case LK_WL_SPHERE: v["color"] = ColorValue(s.color); v["radius"] = (double)s.radius; break;
    case LK_WL_BOX: v["color"] = ColorValue(s.color); v["rot"] = V3(s.rot); v["scale"] = V3(s.scale); break;
    case LK_EFFECT: v["name"] = s.name; v["rot"] = V3(s.rot); v["scale"] = (double)s.escale; break;
    case LK_SOUND: v["name"] = s.name; v["volume"] = (double)s.volume; v["min"] = (double)s.rmin; v["max"] = (double)s.rmax; break;
    case LK_BGM: v["name"] = s.name; v["radius"] = (double)s.radius; break;
    }
    v["selected"] = it.ent == gLfxSelected;
    return v;
}

// ---- effect catalog ----------------------------------------------------------------------------------------------
// LoadSerialize without a pack does not read the .eff files (only Load() does). The effect properties are loaded on
// demand from the cache's effect\visualeffect_0N.eff, exactly the list CNtlPLPropertyContainer::Load uses.
struct PropertyAccess : CNtlPLPropertyContainer {
    static RwBool LoadEff(CNtlPLPropertyContainer* c, const char* f) { RwBool (CNtlPLPropertyContainer::*fn)(const RwChar*) = &PropertyAccess::LoadEffect; return (c->*fn)(f); }
};
static int gEffectFilesLoaded = -1;
static CNtlPLPropertyContainer::MapProp& EffectMap() {
    auto* pc = CNtlPLPropertyContainer::GetInstance();
    auto& m = pc->Gets(PLENTITY_EFFECT);
    if (gEffectFilesLoaded < 0) {
        gEffectFilesLoaded = 0;
        if (m.empty()) {
            char cwd[MAX_PATH]; _getcwd(cwd, MAX_PATH);
            _chdir(dGET_WORLD_PARAM()->CurWorkingFolderName);
            for (const char* f : { ".\\Effect\\VisualEffect_01.eff", ".\\Effect\\VisualEffect_02.eff", ".\\Effect\\VisualEffect_03.eff", ".\\Effect\\VisualEffect_04.eff", ".\\Effect\\VisualEffect_05.eff" }) {
                FILE* t = fopen(f, "rb"); if (!t) continue; fclose(t);
                if (PropertyAccess::LoadEff(pc, f)) ++gEffectFilesLoaded;
            }
            _chdir(cwd);
        }
    }
    return m;
}
static bool EffectExists(const std::string& name) { auto& m = EffectMap(); return m.find(name) != m.end(); }

// ---- create / destroy --------------------------------------------------------------------------------------------
static CNtlPLEntity* CreateLfx(const LfxSpec& s) {
    RwV3d pos = s.pos;
    switch (s.kind) {
    case LK_LIGHT: {
        SPLLightObjectCreateParam p; p.pPos = &pos; p.rpLightType = s.ltype; p.bRealLight = s.real; p.vRotate = s.rot; p.rgbafColor = s.color;
        p.fRadius = s.radius; p.fAngle = s.angle; p.bDecal = s.decal; p.bWorldLight = s.worldLight;
        return GetSceneManager()->CreateEntity(PLENTITY_LIGHT_OBJECT, "NULL", &p); }
    case LK_WL_SPHERE: {
        EPLWorldLightType t = EPLWORLDLIGHT_SPHERE; SPLWorldLightCreateParam_Sphere p; p.pPos = &pos; p._pUserData = &t;
        p.uiRed = (RwUInt8)lroundf(s.color.red * 255); p.uiGreen = (RwUInt8)lroundf(s.color.green * 255); p.uiBlue = (RwUInt8)lroundf(s.color.blue * 255); p.fRadius = s.radius;
        return GetSceneManager()->CreateEntity(PLENTITY_WORLD_LIGHT, "NULL", &p); }
    case LK_WL_BOX: {
        EPLWorldLightType t = EPLWORLDLIGHT_BOX; SPLWorldLightCreateParam_Box p; p.pPos = &pos; p._pUserData = &t;
        p.uiRed = (RwUInt8)lroundf(s.color.red * 255); p.uiGreen = (RwUInt8)lroundf(s.color.green * 255); p.uiBlue = (RwUInt8)lroundf(s.color.blue * 255);
        p.vScale = s.scale; p.vRotate = s.rot;
        return GetSceneManager()->CreateEntity(PLENTITY_WORLD_LIGHT, "NULL", &p); }
    case LK_EFFECT: {
        Require(EffectExists(s.name), "effect name not in the .eff catalog (it would break the sector load)");
        SPLEntityCreateParam p; p.pPos = &pos;
        auto* e = static_cast<CNtlInstanceEffect*>(GetSceneManager()->CreateEntity(PLENTITY_EFFECT, s.name.c_str(), &p));
        if (!e) return nullptr;
        e->SetScale(s.escale); e->SetRotate(s.rot.x, s.rot.y, s.rot.z); e->SetPosition(&pos);
        return e; }
    case LK_SOUND: {
        SPLEntityCreateParam p; p.pPos = &pos;
        auto* snd = static_cast<CNtlPLSound*>(GetSceneManager()->CreateEntity(PLENTITY_SOUND, "NULL", &p));
        if (!snd) return nullptr;
        snd->GetVolume() = s.volume; snd->GetRadius(TRUE) = s.rmin; snd->GetRadius(FALSE) = s.rmax;
        std::vector<char> name(s.name.begin(), s.name.end()); name.push_back(0); snd->PlaySound(name.data());
        return snd; }
    case LK_BGM: {
        SPLEntityCreateParam p; p.pPos = &pos;
        auto* b = static_cast<CNtlPLSoundBGM*>(GetSceneManager()->CreateEntity(PLENTITY_SOUND_BGM, "NULL", &p));
        if (!b) return nullptr;
        b->GetRadius() = s.radius;
        std::vector<char> name(s.name.begin(), s.name.end()); name.push_back(0); b->PlaySound(name.data());
        return b; }
    }
    return nullptr;
}
static void InsertLfx(CNtlWorldSector& sec, int kind, CNtlPLEntity* e) {
    if (kind == LK_LIGHT && !static_cast<CNtlPLLightObject*>(e)->IsRealLight()) sec.m_vecNtlPLEntityLightObject.push_back(e);   // see header note
    else sec.InsertIn2World(e, MenuOf(kind));
}
static int SectorAt(const RwV3d& p) {
    CNtlWorldFieldManager* fm = FieldMgr(); if (!fm) return -1;
    RwV3d q = p; int idx = fm->GetSectorIdx(q);
    if (idx < 0 || !fm->m_pSectors[idx].m_bLoaded) return -1;
    return idx;
}
static void ValidateSpec(const LfxSpec& s) {
    Require(s.kind >= 0 && s.kind < LK_COUNT, "unknown kind");
    const float half = (float)dGET_WORLD_PARAM()->WorldSizeHalf;
    Require(fabsf(s.pos.x) < half && fabsf(s.pos.z) < half, "position outside the world");
    if (s.kind == LK_EFFECT) { Require(!s.name.empty() && s.name.size() < 128, "effect name required (< 128 chars)"); Require(s.escale > 0.001f && s.escale < 1000.f, "effect scale"); }
    if (s.kind == LK_SOUND || s.kind == LK_BGM) Require(!s.name.empty() && s.name.size() < 32, "sound name required (< 32 chars: saved through a 32-byte buffer)");
    if (s.kind == LK_SOUND) Require(s.rmin >= 0 && s.rmax >= s.rmin, "sound radius: 0 <= min <= max");
    if (s.kind == LK_LIGHT || s.kind == LK_WL_SPHERE || s.kind == LK_BGM) Require(s.radius > 0 && s.radius < 4096, "radius");
    if (s.kind == LK_WL_BOX) Require(s.scale.x > 0 && s.scale.y > 0 && s.scale.z > 0, "box scale > 0");
}
// Adds to the sector under the position. Returns the entity (nullptr when the engine refused it).
static CNtlPLEntity* AddLfx(const LfxSpec& s, int* sectorOut = nullptr) {
    ValidateSpec(s);
    int idx = SectorAt(s.pos); Require(idx >= 0, "position is not on a loaded sector (move the camera there first)");
    CNtlPLEntity* e = CreateLfx(s); Require(e != nullptr, "the engine could not create the entity");
    InsertLfx(FieldMgr()->m_pSectors[idx], s.kind, e);
    MarkDirtySector(idx); ++gLfxGeneration;
    if (sectorOut) *sectorOut = idx;
    return e;
}
static void DestroyLfx(const LfxItem& it) {
    CNtlWorldSector& sec = FieldMgr()->m_pSectors[it.sector];
    sec.DeleteEntity(it.ent, MenuOf(it.kind));
    if (gLfxSelected == it.ent) { gLfxSelected = nullptr; gLfxSelectedKind = -1; }
    GetSceneManager()->DeleteEntity(it.ent);
    MarkDirtySector(it.sector); ++gLfxGeneration;
}

// ---- edits with undo -----------------------------------------------------------------------------------------------
static CNtlPLEntity* EditAdd(const LfxSpec& s) {
    BeginStep(std::string("add ") + LfxKindName(s.kind));
    int idx = -1; CNtlPLEntity* e = nullptr;
    try { e = AddLfx(s, &idx); } catch (...) { gUndo.pop_back(); gOpenStep = nullptr; throw; }
    gOpenStep->fields.insert(FieldOfSector(idx));
    gOpenStep->restore.push_back([e]() { LfxItem it; if (FindLfx(e, it)) DestroyLfx(it); });
    EndStep();
    gLfxSelected = e; gLfxSelectedKind = s.kind;
    return e;
}
static void EditDelete(const LfxItem& it) {
    LfxSpec s = Describe(it); CNtlPLEntity* old = it.ent;
    BeginStep(std::string("delete ") + LfxKindName(it.kind));
    gOpenStep->fields.insert(FieldOfSector(it.sector));
    gOpenStep->restore.push_back([s, old]() {
        CNtlPLEntity* e = nullptr; try { e = AddLfx(s); } catch (...) { return; }
        gLfxRemap[old] = e;
    });
    DestroyLfx(it);
    EndStep();
}
// Applies a full spec to an existing entity (position may move it to another sector: delete + insert, R4).
static void ApplySpec(LfxItem& it, const LfxSpec& s) {
    ValidateSpec(s);
    CNtlWorldFieldManager* fm = FieldMgr();
    int dst = SectorAt(s.pos); Require(dst >= 0, "destination is not on a loaded sector");
    RwV3d pos = s.pos;
    switch (it.kind) {
    case LK_LIGHT: {
        auto* l = static_cast<CNtlPLLightObject*>(it.ent);
        // light type, decal and world-light flags are fixed at creation: recreate when they change
        if (l->GetLightType() != s.ltype || (l->IsEnabledDecal() != FALSE) != s.decal || (l->IsRealLight() != FALSE) != s.real || (l->IsEnabledWorldLight() != FALSE) != s.worldLight) {
            CNtlPLEntity* old = it.ent; int oldSector = it.sector;
            CNtlPLEntity* e = CreateLfx(s); Require(e != nullptr, "the engine could not create the light");
            fm->m_pSectors[oldSector].DeleteEntity(old, 8); GetSceneManager()->DeleteEntity(old);
            InsertLfx(fm->m_pSectors[dst], LK_LIGHT, e);
            gLfxRemap[old] = e; if (gLfxSelected == old) gLfxSelected = e;
            MarkDirtySector(oldSector); MarkDirtySector(dst); ++gLfxGeneration;
            it.ent = e; it.sector = dst; return;
        }
        l->SetPosition(&pos); l->SetRotate(s.rot.x, s.rot.y, s.rot.z);
        RwRGBAReal c = s.color; l->SetColor(c); l->SetRadius(s.radius); l->SetAngle(s.angle);
        break; }
    case LK_WL_SPHERE: {
        auto* w = static_cast<CNtlPLWorldLight_Base*>(it.ent);
        w->SetPosition(&pos); w->SetRadius(s.radius);
        w->SetColor((RwUInt8)lroundf(s.color.red * 255), (RwUInt8)lroundf(s.color.green * 255), (RwUInt8)lroundf(s.color.blue * 255));
        break; }
    case LK_WL_BOX: {
        auto* b = static_cast<CNtlPLWorldLight_Box*>(it.ent);
        RwV3d rot = s.rot, sc = s.scale; b->SetRotate(&rot); b->SetScaleVector(&sc); b->SetPosition(&pos);
        b->SetColor((RwUInt8)lroundf(s.color.red * 255), (RwUInt8)lroundf(s.color.green * 255), (RwUInt8)lroundf(s.color.blue * 255));
        break; }
    case LK_EFFECT: {
        auto* e = static_cast<CNtlInstanceEffect*>(it.ent);
        Require(s.name == (e->GetName() ? e->GetName() : ""), "an effect's name cannot change: delete it and add another");
        e->SetScale(s.escale); e->SetRotate(s.rot.x, s.rot.y, s.rot.z); e->SetPosition(&pos);
        break; }
    case LK_SOUND: case LK_BGM: {
        auto* snd = static_cast<CNtlPLSound*>(it.ent);
        Require(s.name == snd->GetName(), "a sound's file cannot change: delete it and add another");
        snd->SetPosition(&pos);
        // The engine's setters are relative (ControlVolume/ControlRadius*(append), they also restart the channel and
        // clamp min radius to one sector); the loader assigns the stored values through the same references.
        if (it.kind == LK_SOUND) { snd->GetVolume() = Clamp01(s.volume); snd->GetRadius(TRUE) = s.rmin; snd->GetRadius(FALSE) = s.rmax; }
        else static_cast<CNtlPLSoundBGM*>(snd)->GetRadius() = s.radius;
        break; }
    }
    if (dst != it.sector) {   // re-bucket into the sector that now contains it
        fm->m_pSectors[it.sector].DeleteEntity(it.ent, MenuOf(it.kind));
        InsertLfx(fm->m_pSectors[dst], it.kind, it.ent);
        MarkDirtySector(it.sector); it.sector = dst;
    }
    MarkDirtySector(dst); ++gLfxGeneration;
}
static void EditSet(LfxItem& it, const LfxSpec& s, const char* label) {
    LfxSpec before = Describe(it); CNtlPLEntity* handle = it.ent; int s0 = it.sector;
    BeginStep(label);
    try { ApplySpec(it, s); } catch (...) { gUndo.pop_back(); gOpenStep = nullptr; throw; }
    gOpenStep->fields.insert(FieldOfSector(s0)); gOpenStep->fields.insert(FieldOfSector(it.sector));
    gOpenStep->restore.push_back([handle, before]() { LfxItem cur; if (FindLfx(handle, cur)) try { ApplySpec(cur, before); } catch (...) {} });
    EndStep();
}

// Reads the editable fields of a command over a base spec (add: template/defaults, set: current values).
static LfxSpec SpecFromCommand(const Value& cmd, LfxSpec s) {
    if (cmd.has("kind")) { int k = ParseLfxKind(cmd.text("kind")); Require(k >= 0, "kind: light | worldlight-sphere | worldlight-box | effect | sound | bgm"); s.kind = k; }
    if (cmd.has("x")) s.pos.x = (RwReal)cmd.num("x", s.pos.x);
    if (cmd.has("z")) s.pos.z = (RwReal)cmd.num("z", s.pos.z);
    if (cmd.has("y")) s.pos.y = (RwReal)cmd.num("y", s.pos.y);
    if (cmd.has("pos")) s.pos = ReadV3(cmd, "pos", s.pos);
    if (cmd.flag("ground", false) || (cmd.has("height") && !cmd.has("y"))) {
        RwV3d q = s.pos; if (FieldMgr() && FieldMgr()->GetHeight(q)) s.pos.y = q.y + (RwReal)cmd.num("height", 0);
    }
    if (cmd.has("rot")) s.rot = ReadV3(cmd, "rot", s.rot);
    if (cmd.has("name")) s.name = cmd.text("name");
    if (cmd.has("type")) s.ltype = ParseLightType(cmd.text("type"));
    if (cmd.has("real")) s.real = cmd.flag("real", s.real);
    if (cmd.has("decal")) s.decal = cmd.flag("decal", s.decal);
    if (cmd.has("worldLight")) s.worldLight = cmd.flag("worldLight", s.worldLight);
    ReadColor(cmd, "color", s.color);
    if (cmd.has("radius")) s.radius = (float)cmd.num("radius", s.radius);
    if (cmd.has("angle")) s.angle = (float)cmd.num("angle", s.angle);
    if (cmd.has("volume")) s.volume = Clamp01(cmd.num("volume", s.volume));
    if (cmd.has("min")) s.rmin = (float)cmd.num("min", s.rmin);
    if (cmd.has("max")) s.rmax = (float)cmd.num("max", s.rmax);
    if (cmd.has("scale")) {
        const Value& sc = cmd.at("scale");
        if (sc.type == Value::Array) s.scale = ReadV3(cmd, "scale", s.scale);
        else { float f = (float)sc.number; s.escale = f; if (s.kind == LK_WL_BOX) s.scale = { f, f, f }; }
    }
    return s;
}
static LfxSpec DefaultSpec(int kind) {
    LfxSpec s; s.kind = kind;
    switch (kind) {
    case LK_LIGHT: s.radius = 12; s.angle = 45; s.color = { 1.f, 0.85f, 0.6f, 1.f }; break;
    case LK_WL_SPHERE: s.radius = 20; s.color = { 1.f, 0.9f, 0.7f, 1.f }; break;
    case LK_WL_BOX: s.scale = { 20, 10, 20 }; s.color = { 0.7f, 0.8f, 1.f, 1.f }; break;
    case LK_SOUND: s.volume = 0.8f; s.rmin = 8; s.rmax = 40; break;
    case LK_BGM: s.radius = 60; break;
    }
    return s;
}

// ---- picking + gizmos ----------------------------------------------------------------------------------------------
static bool FamilyShown(int kind) { return gLfxShow[kind <= LK_WL_BOX ? 0 : kind == LK_EFFECT ? 1 : 2]; }
static bool PickLfx(int x, int y, LfxItem& out, int familyMask = 7) {
    RwCamera* cam = ActiveCamera(); RwV2d px = { (RwReal)x, (RwReal)y }; RwLine ray;
    if (!RwCameraCalcPixelRay(cam, &ray, &px)) return false;
    RwV3d d = { ray.end.x - ray.start.x, ray.end.y - ray.start.y, ray.end.z - ray.start.z };
    float len = sqrtf(d.x * d.x + d.y * d.y + d.z * d.z); if (len <= 0) return false; d.x /= len; d.y /= len; d.z /= len;
    float best = 1e30f; bool found = false;
    ForEachLfx([&](const LfxItem& it) {
        int fam = it.kind <= LK_WL_BOX ? 1 : it.kind == LK_EFFECT ? 2 : 4;
        if (!(familyMask & fam) || !FamilyShown(it.kind)) return;
        RwV3d p = LfxPosition(it);
        RwV3d w = { p.x - ray.start.x, p.y - ray.start.y, p.z - ray.start.z };
        float t = w.x * d.x + w.y * d.y + w.z * d.z; if (t < 0) return;
        RwV3d c = { w.x - d.x * t, w.y - d.y * t, w.z - d.z * t };
        float dist = sqrtf(c.x * c.x + c.y * c.y + c.z * c.z), tol = 0.9f + t * 0.015f;
        if (dist < tol && t < best) { best = t; out = it; found = true; }
    }, true, gPos.x, gPos.z, gLfxGizmoRange);
    return found;
}

static std::vector<RwIm3DVertex> gLfxVerts;
static void LfxLine(const RwV3d& a, const RwV3d& b, RwRGBA c) {
    RwIm3DVertex v0, v1;
    RwIm3DVertexSetPos(&v0, a.x, a.y, a.z); RwIm3DVertexSetRGBA(&v0, c.red, c.green, c.blue, c.alpha);
    RwIm3DVertexSetPos(&v1, b.x, b.y, b.z); RwIm3DVertexSetRGBA(&v1, c.red, c.green, c.blue, c.alpha);
    gLfxVerts.push_back(v0); gLfxVerts.push_back(v1);
}
static void LfxCircle(const RwV3d& o, float r, int axis, RwRGBA c, int seg = 32) {   // axis: 0 = XZ, 1 = XY, 2 = YZ
    RwV3d prev = o;
    for (int i = 0; i <= seg; ++i) {
        float a = 2 * 3.14159265f * i / seg, ca = cosf(a) * r, sa = sinf(a) * r;
        RwV3d p = axis == 0 ? RwV3d{ o.x + ca, o.y, o.z + sa } : axis == 1 ? RwV3d{ o.x + ca, o.y + sa, o.z } : RwV3d{ o.x, o.y + ca, o.z + sa };
        if (i) LfxLine(prev, p, c); prev = p;
    }
}
static RwV3d RotateDeg(RwV3d v, const RwV3d& rotDeg) {   // X, then Y, then Z (degrees), like RwMatrixRotate in SetRotate
    float ax = rotDeg.x * kDeg, ay = rotDeg.y * kDeg, az = rotDeg.z * kDeg;
    RwV3d r = { v.x, v.y * cosf(ax) - v.z * sinf(ax), v.y * sinf(ax) + v.z * cosf(ax) }; v = r;
    r = { v.x * cosf(ay) + v.z * sinf(ay), v.y, -v.x * sinf(ay) + v.z * cosf(ay) }; v = r;
    r = { v.x * cosf(az) - v.y * sinf(az), v.x * sinf(az) + v.y * cosf(az), v.z }; return r;
}
static RwRGBA ToRGBA(const RwRGBAReal& c) { return RwRGBA{ (RwUInt8)lroundf(Clamp01(c.red) * 255), (RwUInt8)lroundf(Clamp01(c.green) * 255), (RwUInt8)lroundf(Clamp01(c.blue) * 255), 255 }; }

static void RenderLightFxGizmos() {
    if (!gApp.world || !FieldMgr()) return;
    gLfxVerts.clear();
    const RwRGBA sel = { 80, 240, 255, 255 };
    RwCamera* cam = ActiveCamera(); RwV3d* eye = RwMatrixGetPos(RwFrameGetLTM(RwCameraGetFrame(cam)));
    ForEachLfx([&](const LfxItem& it) {
        if (!FamilyShown(it.kind)) return;
        LfxSpec s = Describe(it); RwV3d p = s.pos;
        float dx = p.x - eye->x, dy = p.y - eye->y, dz = p.z - eye->z, dist = sqrtf(dx * dx + dy * dy + dz * dz);
        float k = std::max(1.f, dist / 50.f); bool on = it.ent == gLfxSelected;
        auto cross = [&](float h, RwRGBA c) {
            LfxLine({ p.x - h, p.y, p.z }, { p.x + h, p.y, p.z }, c); LfxLine({ p.x, p.y - h, p.z }, { p.x, p.y + h, p.z }, c); LfxLine({ p.x, p.y, p.z - h }, { p.x, p.y, p.z + h }, c);
        };
        auto stem = [&](RwRGBA c) { RwV3d g = p; if (FieldMgr()->GetHeight(g) && g.y < p.y) LfxLine(g, p, RwRGBA{ c.red, c.green, c.blue, 120 }); };
        switch (it.kind) {
        case LK_LIGHT: {
            RwRGBA c = on ? sel : ToRGBA(s.color);
            cross(1.2f * k, c); stem(c);
            LfxCircle(p, 0.6f * k, 0, c, 12); LfxCircle(p, 0.6f * k, 1, c, 12);
            if (s.ltype == rpLIGHTSPOT || s.ltype == rpLIGHTSPOTSOFT || s.ltype == rpLIGHTDIRECTIONAL) {
                RwV3d at = RotateDeg({ 0, 0, 1 }, s.rot); float L = s.ltype == rpLIGHTDIRECTIONAL ? 6.f * k : s.radius;
                RwV3d tip = { p.x + at.x * L, p.y + at.y * L, p.z + at.z * L }; LfxLine(p, tip, c);
                if (s.ltype != rpLIGHTDIRECTIONAL) {   // cone outline
                    float r = L * tanf(std::min(89.f, s.angle) * kDeg * 0.5f);
                    RwV3d u = RotateDeg({ 1, 0, 0 }, s.rot), v = RotateDeg({ 0, 1, 0 }, s.rot);
                    RwV3d prev = tip;
                    for (int i = 0; i <= 16; ++i) {
                        float a = 2 * 3.14159265f * i / 16; RwV3d q = { tip.x + (u.x * cosf(a) + v.x * sinf(a)) * r, tip.y + (u.y * cosf(a) + v.y * sinf(a)) * r, tip.z + (u.z * cosf(a) + v.z * sinf(a)) * r };
                        if (i) LfxLine(prev, q, c); if (i % 4 == 0) LfxLine(p, q, c); prev = q;
                    }
                }
            } else if (on || dist < 120) LfxCircle(p, s.radius, 0, RwRGBA{ c.red, c.green, c.blue, 160 }, 40);
            break; }
        case LK_WL_SPHERE: {
            RwRGBA c = on ? sel : ToRGBA(s.color);
            cross(0.8f * k, c); LfxCircle(p, s.radius, 0, c, 48); LfxCircle(p, s.radius, 1, c, 48); LfxCircle(p, s.radius, 2, c, 48);
            break; }
        case LK_WL_BOX: {
            RwRGBA c = on ? sel : ToRGBA(s.color);
            RwV3d h = { s.scale.x * 0.5f, s.scale.y * 0.5f, s.scale.z * 0.5f }, corner[8];
            for (int i = 0; i < 8; ++i) {
                RwV3d o = RotateDeg({ (i & 1 ? h.x : -h.x), (i & 2 ? h.y : -h.y), (i & 4 ? h.z : -h.z) }, s.rot);
                corner[i] = { p.x + o.x, p.y + o.y, p.z + o.z };
            }
            static const int e[12][2] = { {0,1},{2,3},{4,5},{6,7},{0,2},{1,3},{4,6},{5,7},{0,4},{1,5},{2,6},{3,7} };
            for (auto& ed : e) LfxLine(corner[ed[0]], corner[ed[1]], c);
            cross(0.8f * k, c);
            break; }
        case LK_EFFECT: {
            RwRGBA c = on ? sel : RwRGBA{ 255, 90, 230, 255 };
            float h = 1.0f * k;
            RwV3d t = { p.x, p.y + h, p.z }, b = { p.x, p.y - h, p.z }, q[4] = { { p.x + h, p.y, p.z }, { p.x, p.y, p.z + h }, { p.x - h, p.y, p.z }, { p.x, p.y, p.z - h } };
            for (int i = 0; i < 4; ++i) { LfxLine(t, q[i], c); LfxLine(b, q[i], c); LfxLine(q[i], q[(i + 1) % 4], c); }
            stem(c);
            break; }
        case LK_SOUND: case LK_BGM: {
            RwRGBA c = on ? sel : (it.kind == LK_SOUND ? RwRGBA{ 110, 255, 120, 255 } : RwRGBA{ 120, 150, 255, 255 });
            cross(0.9f * k, c); stem(c);
            if (it.kind == LK_SOUND) { if (on || dist < 160) { LfxCircle(p, s.rmin, 0, c, 32); LfxCircle(p, s.rmax, 0, RwRGBA{ c.red, c.green, c.blue, 140 }, 48); } }
            else if (on || dist < 200) LfxCircle(p, s.radius, 0, c, 48);
            break; }
        }
    }, true, gPos.x, gPos.z, gLfxGizmoRange);
    if (gLfxVerts.empty()) return;
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);   // gizmos stay visible through terrain and props
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEVERTEXALPHAENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATEFOGENABLE, (void*)FALSE);
    for (size_t i = 0; i < gLfxVerts.size(); i += 2000) {
        RwInt32 n = (RwInt32)std::min<size_t>(2000, gLfxVerts.size() - i);
        if (RwIm3DTransform(&gLfxVerts[i], n, nullptr, rwIM3D_ALLOPAQUE)) { RwIm3DRenderPrimitive(rwPRIMTYPELINELIST); RwIm3DEnd(); }
    }
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)TRUE);
}

// Mouse: with a light/effect/sound tool active (and no terrain brush) a left click is ours.
//   select: pick a gizmo · shift+click on terrain moves the selection there
//   place:  creates the template on the clicked terrain point
static bool LightFxClick(int x, int y, bool shift) {
    if (gLfxTool == LT_NONE || gTool != TT_NONE) return false;
    Value v = Event("lfx-pick"); v["tool"] = gLfxTool == LT_PLACE ? "place" : "select";
    try {
        RwV3d hit; bool terrain = FieldMgr() && FieldMgr()->PickTerrain(x, y, hit);
        if (gLfxTool == LT_PLACE) {
            Require(gEditMode, "placing needs --edit (workspace)");
            Require(terrain, "click on the terrain to place");
            LfxSpec s = gLfxTemplate; s.pos = hit; s.pos.y += s.kind == LK_EFFECT ? 0.f : 2.f;
            CNtlPLEntity* e = EditAdd(s); LfxItem it; if (FindLfx(e, it)) v["item"] = ItemValue(it);
        } else if (shift && gLfxSelected && terrain) {
            Require(gEditMode, "moving needs --edit (workspace)");
            LfxItem it; Require(FindLfx(gLfxSelected, it), "selection is gone (sector unloaded)");
            LfxSpec s = Describe(it); float lift = s.pos.y - [&] { RwV3d g = s.pos; return FieldMgr()->GetHeight(g) ? (float)g.y : (float)s.pos.y; }();
            s.pos = hit; s.pos.y += std::max(0.f, lift);
            EditSet(it, s, "move"); v["item"] = ItemValue(it); v["moved"] = true;
        } else {
            LfxItem it;
            if (PickLfx(x, y, it)) { gLfxSelected = it.ent; gLfxSelectedKind = it.kind; v["item"] = ItemValue(it); }
            else { gLfxSelected = nullptr; gLfxSelectedKind = -1; }
            ++gLfxGeneration;
        }
        if (terrain) v["terrain"] = V3(hit);
    } catch (const std::exception& e) { v["event"] = "error"; v["message"] = e.what(); }
    Emit(v);
    return true;
}

static void LightFxStats(Value& v) {
    Value l = Value::obj();
    l["tool"] = gLfxTool == LT_PLACE ? "place" : gLfxTool == LT_SELECT ? "select" : "none";
    l["place"] = LfxKindName(gLfxTemplate.kind); l["generation"] = gLfxGeneration;
    LfxItem it; if (gLfxSelected && FindLfx(gLfxSelected, it)) l["selected"] = ItemValue(it);
    v["lfx"] = l;
}

// ---- field properties: eNFP_LIGHT / eNFP_SOUND --------------------------------------------------------------------
static int LoadedFieldAt(const Value& cmd, RwV3d& pos) {
    CNtlWorldFieldManager* fm = FieldMgr(); Require(fm != nullptr, "no world");
    pos = { (RwReal)cmd.num("x", gPos.x), 0, (RwReal)cmd.num("z", gPos.z) };
    int f = fm->GetFieldIdx(pos); Require(f >= 0, "position outside the world");
    Require(fm->IsFieldLoaded(f) != FALSE, "the field is not loaded (move the camera there first)");
    return f;
}
static Value FieldLightValue(const sNTL_FIELD_PROP& p) {
    Value v = Value::obj();
    v["ambient"] = ColorValue(p._ClrLightAmbient);
    Value d = Value::arr();
    for (int i = 0; i < dNTL_PL_DIRECTIONAL_LIGHT_NUM; ++i) { Value e = Value::obj(); e["color"] = ColorValue(p._ClrLightDirectional[i]); e["angle"] = V3(p._AngLightDirectional[i]); d.array.push_back(e); }
    v["directional"] = d; return v;
}
static Value FieldSoundValue(const sNTL_FIELD_PROP& p) {
    Value v = Value::obj(); v["bgm"] = p._IdxBGMTbl; v["env"] = p._IdxEnvTbl;
    Value s = Value::arr(); for (int i = 0; i < 5; ++i) s.array.push_back(Value(p._IdxShareTbl[i])); v["share"] = s; return v;
}
static const sNTL_FIELD_PROP& FieldProp(int f) { return *FieldMgr()->GetFields()[f].GetProp(); }

struct FieldLightState { RwRGBAReal amb; RwRGBAReal dir[dNTL_PL_DIRECTIONAL_LIGHT_NUM]; RwV3d ang[dNTL_PL_DIRECTIONAL_LIGHT_NUM]; };
struct FieldSoundState { RwInt32 bgm, env, share[5]; };
static void ApplyFieldLight(RwV3d pos, const FieldLightState& s) {
    sNTL_FIELD_PROP p(FALSE); p._ClrLightAmbient = s.amb;
    for (int i = 0; i < dNTL_PL_DIRECTIONAL_LIGHT_NUM; ++i) { p._ClrLightDirectional[i] = s.dir[i]; p._AngLightDirectional[i] = s.ang[i]; }
    Require(FieldMgr()->SetAFieldProp(pos, p, eNFP_LIGHT) != FALSE, "SetAFieldProp(eNFP_LIGHT)");
}
static void ApplyFieldSound(RwV3d pos, const FieldSoundState& s) {
    sNTL_FIELD_PROP p(FALSE); p._IdxBGMTbl = s.bgm; p._IdxEnvTbl = s.env; for (int i = 0; i < 5; ++i) p._IdxShareTbl[i] = s.share[i];
    Require(FieldMgr()->SetAFieldProp(pos, p, eNFP_SOUND) != FALSE, "SetAFieldProp(eNFP_SOUND)");
}

// ---- protocol ------------------------------------------------------------------------------------------------------
static Value ListValue(const Value& cmd, int familyMask) {
    bool all = cmd.flag("all", false);
    float cx = (float)cmd.num("x", gPos.x), cz = (float)cmd.num("z", gPos.z), range = (float)cmd.num("range", 200);
    Value arr = Value::arr(); int count[LK_COUNT] = {}; size_t limit = (size_t)cmd.num("limit", 400);
    ForEachLfx([&](const LfxItem& it) {
        int fam = it.kind <= LK_WL_BOX ? 1 : it.kind == LK_EFFECT ? 2 : 4; if (!(familyMask & fam)) return;
        ++count[it.kind];
        if (arr.array.size() < limit) {
            if (!all) { RwV3d p = LfxPosition(it); float dx = p.x - cx, dz = p.z - cz; if (dx * dx + dz * dz > range * range) return; }
            arr.array.push_back(ItemValue(it));
        }
    });
    Value v = Event("lfx-list"); v["items"] = arr;
    Value c = Value::obj(); for (int k = 0; k < LK_COUNT; ++k) c[LfxKindName(k)] = count[k]; v["loadedCounts"] = c;
    v["generation"] = gLfxGeneration;
    return v;
}
static LfxItem RequireItem(const Value& cmd) {
    Require(cmd.has("ref"), "ref required (entity reference from *-list, or \"selected\")");
    CNtlPLEntity* p = nullptr;
    if (cmd.at("ref").type == Value::String) { Require(cmd.text("ref") == "selected", "ref: number or \"selected\""); p = gLfxSelected; Require(p != nullptr, "nothing selected"); }
    else p = (CNtlPLEntity*)(uintptr_t)cmd.num("ref", 0);
    LfxItem it; Require(FindLfx(p, it), "entity not found in the loaded sectors");
    return it;
}
static int FamilyMaskOf(const std::string& c) { return c.rfind("light-", 0) == 0 ? 1 : c.rfind("effect-", 0) == 0 ? 2 : c.rfind("sound-", 0) == 0 ? 4 : 0; }

static bool LightFxCommand(const std::string& c, const Value& cmd) {
    int fam = FamilyMaskOf(c); if (!fam) return false;
    std::string op = c.substr(c.find('-') + 1);
    // generic operations, the family prefix only filters/validates
    if (op == "list") { Reply(cmd, ListValue(cmd, cmd.flag("everything", false) ? 7 : fam)); return true; }
    if (op == "tool") {
        std::string t = cmd.text("tool", "none");
        gLfxTool = t == "place" ? LT_PLACE : t == "select" ? LT_SELECT : LT_NONE;
        if (gLfxTool != LT_NONE) gTool = TT_NONE;   // one mouse tool at a time
        int defKind = fam == 1 ? LK_LIGHT : fam == 2 ? LK_EFFECT : LK_SOUND;
        int k = cmd.has("kind") ? ParseLfxKind(cmd.text("kind")) : (gLfxTemplate.kind >= 0 && (FamilyMaskOf(std::string(LfxFamily(gLfxTemplate.kind)) + "-") & fam) ? gLfxTemplate.kind : defKind);
        Require(k >= 0, "kind");
        if (k != gLfxTemplate.kind) gLfxTemplate = DefaultSpec(k);
        gLfxTemplate = SpecFromCommand(cmd, gLfxTemplate); gLfxTemplate.kind = k;
        if (gLfxTemplate.kind == LK_EFFECT && gLfxTool == LT_PLACE && !gLfxTemplate.name.empty()) Require(EffectExists(gLfxTemplate.name), "effect name not in the .eff catalog");
        if (cmd.has("show")) for (int i = 0; i < 3; ++i) if ((1 << i) & fam) gLfxShow[i] = cmd.flag("show", true);
        Value v = EditState("ack"); LightFxStats(v); Reply(cmd, v); return true;
    }
    if (op == "show") { for (int i = 0; i < 3; ++i) if ((1 << i) & fam) gLfxShow[i] = cmd.flag("enabled", true); Reply(cmd, Event("ack")); return true; }
    if (op == "select") {
        if (cmd.has("ref") && (cmd.at("ref").type == Value::String || cmd.num("ref", 0) != 0)) { LfxItem it = RequireItem(cmd); gLfxSelected = it.ent; gLfxSelectedKind = it.kind;
            if (cmd.flag("focus", false)) { gPos = LfxPosition(it); gSnapPending = false; }
            Value v = Event("ack"); v["item"] = ItemValue(it); Reply(cmd, v); }
        else { gLfxSelected = nullptr; gLfxSelectedKind = -1; Reply(cmd, Event("ack")); }
        ++gLfxGeneration; return true;
    }
    if (op == "pick") {
        LfxItem it; Value v = Event("lfx-pick");
        if (PickLfx((int)cmd.num("sx", gWidth / 2), (int)cmd.num("sy", gHeight / 2), it, fam)) { v["item"] = ItemValue(it); if (cmd.flag("select", true)) { gLfxSelected = it.ent; gLfxSelectedKind = it.kind; ++gLfxGeneration; } }
        Reply(cmd, v); return true;
    }
    if (op == "get") { LfxItem it = RequireItem(cmd); Value v = Event("lfx-item"); v["item"] = ItemValue(it); Reply(cmd, v); return true; }
    if (op == "add" || op == "world-add") {
        Require(gEditMode, "edits need --edit (workspace)");
        int k = fam == 1 ? (op == "world-add" ? (cmd.text("shape", "sphere") == "box" ? LK_WL_BOX : LK_WL_SPHERE) : LK_LIGHT) : fam == 2 ? LK_EFFECT : (cmd.flag("bgm", false) ? LK_BGM : LK_SOUND);
        if (cmd.has("kind")) { int kk = ParseLfxKind(cmd.text("kind")); Require(kk >= 0 && (FamilyMaskOf(std::string(LfxFamily(kk)) + "-") & fam), "kind does not belong to this command family"); k = kk; }
        LfxSpec s = DefaultSpec(k); s.pos = gPos;
        Value tmp = cmd; tmp.object.erase("kind");
        s = SpecFromCommand(tmp, s); s.kind = k;
        if (!cmd.has("y") && !cmd.has("pos")) { RwV3d q = s.pos; if (FieldMgr()->GetHeight(q)) s.pos.y = q.y + (RwReal)cmd.num("height", k == LK_EFFECT ? 0.0 : 3.0); }
        CNtlPLEntity* e = EditAdd(s);
        LfxItem it; Require(FindLfx(e, it), "entity vanished after insert");
        Value v = EditState("ack"); v["item"] = ItemValue(it); Reply(cmd, v); return true;
    }
    if (op == "set" || op == "move") {
        Require(gEditMode, "edits need --edit (workspace)");
        LfxItem it = RequireItem(cmd);
        Value tmp = cmd; tmp.object.erase("kind");
        LfxSpec s = SpecFromCommand(tmp, Describe(it));
        if (cmd.has("dx") || cmd.has("dy") || cmd.has("dz")) { s.pos.x += (RwReal)cmd.num("dx", 0); s.pos.y += (RwReal)cmd.num("dy", 0); s.pos.z += (RwReal)cmd.num("dz", 0); }
        EditSet(it, s, op == "move" ? "move" : "set");
        Value v = EditState("ack"); v["item"] = ItemValue(it); Reply(cmd, v); return true;
    }
    if (op == "delete") {
        Require(gEditMode, "edits need --edit (workspace)");
        LfxItem it = RequireItem(cmd); Value item = ItemValue(it);
        EditDelete(it);
        Value v = EditState("ack"); v["deleted"] = item; Reply(cmd, v); return true;
    }
    if (op == "catalog") {
        Value v = Event("lfx-catalog"); Value names = Value::arr();
        std::string filter = cmd.text("filter"); for (auto& ch : filter) ch = (char)tolower((unsigned char)ch);
        size_t limit = (size_t)cmd.num("limit", 2000), total = 0;
        if (fam == 2) {
            auto& m = EffectMap(); v["effectFilesLoaded"] = gEffectFilesLoaded;
            for (auto& kv : m) {
                std::string low = kv.first; for (auto& ch : low) ch = (char)tolower((unsigned char)ch);
                if (!filter.empty() && low.find(filter) == std::string::npos) continue;
                ++total; if (names.array.size() < limit) names.array.push_back(Value(kv.first));
            }
        } else {   // sounds: names already used by loaded sectors (the audio files are not in the editor cache)
            std::set<std::string> seen;
            ForEachLfx([&](const LfxItem& it) { if (it.kind == LK_SOUND || it.kind == LK_BGM) seen.insert(std::string(it.kind == LK_BGM ? "bgm:" : "") + static_cast<CNtlPLSound*>(it.ent)->GetName()); });
            for (auto& n : seen) { ++total; if (names.array.size() < limit) names.array.push_back(Value(n)); }
        }
        v["names"] = names; v["total"] = (int)total; Reply(cmd, v); return true;
    }
    if (op == "field") {   // light-field / sound-field: get (no values) or set (values given) on a loaded field
        RwV3d pos; int f = LoadedFieldAt(cmd, pos);
        Require(fam != 2, "effects have no field property");
        bool set = fam == 1 ? (cmd.has("ambient") || cmd.has("directional")) : (cmd.has("bgm") || cmd.has("env") || cmd.has("share"));
        if (set) {
            Require(gEditMode, "edits need --edit (workspace)");
            const sNTL_FIELD_PROP& cur = FieldProp(f);
            if (fam == 1) {
                FieldLightState before, after; before.amb = cur._ClrLightAmbient;
                for (int i = 0; i < dNTL_PL_DIRECTIONAL_LIGHT_NUM; ++i) { before.dir[i] = cur._ClrLightDirectional[i]; before.ang[i] = cur._AngLightDirectional[i]; }
                after = before;
                RwRGBAReal c; if (ReadColor(cmd, "ambient", c)) { c.alpha = after.amb.alpha; after.amb = c; }
                if (cmd.has("directional")) {
                    const auto& arr = cmd.at("directional").array;
                    for (size_t i = 0; i < arr.size() && i < dNTL_PL_DIRECTIONAL_LIGHT_NUM; ++i) {
                        if (ReadColor(arr[i], "color", c)) { c.alpha = after.dir[i].alpha; after.dir[i] = c; }
                        after.ang[i] = ReadV3(arr[i], "angle", after.ang[i]);
                    }
                }
                BeginStep("field light"); gOpenStep->fields.insert(f);
                gOpenStep->restore.push_back([pos, before]() { try { ApplyFieldLight(pos, before); } catch (...) {} });
                try { ApplyFieldLight(pos, after); } catch (...) { gUndo.pop_back(); gOpenStep = nullptr; throw; }
                EndStep();
            } else {
                FieldSoundState before, after; before.bgm = cur._IdxBGMTbl; before.env = cur._IdxEnvTbl; for (int i = 0; i < 5; ++i) before.share[i] = cur._IdxShareTbl[i];
                after = before;
                if (cmd.has("bgm")) after.bgm = (RwInt32)cmd.num("bgm", after.bgm);
                if (cmd.has("env")) after.env = (RwInt32)cmd.num("env", after.env);
                if (cmd.has("share")) { const auto& arr = cmd.at("share").array; for (size_t i = 0; i < arr.size() && i < 5; ++i) after.share[i] = (RwInt32)arr[i].number; }
                BeginStep("field sound"); gOpenStep->fields.insert(f);
                gOpenStep->restore.push_back([pos, before]() { try { ApplyFieldSound(pos, before); } catch (...) {} });
                try { ApplyFieldSound(pos, after); } catch (...) { gUndo.pop_back(); gOpenStep = nullptr; throw; }
                EndStep();
            }
            MarkDirtyField(f); ++gLfxGeneration;
        }
        Value v = set ? EditState("ack") : Event("field-prop");
        v["field"] = f; v["prop"] = fam == 1 ? FieldLightValue(FieldProp(f)) : FieldSoundValue(FieldProp(f)); v["changed"] = set;
        Reply(cmd, v); return true;
    }
    Value v = Event("error"); v["message"] = "unknown command: " + c; Reply(cmd, v); return true;
}
