// World Editor native host: opens a Taiwan-format world with the client's real NtlPresentation engine
// (compiled with dNTL_WORLD_TOOL_MODE) and renders it with the production camera/visual manager.
//
//   world-host --root <cache> --world <name> [--pos x,y,z] [--ground] [--camera free|top|bottom|left|right|front|back]
//              [--yaw deg] [--pitch deg] [--dist m] [--size WxH] [--capture out.png] [--frames N]
//              [--stats out.json] [--parent HWND] [--hidden] [--no-sky] [--no-overlay] [--spawns file.json]
//
// --capture: renders N frames, saves the back buffer as PNG and exits (CLI / audits).
// Otherwise it stays open: JSON commands on stdin, one per line ({"cmd":"camera","mode":"top","id":"7"}),
// JSON events on stdout. Every command with an "id" gets exactly one reply carrying that id.
// It only reads under --root (a private cache extracted from the client's paks). Nothing here saves
// world data: in tool mode CNtlPLWorldEntity::Destroy would autosave, so the host never destroys it
// and leaves with ExitProcess.
#include "precomp_ntlpresentation.h"
#include "NtlApplication.h"
#include "NtlPLGlobal.h"
#include "NtlPLApi.h"
#include "NtlAtomic.h"
#include "NtlMaterialExt.h"
#include "NtlPLMaterialExtWrapper.h"
#include "ntlworldinterface.h"
#include "ntlworldplugin.h"
#include "NtlWorldMaterialPlugin.h"
#include "NtlPLVisualManager.h"
#include "NtlPLSceneManagerFactory.h"
#include "NtlPLWorldEntity.h"
#include "NtlPostEffectCamera.h"
#include "NtlPLPropertyContainer.h"
#include "NtlPLEventHandler.h"
#include "NtlPLEventGenerator.h"
#include "NtlPLResourcePack.h"
#include "NtlPLResourceManager.h"
#include "NtlPLEntityFreeList.h"
#include "NtlEffectSystemFreeList.h"
#include "NtlWorldCommon.h"
#include "NtlWorldFieldManager.h"
#include "NtlWorldSectorManager.h"
#include "ceventhandler.h"
#include "NtlTimer.h"
#include <rtcharse.h>
#include <rtpick.h>
#include <d3d9.h>
#include <d3dx9.h>
#include <windowsx.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <string>
#include <vector>
#include <deque>
#include <mutex>
#include <thread>
#include <iostream>
#include <stdexcept>
#include <algorithm>
#include <direct.h>
#include <io.h>
#include <fcntl.h>
#include "json-protocol.h"

using labjson::Value;
static void Require(bool ok, const char* what) { if (!ok) throw std::runtime_error(what); }
static FILE* gOut = stdout;
static std::mutex gOutMutex;
static void Emit(const std::string& line) { std::lock_guard<std::mutex> l(gOutMutex); fprintf(gOut, "%s\n", line.c_str()); fflush(gOut); }
static void Emit(const Value& v) { Emit(v.dump()); }
static Value Event(const char* name) { Value v = Value::obj(); v["event"] = name; return v; }
static const char* gStage = "start";
static void Stage(const char* s) { gStage = s; fprintf(stderr, "[world-host] %s\n", s); fflush(stderr); }

// Crash report: symbolized stack (PDB next to the exe / in obj) on stderr, then a JSON error line.
#include <dbghelp.h>
static void PrintStack(EXCEPTION_POINTERS* ep, const char* kind) {
    HANDLE proc = GetCurrentProcess(), thread = GetCurrentThread();
    SymSetOptions(SYMOPT_LOAD_LINES | SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS);
    static bool inited = false; if (!inited) { SymInitialize(proc, nullptr, TRUE); inited = true; }
    fprintf(stderr, "[world-host] %s 0x%08lX at %p during '%s'\n", kind, ep->ExceptionRecord->ExceptionCode, ep->ExceptionRecord->ExceptionAddress, gStage);
    CONTEXT ctx = *ep->ContextRecord; STACKFRAME64 sf = {};
    sf.AddrPC.Offset = ctx.Eip; sf.AddrPC.Mode = AddrModeFlat; sf.AddrFrame.Offset = ctx.Ebp; sf.AddrFrame.Mode = AddrModeFlat; sf.AddrStack.Offset = ctx.Esp; sf.AddrStack.Mode = AddrModeFlat;
    for (int i = 0; i < 32 && StackWalk64(IMAGE_FILE_MACHINE_I386, proc, thread, &sf, &ctx, nullptr, SymFunctionTableAccess64, SymGetModuleBase64, nullptr); ++i) {
        char buf[sizeof(SYMBOL_INFO) + 512] = {}; auto* sym = (SYMBOL_INFO*)buf; sym->SizeOfStruct = sizeof(SYMBOL_INFO); sym->MaxNameLen = 511;
        DWORD64 disp = 0; DWORD ldisp = 0; IMAGEHLP_LINE64 line = {}; line.SizeOfStruct = sizeof(line);
        const char* name = SymFromAddr(proc, sf.AddrPC.Offset, &disp, sym) ? sym->Name : "?";
        if (SymGetLineFromAddr64(proc, sf.AddrPC.Offset, &ldisp, &line)) fprintf(stderr, "  #%02d %s  %s:%lu\n", i, name, line.FileName, line.LineNumber);
        else fprintf(stderr, "  #%02d %s +0x%llx\n", i, name, disp);
    }
    fflush(stderr);
}
static LONG WINAPI CppThrowLogger(EXCEPTION_POINTERS* ep) {
    static int count = 0;
    if (ep->ExceptionRecord->ExceptionCode == 0xE06D7363 && count++ < 4) PrintStack(ep, "C++ throw");
    return EXCEPTION_CONTINUE_SEARCH;
}
static LONG WINAPI CrashFilter(EXCEPTION_POINTERS* ep) {
    PrintStack(ep, "CRASH");
    { std::lock_guard<std::mutex> l(gOutMutex); fprintf(gOut, "{\"event\":\"error\",\"message\":\"native crash during %s\"}\n", gStage); fflush(gOut); }
    ExitProcess(3);
}

enum CameraMode { CAM_FREE, CAM_TOP, CAM_BOTTOM, CAM_LEFT, CAM_RIGHT, CAM_FRONT, CAM_BACK };
static CameraMode ParseCamera(const std::string& s) {
    if (s == "top") return CAM_TOP; if (s == "bottom") return CAM_BOTTOM; if (s == "left") return CAM_LEFT;
    if (s == "right") return CAM_RIGHT; if (s == "front") return CAM_FRONT; if (s == "back") return CAM_BACK; return CAM_FREE;
}
static const char* CameraName(CameraMode m) {
    static const char* n[] = { "free", "top", "bottom", "left", "right", "front", "back" }; return n[m];
}

class CWorldHostApp : public CNtlApplication {
public:
    CNtlPostEffectCamera* camera = nullptr;
    CNtlPLVisualManager* visual = nullptr;
    CNtlPLEventHandler* plEvents = nullptr;
    CNtlPLWorldEntity* world = nullptr;
    RtCharset* charset = nullptr;
    RwBool AttachPlugin() override {
        return RpWorldPluginAttach() && RpPVSPluginAttach() && RpCollisionPluginAttach() && RtAnimInitialize() &&
            RpLtMapPluginAttach() && RpHAnimPluginAttach() && RpSkinPluginAttach() && RpToonPluginAttach() &&
            RpUserDataPluginAttach() && RpIdPluginAttach() && RpMatFXPluginAttach() && RpUVAnimPluginAttach() &&
            RpPTankPluginAttach() && RpPrtStdPluginAttach() && RpPrtAdvPluginAttach() && RpNtlAtomicPluginAttach() &&
            NtlMatExtPluginAttach() && RpNtlWorldAttach() && RpNtlWorldMaterialPluginAttach();
    }
    RwBool CreateSubSystem(RwUInt32 w, RwUInt32 h, RwBool zBuffer) override {
        Stage("subsystems"); RWS::CEventHandler::Open();
        GetNtlResourcePackManager()->SetActiveTypeFlags(0); // loose files from the private cache only
        // Same presentation init as the client (CNtlPLGlobal, entity/attach/schedule free lists, effect system).
        Require(API_PLInit() != FALSE, "API_PLInit");
        CNtlPLResourceManager::GetInstance()->Crate();
        plEvents = NTL_NEW CNtlPLEventHandler; Require(plEvents->Create() != FALSE, "PL event handler");
        Stage("properties"); if (!CNtlPLPropertyContainer::GetInstance()) { auto* pc = NTL_NEW CNtlPLPropertyContainer; pc->Create(); }
        RwBool props = CNtlPLPropertyContainer::GetInstance()->LoadSerialize();
        if (!props) props = CNtlPLPropertyContainer::GetInstance()->Load();
        Require(props != FALSE, "property container (property\\*.rdf in cache)");
        camera = NTL_NEW CNtlPostEffectCamera;
        Require(camera->Create(w, h, zBuffer, 0.5f, 1024.f) != nullptr, "post effect camera");
        CNtlPLGlobal::SetActiveCamera(camera->GetCamera());
        RwRGBA fg = { 235, 240, 245, 255 }, bg = { 0, 0, 0, 0 };
        charset = RtCharsetCreate(&fg, &bg); // engine overlay text (same facility the client uses for FPS)
        Stage("visual manager"); visual = NTL_NEW CNtlPLVisualManager;
        Require(visual->Create() != FALSE, "visual manager");
        CNtlPLSceneManagerFactory::ActiveSceneManager(visual);
        return TRUE;
    }
};

struct SpawnMarker { int uid; RwV3d pos; RwV3d dir; int kind; };   // kind 0 mob, 1 npc
static CWorldHostApp gApp;
static HWND gWindow = nullptr;
static bool gQuit = false, gEmbedded = false;
static std::string gRoot, gWorld = "dbo_main_world", gCapture, gStats, gSpawnsFile, gPendingCapture, gPendingCaptureId;
static int gWidth = 1280, gHeight = 720, gFrames = 240;
static RwV3d gPos = { 0, 0, 0 };
static bool gPosGiven = false, gGround = false, gNoSky = false, gOverlay = true, gSnapPending = true, gWorldReadyEmitted = false;
static float gYaw = 45, gPitch = -25, gDist = 60;
static CameraMode gCam = CAM_FREE;
static std::mutex gCmdMutex; static std::deque<std::string> gCmds; static bool gEof = false;
static std::vector<SpawnMarker> gSpawns;
static std::vector<int> gSelected;
static float gFps = 0;
// Mouse state for camera control inside the native window.
static bool gOrbit = false, gPan = false; static int gMouseX = 0, gMouseY = 0, gDownX = 0, gDownY = 0; static bool gKeys[256] = {};
static const float kDeg = 3.14159265f / 180.f;

static RwCamera* ActiveCamera() { return CNtlPLGlobal::m_RwCamera ? CNtlPLGlobal::m_RwCamera : gApp.camera->GetCamera(); }
static void EffectiveAngles(float& yaw, float& pitch) {
    yaw = gYaw * kDeg; pitch = gPitch * kDeg;
    switch (gCam) {
    case CAM_TOP: pitch = -89.9f * kDeg; break;
    case CAM_BOTTOM: pitch = 89.9f * kDeg; break;
    case CAM_LEFT: yaw = 90 * kDeg; pitch = -20 * kDeg; break;
    case CAM_RIGHT: yaw = -90 * kDeg; pitch = -20 * kDeg; break;
    case CAM_FRONT: yaw = 0; pitch = -20 * kDeg; break;
    case CAM_BACK: yaw = 180 * kDeg; pitch = -20 * kDeg; break;
    default: break;
    }
}
static void ApplyCamera() {
    RwCamera* cam = ActiveCamera();
    RwV3d target = gPos, eye;
    float yaw, pitch; EffectiveAngles(yaw, pitch);
    RwV3d at = { cosf(pitch) * sinf(yaw), sinf(pitch), cosf(pitch) * cosf(yaw) };
    eye.x = target.x - at.x * gDist; eye.y = target.y - at.y * gDist; eye.z = target.z - at.z * gDist;
    if (fabsf(at.y) > 0.99f) { RwV3d negUp = { -sinf(yaw), 0, -cosf(yaw) }; API_PL_CameraLookAt(cam, &eye, &target, &negUp); }
    else API_PL_CameraLookAt(cam, &eye, &target, nullptr); // engine look-at; its internal up is (0,-1,0)
    if (gApp.world) { RwV3d p = gPos; gApp.world->SetPlayerPosition(p); }
}

static void Reply(const Value& cmd, Value v);
// Tool modules: shared edit infrastructure first, then one header per tool family (terrain, objects, water, ...).
#include "world-host-edit.h"
#include "world-host-terrain.h"
#include "world-host-water.h"
#include "world-host-lightfx.h"
#include "world-host-objects.h"
#include "world-host-props.h"

static RpWorldSector* CountSector(RpWorldSector* s, void* d) { ++*(int*)d; return s; }
static Value Stats(const char* name = "stats") {
    Value v = Event(name);
    sNtlWorldParam* wp = dGET_WORLD_PARAM();
    CNtlWorldFieldManager* fm = gApp.world ? gApp.world->GetWorldFieldMgr() : nullptr;
    RwV3d p = gPos;
    int frustum = 0; RwCamera* cam = CNtlPLGlobal::m_RwCamera;
    if (cam && RwCameraGetWorld(cam)) RwCameraForAllSectorsInFrustum(cam, CountSector, &frustum);
    v["world"] = gWorld; v["camera"] = CameraName(gCam);
    Value pos = Value::arr(); pos.array = { Value((double)p.x), Value((double)p.y), Value((double)p.z) }; v["pos"] = pos;
    v["yaw"] = (double)gYaw; v["pitch"] = (double)gPitch; v["dist"] = (double)gDist;
    v["field"] = fm ? fm->GetFieldIdx(p) : -1;
    v["sector"] = fm ? fm->GetSectorIdx(p) : -1;
    v["worldReady"] = fm ? (bool)(fm->GetWorldReady() != FALSE) : false;
    v["worldSize"] = wp->WorldSize; v["sectorSize"] = wp->WorldSectorSize; v["fieldSize"] = wp->WorldFieldSize; v["version"] = std::string(wp->WorldVer);
    v["frustumSectors"] = frustum; v["visibleSectors"] = (int)g_vecVisibleSectors.size(); v["polys"] = (int)wp->WorldPolyCnt;
    v["fps"] = (double)gFps; v["spawns"] = (int)gSpawns.size(); v["selected"] = (int)gSelected.size();
    v["overlay"] = gOverlay; v["toolMode"] = true;
    v["editMode"] = gEditMode; v["dirtyFields"] = (int)gDirtyFields.size(); v["editedFields"] = (int)gEverDirtyFields.size(); v["undo"] = (int)gUndo.size();
    v["tool"] = gTool == TT_HEIGHT ? "height" : gTool == TT_PAINT ? "paint" : "none";
    WaterStats(v);
    LightFxStats(v);
    ObjectsStats(v);
    return v;
}

static bool CaptureBackBuffer(const std::string& path) {
    IDirect3DDevice9* dev = (IDirect3DDevice9*)RwD3D9GetCurrentD3DDevice();
    if (!dev) return false;
    IDirect3DSurface9* back = nullptr;
    if (FAILED(dev->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &back)) || !back) return false;
    D3DSURFACE_DESC desc = {}; back->GetDesc(&desc);
    RECT r = { 0, 0, std::min<LONG>(gWidth, (LONG)desc.Width), std::min<LONG>(gHeight, (LONG)desc.Height) };
    HRESULT hr = D3DXSaveSurfaceToFileA(path.c_str(), D3DXIFF_PNG, back, nullptr, &r);
    if (FAILED(hr)) fprintf(stderr, "[world-host] capture failed hr=0x%08lX backbuffer=%ux%u window=%dx%d\n", hr, desc.Width, desc.Height, gWidth, gHeight);
    bool ok = SUCCEEDED(hr);
    back->Release();
    return ok;
}

// Spawn markers: vertical pole + facing tick, drawn with the engine's own Im3D after the world pass.
// Mob and NPC tables coexist: a marker is identified by (kind, uid); gSelected holds SpawnKey values.
static int SpawnKey(int kind, int uid) { return kind * 10000000 + uid; }
static bool IsSelected(const SpawnMarker& s) { int k = SpawnKey(s.kind, s.uid); return std::find(gSelected.begin(), gSelected.end(), k) != gSelected.end(); }
static int KindOf(const std::string& k) { return (k == "npc" || k == "NPC") ? 1 : 0; }
static void RenderSpawnMarkers() {
    if (gSpawns.empty()) return;
    RwCamera* cam = ActiveCamera();
    RwV3d* eye = RwMatrixGetPos(RwFrameGetLTM(RwCameraGetFrame(cam)));
    static std::vector<RwIm3DVertex> verts; verts.clear();
    auto add = [&](const RwV3d& a, const RwV3d& b, RwRGBA c) {
        RwIm3DVertex v0, v1;
        RwIm3DVertexSetPos(&v0, a.x, a.y, a.z); RwIm3DVertexSetRGBA(&v0, c.red, c.green, c.blue, c.alpha);
        RwIm3DVertexSetPos(&v1, b.x, b.y, b.z); RwIm3DVertexSetRGBA(&v1, c.red, c.green, c.blue, c.alpha);
        verts.push_back(v0); verts.push_back(v1);
    };
    float far2 = 420.f * 420.f;
    for (const auto& s : gSpawns) {
        float dx = s.pos.x - eye->x, dy = s.pos.y - eye->y, dz = s.pos.z - eye->z;
        if (dx * dx + dy * dy + dz * dz > far2) continue;
        bool sel = IsSelected(s);
        RwRGBA c = sel ? RwRGBA{ 80, 240, 255, 255 } : (s.kind ? RwRGBA{ 255, 215, 70, 255 } : RwRGBA{ 255, 80, 70, 255 });
        float k = std::max(1.f, sqrtf(dx * dx + dy * dy + dz * dz) / 40.f);   // keep markers readable from far away
        float h = (sel ? 6.f : 4.f) * k;
        RwV3d top = { s.pos.x, s.pos.y + h, s.pos.z };
        add(s.pos, top, c);
        float len = 1.5f * k; RwV3d d = s.dir; float n = sqrtf(d.x * d.x + d.z * d.z); if (n < 1e-3f) { d.x = 1; d.z = 0; n = 1; }
        RwV3d tip = { top.x + d.x / n * len, top.y, top.z + d.z / n * len }; add(top, tip, c);
        float r = (sel ? 1.2f : 0.7f) * k;
        RwV3d a = { s.pos.x - r, s.pos.y + 0.1f, s.pos.z }, b = { s.pos.x + r, s.pos.y + 0.1f, s.pos.z }; add(a, b, c);
        a = { s.pos.x, s.pos.y + 0.1f, s.pos.z - r }; b = { s.pos.x, s.pos.y + 0.1f, s.pos.z + r }; add(a, b, c);
    }
    if (verts.empty()) return;
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, nullptr);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEVERTEXALPHAENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATEFOGENABLE, (void*)FALSE);
    for (size_t i = 0; i < verts.size(); i += 2000) {
        RwInt32 n = (RwInt32)std::min<size_t>(2000, verts.size() - i);
        if (RwIm3DTransform(&verts[i], n, nullptr, rwIM3D_ALLOPAQUE)) { RwIm3DRenderPrimitive(rwPRIMTYPELINELIST); RwIm3DEnd(); }
    }
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)TRUE);
}

static void RenderOverlay() {
    if (!gOverlay || !gApp.charset) return;
    sNtlWorldParam* wp = dGET_WORLD_PARAM();
    CNtlWorldFieldManager* fm = gApp.world ? gApp.world->GetWorldFieldMgr() : nullptr;
    RwV3d p = gPos; int frustum = 0;
    if (CNtlPLGlobal::m_RwCamera && RwCameraGetWorld(CNtlPLGlobal::m_RwCamera)) RwCameraForAllSectorsInFrustum(CNtlPLGlobal::m_RwCamera, CountSector, &frustum);
    char line[256]; int y = 8; const int step = 12;
    auto print = [&](const char* s) { RtCharsetPrint(gApp.charset, s, 8, y); y += step; };
    snprintf(line, sizeof(line), "NTL ENGINE  %s  [%s]  %.0f fps", gWorld.c_str(), CameraName(gCam), gFps); print(line);
    snprintf(line, sizeof(line), "POS %.1f %.1f %.1f  YAW %.0f PITCH %.0f DIST %.0f", p.x, p.y, p.z, gYaw, gPitch, gDist); print(line);
    snprintf(line, sizeof(line), "FIELD %d  SECTOR %d  READY %d", fm ? fm->GetFieldIdx(p) : -1, fm ? fm->GetSectorIdx(p) : -1, fm ? (int)fm->GetWorldReady() : 0); print(line);
    snprintf(line, sizeof(line), "SECTORS frustum %d visible %d  POLYS %d", frustum, (int)g_vecVisibleSectors.size(), (int)wp->WorldPolyCnt); print(line);
    snprintf(line, sizeof(line), "SPAWNS %d  SELECTED %d", (int)gSpawns.size(), (int)gSelected.size()); print(line);
}

static void RenderFrame(float elapsed, const std::string& capturePath, const std::string& captureId = std::string()) {
    g_SetElapsedTime(elapsed);
    ApplyCamera();
    gStage = "frame update";
    gApp.visual->UpdateBeforeCamera(elapsed);
    gApp.visual->Update(elapsed);
    gStage = "frame render";
    static RwRGBA background = { 40, 40, 48, 255 };
    if (!gApp.camera->CameraClear(&background, rwCAMERACLEARZ | rwCAMERACLEARIMAGE)) return;
    if (gApp.camera->PostCameraBeginUpdate(elapsed)) {
        gApp.visual->Render();
        RwD3D9SetVertexShader(NULL);
        RwD3D9SetPixelShader(NULL);
        RenderSpawnMarkers();
        RenderBrushCursor();
        RenderWaterGizmos();
        RenderLightFxGizmos();
        RenderObjectGizmo();
        RenderPropsOverlay();
        gApp.camera->PostCameraEndUpdate();
    }
    gApp.camera->UpdatePostEffectFilter();
    if (gApp.camera->MainCameraBeginUpdate(elapsed)) {
        gApp.visual->PostRender();
        RenderOverlay();
        RenderPropsOverlay2D();
        gApp.camera->MainCameraEndUpdate();
    }
    if (!capturePath.empty()) {
        bool ok = CaptureBackBuffer(capturePath);
        Value v = Event("capture"); v["ok"] = ok; v["path"] = capturePath; if (!captureId.empty()) v["id"] = captureId;
        Emit(v);
    }
    gApp.camera->CameraShowRaster(gWindow, 0);
}

static void CreateWorld() {
    sNtlWorldParam* wp = dGET_WORLD_PARAM();
    _getcwd(wp->CurWorkingFolderName, 256);
    strcpy(wp->CurDefaultTexPath, wp->CurWorkingFolderName); strcat(wp->CurDefaultTexPath, "\\texture\\ntlwe\\");
    std::string rel = "\\world\\" + gWorld;
    strcpy(wp->WorldProjectFolderName, wp->CurWorkingFolderName); strcat(wp->WorldProjectFolderName, rel.c_str());
    std::string gwp = "." + rel + "\\#######.gwp";
    FILE* f = fopen(gwp.c_str(), "rb");
    Require(f != nullptr, "world descriptor #######.gwp missing in cache (run prepare-world-editor-cache.ps1)");
    LoadFileWorldState(wp, f); fclose(f);
    Value v = Event("world-param"); v["version"] = std::string(wp->WorldVer); v["supported"] = std::string(dNTL_WORLD_VERSION); v["worldSize"] = wp->WorldSize; v["world"] = gWorld;
    Emit(v);
    Require(dNTL_WORLD_VERSION_COMPARE(wp->WorldVer, dNTL_WORLD_VERSION), "unsupported world resource version");
    // Indoor (RpWorld) saves go through CNtlWorldLTManager (lightmaps into texture/ntlwe/ltmap, a shared junction in
    // the workspace, plus SaveBSP): view-only until that is redirected into the workspace.
    Require(!(gEditMode && wp->WorldType == 1), "indoor (RpWorld) worlds open view-only in the World Editor");
    gApp.world = static_cast<CNtlPLWorldEntity*>(GetSceneManager()->CreateEntity(PLENTITY_WORLD, "NtlWorld"));
    Require(gApp.world != nullptr, "CreateEntity(PLENTITY_WORLD)");
    RwV3d p = gPos; gApp.world->CreateWorld(p);
    // Same as CGameStage::PostMutiThreadLoading: put the active camera into the engine's RpWorld.
    RwFrameListSetAutoUpdate(TRUE);
    if (CNtlPLGlobal::m_pRpWorld && CNtlPLGlobal::m_RwCamera) RpWorldAddCamera(CNtlPLGlobal::m_pRpWorld, CNtlPLGlobal::m_RwCamera);
}

static bool SnapToGround(RwV3d& p) {
    if (!gApp.world) return false;
    RwV3d q = p; if (!gApp.world->GetWorldFieldMgr()->GetHeight(q)) return false;
    p.y = q.y; return true;
}

static int gResizeReapply = 0;   // frames until the size is applied again (see ApplySize)
static void ApplySize() {
    RwRect rt = { 0, 0, gWidth, gHeight };
    gApp.camera->SetCameraSize(&rt);
    CNtlPLEventGenerator::Resize(gWidth, gHeight, FALSE);
}
static void ResizeTo(int w, int h) {
    w = std::max(64, std::min(4096, w)); h = std::max(64, std::min(2160, h));
    if (w == gWidth && h == gHeight) return;
    gWidth = w; gHeight = h;
    RECT rc = { 0, 0, w, h };
    if (!gEmbedded) AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);   // client area must match the camera raster
    SetWindowPos(gWindow, nullptr, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_NOZORDER | SWP_NOMOVE | SWP_NOACTIVATE);
    ApplySize();
    // Like CDboApplication::ApplyPendingWindowChange: the first resize resets the device and the post-effect
    // targets are recreated lazily on the next frames, so the same size is applied once more afterwards.
    gResizeReapply = 3;
}
static void TickResize() { if (gResizeReapply > 0 && --gResizeReapply == 0) ApplySize(); }

// Picking: nearest spawn to the camera ray (within a screen-size dependent radius), else terrain.
static void Pick(int x, int y, bool shift, const char* button) {
    Value v = Event("pick"); v["button"] = button; v["shift"] = shift;
    Value screen = Value::arr(); screen.array = { Value(x), Value(y) }; v["screen"] = screen;
    RwCamera* cam = ActiveCamera();
    RwV2d px = { (RwReal)x, (RwReal)y }; RwLine ray;
    if (RwCameraCalcPixelRay(cam, &ray, &px)) {
        RwV3d d = { ray.end.x - ray.start.x, ray.end.y - ray.start.y, ray.end.z - ray.start.z };
        float len = sqrtf(d.x * d.x + d.y * d.y + d.z * d.z); if (len > 0) { d.x /= len; d.y /= len; d.z /= len; }
        int best = -1, bestKind = 0; float bestScore = 1e30f;
        for (const auto& s : gSpawns) {
            for (float hy : { 0.f, 2.f, 4.f }) {   // test along the drawn pole
                RwV3d w = { s.pos.x - ray.start.x, s.pos.y + hy - ray.start.y, s.pos.z - ray.start.z };
                float t = w.x * d.x + w.y * d.y + w.z * d.z; if (t < 0) continue;
                RwV3d c = { w.x - d.x * t, w.y - d.y * t, w.z - d.z * t };
                float dist = sqrtf(c.x * c.x + c.y * c.y + c.z * c.z), tol = 0.6f + t * 0.012f;
                if (dist < tol && t < bestScore) { bestScore = t; best = s.uid; bestKind = s.kind; }
            }
        }
        if (best >= 0) { v["uid"] = best; v["kind"] = bestKind ? "npc" : "mob"; }
    }
    RwV3d hit;
    if (gApp.world && gApp.world->GetWorldFieldMgr()->PickTerrain(x, y, hit)) {
        Value t = Value::arr(); t.array = { Value((double)hit.x), Value((double)hit.y), Value((double)hit.z) }; v["terrain"] = t;
    }
    Emit(v);
}

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_CLOSE: case WM_DESTROY: gQuit = true; return 0;
    case WM_LBUTTONDOWN:
        SetFocus(h); SetCapture(h); gDownX = GET_X_LPARAM(l); gDownY = GET_Y_LPARAM(l);
        if (gTool != TT_NONE) { BrushAt(gDownX, gDownY); BrushBegin(); }
        else if (PropsMouseDown(gDownX, gDownY)) {}
        else ObjectsMouseDown(gDownX, gDownY, w);
        return 0;
    case WM_LBUTTONUP: {
        ReleaseCapture(); int x = GET_X_LPARAM(l), y = GET_Y_LPARAM(l);
        if (gTool != TT_NONE) { BrushEnd(); return 0; }
        if (PropsMouseUp()) return 0;
        if (ObjectsMouseUp(x, y, w)) return 0;
        if (abs(x - gDownX) < 4 && abs(y - gDownY) < 4 && WaterClick(x, y, (w & MK_SHIFT) != 0)) return 0;
        if (abs(x - gDownX) < 4 && abs(y - gDownY) < 4 && LightFxClick(x, y, (w & MK_SHIFT) != 0)) return 0;
        if (abs(x - gDownX) < 4 && abs(y - gDownY) < 4) Pick(x, y, (w & MK_SHIFT) != 0, "left");
        return 0; }
    case WM_RBUTTONDOWN: SetFocus(h); SetCapture(h); gOrbit = true; gMouseX = GET_X_LPARAM(l); gMouseY = GET_Y_LPARAM(l); return 0;
    case WM_RBUTTONUP: ReleaseCapture(); gOrbit = false; return 0;
    case WM_MBUTTONDOWN: SetFocus(h); SetCapture(h); gPan = true; gMouseX = GET_X_LPARAM(l); gMouseY = GET_Y_LPARAM(l); return 0;
    case WM_MBUTTONUP: ReleaseCapture(); gPan = false; return 0;
    case WM_MOUSEMOVE: {
        int x = GET_X_LPARAM(l), y = GET_Y_LPARAM(l), dx = x - gMouseX, dy = y - gMouseY; gMouseX = x; gMouseY = y;
        if (gTool != TT_NONE) BrushAt(x, y);
        else { WaterHover(x, y); ObjectsMouseMove(x, y, w); }
        PropsMouseMove(x, y);
        if (gOrbit && !(w & MK_SHIFT)) {
            if (gCam != CAM_FREE) { float yaw, pitch; EffectiveAngles(yaw, pitch); gYaw = yaw / kDeg; gPitch = pitch / kDeg; gCam = CAM_FREE; }
            gYaw += dx * 0.3f; gPitch = std::max(-89.f, std::min(60.f, gPitch - dy * 0.3f));
        } else if (gPan || (gOrbit && (w & MK_SHIFT))) {
            float yaw, pitch; EffectiveAngles(yaw, pitch); float k = gDist * 0.0025f;
            gPos.x += (-cosf(yaw) * dx + -sinf(yaw) * dy) * k; gPos.z += (sinf(yaw) * dx + -cosf(yaw) * dy) * k;
            gSnapPending = true;
        }
        return 0; }
    case WM_MOUSEWHEEL: { float notches = GET_WHEEL_DELTA_WPARAM(w) / 120.f; gDist = std::max(3.f, std::min(900.f, gDist * powf(0.88f, notches))); return 0; }
    case WM_KEYDOWN: if (ObjectsKey(w)) return 0; if (w < 256) gKeys[w] = true; return 0;
    case WM_KEYUP: if (w < 256) gKeys[w] = false; return 0;
    case WM_KILLFOCUS: memset(gKeys, 0, sizeof(gKeys)); gOrbit = gPan = false; BrushEnd(); ObjectsMouseCancel(); return 0;
    }
    return DefWindowProcA(h, m, w, l);
}

static void KeyboardMove(float elapsed) {
    float fwd = (gKeys['W'] ? 1.f : 0.f) - (gKeys['S'] ? 1.f : 0.f), side = (gKeys['D'] ? 1.f : 0.f) - (gKeys['A'] ? 1.f : 0.f);
    float up = (gKeys['E'] ? 1.f : 0.f) - (gKeys['Q'] ? 1.f : 0.f);
    if (fwd == 0 && side == 0 && up == 0) return;
    float yaw, pitch; EffectiveAngles(yaw, pitch);
    float speed = std::max(10.f, gDist * 1.2f) * elapsed * (gKeys[VK_SHIFT] ? 4.f : 1.f);
    gPos.x += (sinf(yaw) * fwd - cosf(yaw) * side) * speed; gPos.z += (cosf(yaw) * fwd + sinf(yaw) * side) * speed;
    gPos.y += up * speed;
    if (up == 0) gSnapPending = true;
}

// Large spawn tables (30k+ rows) exceed labjson's bounds: numeric arrays are scanned directly.
static std::vector<double> ScanArray(const std::string& text, const char* key) {
    std::vector<double> out;
    std::string needle = std::string("\"") + key + "\"";
    size_t at = text.find(needle);
    if (at == std::string::npos) return out;
    at = text.find('[', at + needle.size());
    if (at == std::string::npos) return out;
    const char* p = text.c_str() + at + 1;
    while (*p) {
        while (*p == ' ' || *p == '\n' || *p == '\r' || *p == '\t' || *p == ',') ++p;
        if (*p == ']' || !*p) break;
        char* end = nullptr; double d = strtod(p, &end);
        if (end == p) break;
        out.push_back(d); p = end;
    }
    return out;
}
static std::string ScanString(const std::string& text, const char* key) {
    std::string needle = std::string("\"") + key + "\"";
    size_t at = text.find(needle); if (at == std::string::npos) return "";
    at = text.find(':', at + needle.size()); if (at == std::string::npos) return "";
    size_t q = text.find('"', at); if (q == std::string::npos) return "";
    size_t e = text.find('"', q + 1); if (e == std::string::npos) return "";
    return text.substr(q + 1, e - q - 1);
}
// Replaces the markers of one kind (mob or npc); the other kind's markers stay.
static void LoadSpawnsText(const std::string& text, const std::string& kindOverride = std::string()) {
    std::vector<double> uid = ScanArray(text, "uid"), xs = ScanArray(text, "x"), ys = ScanArray(text, "y"), zs = ScanArray(text, "z");
    std::vector<double> dxs = ScanArray(text, "dirX"), dzs = ScanArray(text, "dirZ");
    int kind = KindOf(kindOverride.empty() ? ScanString(text, "kind") : kindOverride);
    Require(ys.size() == xs.size() && zs.size() == xs.size(), "spawns: x/y/z length mismatch");
    gSpawns.erase(std::remove_if(gSpawns.begin(), gSpawns.end(), [kind](const SpawnMarker& m) { return m.kind == kind; }), gSpawns.end());
    gSelected.erase(std::remove_if(gSelected.begin(), gSelected.end(), [kind](int k) { return k / 10000000 == kind; }), gSelected.end());
    for (size_t i = 0; i < xs.size() && i < 500000; ++i) {
        SpawnMarker s;
        s.uid = i < uid.size() ? (int)uid[i] : (int)i;
        s.pos = { (RwReal)xs[i], (RwReal)ys[i], (RwReal)zs[i] };
        s.dir = { i < dxs.size() ? (RwReal)dxs[i] : 1.f, 0, i < dzs.size() ? (RwReal)dzs[i] : 0.f };
        s.kind = kind;
        gSpawns.push_back(s);
    }
}
static std::string ReadFileText(const std::string& path) {
    FILE* f = fopen(path.c_str(), "rb"); Require(f != nullptr, "cannot open file");
    std::string s; char buf[65536]; size_t n; while ((n = fread(buf, 1, sizeof(buf), f)) > 0) s.append(buf, n); fclose(f);
    return s;
}
static void LoadSpawns(const Value& v) {
    gSpawns.clear();
    static const Value empty = Value::arr();
    auto opt = [&](const char* k) -> const Value& { return v.has(k) ? v.at(k) : empty; };
    const Value& ux = opt("uid"); const Value& xs = v.at("x"); const Value& ys = v.at("y"); const Value& zs = v.at("z");
    const Value& dxs = opt("dirX"); const Value& dzs = opt("dirZ");
    int kind = v.text("kind") == "npc" ? 1 : 0;
    size_t n = xs.array.size();
    Require(ys.array.size() == n && zs.array.size() == n, "spawns: x/y/z length mismatch");
    for (size_t i = 0; i < n && i < 200000; ++i) {
        SpawnMarker s;
        s.uid = i < ux.array.size() ? (int)ux.array[i].number : (int)i;
        s.pos = { (RwReal)xs.array[i].number, (RwReal)ys.array[i].number, (RwReal)zs.array[i].number };
        s.dir = { i < dxs.array.size() ? (RwReal)dxs.array[i].number : 1.f, 0, i < dzs.array.size() ? (RwReal)dzs.array[i].number : 0.f };
        s.kind = kind;
        gSpawns.push_back(s);
    }
    gSelected.clear();
}

static void Reply(const Value& cmd, Value v) { if (cmd.has("id")) v["id"] = cmd.at("id").type == Value::String ? cmd.at("id").string : cmd.at("id").dump(); Emit(v); }

static void Command(const std::string& line) {
    if (ScanString(line, "cmd") == "spawns") {   // fast path: big arrays, or a file written by the Studio
        Value ack = Event("ack");
        std::string id = ScanString(line, "id"); if (!id.empty()) ack["id"] = id;
        try {
            std::string file = ScanString(line, "file");
            if (!file.empty()) { std::string unescaped; for (size_t i = 0; i < file.size(); ++i) { if (file[i] == '\\' && i + 1 < file.size()) ++i; unescaped += file[i]; } file = unescaped; }
            std::string text = file.empty() ? line : ReadFileText(file);
            std::string kindOverride = ScanString(line, "kind");
            LoadSpawnsText(text, kindOverride);
            int kind = KindOf(kindOverride.empty() ? ScanString(text, "kind") : kindOverride), count = 0;
            for (const auto& m : gSpawns) count += m.kind == kind;
            ack["count"] = count; ack["total"] = (int)gSpawns.size();
        } catch (const std::exception& e) { ack["event"] = "error"; ack["message"] = e.what(); }
        Emit(ack);
        return;
    }
    Value cmd;
    try { cmd = labjson::parse(line); } catch (const std::exception& e) { Value v = Event("error"); v["message"] = std::string("bad command json: ") + e.what(); Emit(v); return; }
    std::string c = cmd.text("cmd");
    try {
        if (TerrainCommand(c, cmd)) {}
        else if (WaterCommand(c, cmd)) {}
        else if (LightFxCommand(c, cmd)) {}
        else if (ObjectsCommand(c, cmd)) {}
        else if (PropsCommand(c, cmd)) {}
        else if (c == "close" || c == "quit") { gQuit = true; Reply(cmd, Event("ack")); }
        else if (c == "goto") {
            gPos.x = (RwReal)cmd.num("x", gPos.x); gPos.y = (RwReal)cmd.num("y", gPos.y); gPos.z = (RwReal)cmd.num("z", gPos.z);
            if (cmd.flag("ground", true)) gSnapPending = true;
            if (cmd.has("dist")) gDist = (float)std::max(3.0, std::min(900.0, cmd.num("dist", gDist)));
            Reply(cmd, Stats("ack"));
        }
        else if (c == "camera") {
            if (cmd.has("mode")) gCam = ParseCamera(cmd.text("mode"));
            if (cmd.has("yaw")) gYaw = (float)cmd.num("yaw", gYaw);
            if (cmd.has("pitch")) gPitch = (float)std::max(-89.0, std::min(60.0, cmd.num("pitch", gPitch)));
            if (cmd.has("dist")) gDist = (float)std::max(3.0, std::min(900.0, cmd.num("dist", gDist)));
            Reply(cmd, Stats("ack"));
        }
        else if (c == "resize") { ResizeTo((int)cmd.num("width", gWidth), (int)cmd.num("height", gHeight)); Reply(cmd, Event("ack")); }
        else if (c == "overlay") { gOverlay = cmd.flag("enabled", !gOverlay); Reply(cmd, Event("ack")); }
        else if (c == "sky") { CNtlPLGlobal::m_bWorldSkyVisible = cmd.flag("enabled", true) ? TRUE : FALSE; Reply(cmd, Event("ack")); }
        else if (c == "spawns") { LoadSpawns(cmd); Value v = Event("ack"); v["count"] = (int)gSpawns.size(); Reply(cmd, v); }
        else if (c == "select") {
            int kind = KindOf(cmd.text("kind", "mob"));
            gSelected.clear(); for (const auto& u : (cmd.has("uids") ? cmd.at("uids").array : std::vector<Value>())) gSelected.push_back(SpawnKey(kind, (int)u.number));
            if (cmd.flag("focus", false) && !gSelected.empty())
                for (const auto& s : gSpawns) if (SpawnKey(s.kind, s.uid) == gSelected[0]) { gPos = s.pos; break; }
            Reply(cmd, Event("ack"));
        }
        else if (c == "move-spawn") {   // visual only: the Studio applies the edit through SpawnEditSession and echoes it here
            int uid = (int)cmd.num("uid", -1), kind = KindOf(cmd.text("kind", "mob"));
            for (auto& s : gSpawns) if (s.uid == uid && s.kind == kind) { s.pos = { (RwReal)cmd.num("x", s.pos.x), (RwReal)cmd.num("y", s.pos.y), (RwReal)cmd.num("z", s.pos.z) }; }
            Reply(cmd, Event("ack"));
        }
        else if (c == "capture") {
            std::string path = cmd.text("path"); Require(!path.empty(), "capture path required");
            gPendingCapture = path; gPendingCaptureId = cmd.has("id") ? (cmd.at("id").type == Value::String ? cmd.at("id").string : cmd.at("id").dump()) : "";
        }
        else if (c == "stats" || c == "state") Reply(cmd, Stats());
        else if (c == "focus") { SetFocus(gWindow); Reply(cmd, Event("ack")); }
        else if (c == "pick-center" || c == "pick") {   // CLI/automation: same picking as a click
            int x = (int)cmd.num("x", gWidth / 2), y = (int)cmd.num("y", gHeight / 2);
            Pick(x, y, cmd.flag("shift", false), "command"); Reply(cmd, Event("ack"));
        }
        else { Value v = Event("error"); v["message"] = "unknown command: " + c; Reply(cmd, v); }
    } catch (const std::exception& e) { Value v = Event("error"); v["message"] = e.what(); Reply(cmd, v); }
}

static bool ParseVec(const char* s, RwV3d& v) { return sscanf(s, "%f,%f,%f", &v.x, &v.y, &v.z) == 3; }

int main(int argc, char** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    SetUnhandledExceptionFilter(CrashFilter);
    AddVectoredExceptionHandler(1, CppThrowLogger);
    { int fd = _dup(_fileno(stdout)); gOut = _fdopen(fd, "w"); _dup2(_fileno(stderr), _fileno(stdout)); } // engine printf -> stderr; protocol keeps the real stdout
    HWND parent = nullptr; bool hidden = false;
    try {
        for (int i = 1; i < argc; ++i) {
            std::string arg = argv[i];
            auto next = [&]() -> const char* { Require(i + 1 < argc, "missing argument value"); return argv[++i]; };
            if (arg == "--root") gRoot = next();
            else if (arg == "--world") gWorld = next();
            else if (arg == "--pos") { Require(ParseVec(next(), gPos), "--pos x,y,z"); gPosGiven = true; }
            else if (arg == "--camera") gCam = ParseCamera(next());
            else if (arg == "--yaw") gYaw = (float)atof(next());
            else if (arg == "--pitch") gPitch = (float)atof(next());
            else if (arg == "--dist") gDist = (float)atof(next());
            else if (arg == "--size") Require(sscanf(next(), "%dx%d", &gWidth, &gHeight) == 2, "--size WxH");
            else if (arg == "--capture") gCapture = next();
            else if (arg == "--stats") gStats = next();
            else if (arg == "--frames") gFrames = atoi(next());
            else if (arg == "--parent") parent = (HWND)(uintptr_t)strtoull(next(), nullptr, 0);
            else if (arg == "--hidden") hidden = true;
            else if (arg == "--ground") gGround = true;
            else if (arg == "--no-sky") gNoSky = true;
            else if (arg == "--no-overlay") gOverlay = false;
            else if (arg == "--edit") gEditMode = true;
            else if (arg == "--spawns") gSpawnsFile = next();
            else throw std::runtime_error("usage: world-host --root <cache> --world <name> [--pos x,y,z] [--camera free|top|...] [--capture out.png]");
        }
        for (char c : gWorld) Require(isalnum((unsigned char)c) || c == '_', "invalid world name");
        Require(!gRoot.empty(), "--root <cache> required");
        char full[MAX_PATH]; Require(GetFullPathNameA(gRoot.c_str(), MAX_PATH, full, nullptr) > 0, "root path");
        gRoot = full;
        std::string low = gRoot; for (auto& c : low) c = (char)tolower((unsigned char)c);
        Require(low.find("\\pack\\") == std::string::npos, "root must be the private cache, not the client's pack folder");
        std::string spawnsText;
        if (!gSpawnsFile.empty()) {   // read before chdir: the path is relative to the caller
            FILE* f = fopen(gSpawnsFile.c_str(), "rb"); Require(f != nullptr, "--spawns file");
            fclose(f); spawnsText = ReadFileText(gSpawnsFile);
        }
        if (!gCapture.empty()) { char cap[MAX_PATH]; if (GetFullPathNameA(gCapture.c_str(), MAX_PATH, cap, nullptr)) gCapture = cap; }
        if (!gStats.empty()) { char st[MAX_PATH]; if (GetFullPathNameA(gStats.c_str(), MAX_PATH, st, nullptr)) gStats = st; }
        Require(SetCurrentDirectoryA(gRoot.c_str()) != FALSE, "enter cache root");
        Stage("window");

        WNDCLASSA wc = {}; wc.lpfnWndProc = WndProc; wc.hInstance = GetModuleHandle(nullptr); wc.lpszClassName = "DboWorldHost"; wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.style = CS_DBLCLKS;
        Require(RegisterClassA(&wc) != 0, "register window class");
        gEmbedded = parent != nullptr;
        if (parent) { RECT r = {}; Require(IsWindow(parent) != FALSE, "invalid parent HWND"); GetClientRect(parent, &r); gWidth = std::max(64L, r.right); gHeight = std::max(64L, r.bottom); }
        RECT rc = { 0, 0, gWidth, gHeight }; if (!parent) AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);
        gWindow = CreateWindowExA(0, wc.lpszClassName, "World Editor - NTL engine", parent ? (WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS) : WS_OVERLAPPEDWINDOW,
            parent ? 0 : 60, parent ? 0 : 60, rc.right - rc.left, rc.bottom - rc.top, parent, nullptr, wc.hInstance, nullptr);
        Require(gWindow != nullptr, "create window");
        if (!hidden && !parent) ShowWindow(gWindow, SW_SHOW);
        Stage("engine create");
        Require(gApp.Create(gWindow, FALSE) != FALSE, "CNtlApplication::Create (RenderWare D3D9 + subsystems)");
        { Value v = Event("ready"); v["hwnd"] = std::to_string((uintptr_t)gWindow); v["toolMode"] = true; v["protocolVersion"] = 1;
          v["renderer"] = "NtlPresentation Release4Tools (dNTL_WORLD_TOOL_MODE) + CNtlPLVisualManager"; Emit(v); }

        if (gNoSky) CNtlPLGlobal::m_bWorldSkyVisible = FALSE;
        // The viewer must never write: in tool mode the engine rewrites every field leaving the streaming ring.
        g_bNtlWorldToolSaveOnUnload = gEditMode ? TRUE : FALSE;
        Stage("create world");
        CreateWorld();
        Stage("world created");
        if (!gPosGiven) { gPos.x = 0; gPos.z = 0; }
        gSnapPending = !gPosGiven || gGround;
        if (!spawnsText.empty()) LoadSpawnsText(spawnsText);
        Emit(Stats("loaded"));

        if (!gCapture.empty()) {
            for (int i = 0; i < gFrames && !gQuit; ++i) {
                MSG msg; while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessage(&msg); }
                if (gSnapPending && i >= 10) { if (SnapToGround(gPos)) gSnapPending = false; }
                RenderFrame(1.f / 30, i == gFrames - 1 ? gCapture : std::string());
            }
            Value s = Stats(); Emit(s);
            if (!gStats.empty()) { FILE* f = fopen(gStats.c_str(), "wb"); if (f) { std::string d = s.dump(); fwrite(d.data(), 1, d.size(), f); fclose(f); } }
        } else {
            std::thread reader([] { std::string line; while (std::getline(std::cin, line)) { if (line.size() > 8 * 1024 * 1024) continue; std::lock_guard<std::mutex> l(gCmdMutex); if (gCmds.size() < 256) gCmds.push_back(line); } std::lock_guard<std::mutex> l(gCmdMutex); gEof = true; });
            reader.detach();
            LARGE_INTEGER freq, last, now; QueryPerformanceFrequency(&freq); QueryPerformanceCounter(&last);
            double stateClock = 0, fpsClock = 0; int fpsFrames = 0;
            while (!gQuit) {
                MSG msg; while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessage(&msg); }
                std::deque<std::string> cmds; { std::lock_guard<std::mutex> l(gCmdMutex); cmds.swap(gCmds); if (gEof && cmds.empty() && (parent != nullptr || hidden)) gQuit = true; }
                for (auto& c : cmds) Command(c);
                QueryPerformanceCounter(&now); double raw = (double)(now.QuadPart - last.QuadPart) / freq.QuadPart; last = now;
                float el = (float)std::min(0.1, raw);
                if (GetFocus() == gWindow) KeyboardMove(el);
                BrushTick(el, (GetKeyState(VK_CONTROL) & 0x8000) != 0);
                PvsTick();
                if (gSnapPending) { if (SnapToGround(gPos)) gSnapPending = false; }
                std::string cap; std::string capId; if (!gPendingCapture.empty()) { cap.swap(gPendingCapture); capId.swap(gPendingCaptureId); }
                RenderFrame(el, cap, capId);
                TickResize();
                fpsFrames++; fpsClock += raw; if (fpsClock >= 0.5) { gFps = (float)(fpsFrames / fpsClock); fpsFrames = 0; fpsClock = 0; }
                stateClock += raw;
                if (stateClock >= 0.5) { stateClock = 0; Emit(Stats("state")); }
                if (!gWorldReadyEmitted && gApp.world && gApp.world->GetWorldFieldMgr()->GetWorldReady()) { gWorldReadyEmitted = true; Emit(Stats("world-ready")); }
                Sleep(gEmbedded && GetForegroundWindow() != GetAncestor(gWindow, GA_ROOT) ? 15 : 1);
            }
        }
        Emit(Event("exit"));
        // Process teardown: the OS reclaims the device; the engine's own shutdown path assumes the full client
        // and, in tool mode, CNtlPLWorldEntity::Destroy would autosave every loaded field.
        ExitProcess(0);
    } catch (const std::exception& e) {
        Value v = Event("error"); v["message"] = std::string(e.what()) + " (during " + gStage + ")"; Emit(v);
        return 1;
    }
}
