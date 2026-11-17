// UI Maker native renderer.
// Draws a window with the client's real GUI library (NtlGui: CDialog, CPanel,
// CStaticBox, CButton) on a RenderWare D3D9 device, the same path the game
// uses (RwIm2D quads + GDI font atlas), and saves the frame as PNG.
//
// Input: a tab-separated scene file produced by the Content Studio
// (UiRenderService.cs). Run with the working directory set to a folder with
//   gui\Font\GuiFont.def   font\*.ttf
// so the font manager registers the same fonts as the client.
//
// usage: ui-render.exe <scene.tsv>
#include "gui_precomp.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <vector>
#include <d3d9.h>
#include <d3dx9.h>

#include <rwcore.h>
#include <rpworld.h>

#include "gui_define.h"
#include "gui_guimanager.h"
#include "gui_resourcemanager.h"
#include "gui_surfacemanager.h"
#include "gui_componentmanager.h"
#include "gui_dialog.h"
#include "gui_panel.h"
#include "gui_staticbox.h"
#include "gui_button.h"
#include "gui_texture.h"
#include "gui_renderer.h"
#include "gui_fontmanager.h"
#include "gui_font.h"
#include "gui_componenttypes.h"
#include "gui_componentmanager.h"
#include "gui_progressbar.h"
#include "gui_inputbox.h"
#include "gui_scrollbar.h"
#include "gui_sliderbar.h"
#include "gui_listbox.h"
#include "gui_htmlbox.h"
#include "gui_outputbox.h"
#include "gui_flash.h"
#include "gui_flash_generic.h"
#include "gui_hypertextbox.h"
#include "gui_frame.h"
#include "gui_anipanel.h"
#include "gui_combobox.h"
#include "NtlFlasherManager.h"
#include <functional>
#include <set>

static void fail(const char* what) { std::fprintf(stderr, "ui-render: %s\n", what); }

static std::vector<std::string> split(const std::string& line)
{
    std::vector<std::string> out; std::string cur;
    for (char c : line) { if (c == '\t') { out.push_back(cur); cur.clear(); } else if (c != '\r') cur.push_back(c); }
    out.push_back(cur); return out;
}
static int I(const std::vector<std::string>& t, size_t i) { return i < t.size() ? std::atoi(t[i].c_str()) : 0; }
static unsigned U(const std::vector<std::string>& t, size_t i) { return i < t.size() ? (unsigned)std::strtoul(t[i].c_str(), 0, 16) : 0; }
static std::wstring fromHex(const std::string& hex)
{
    std::string utf8;
    for (size_t i = 0; i + 1 < hex.size(); i += 2) utf8.push_back((char)std::strtoul(hex.substr(i, 2).c_str(), 0, 16));
    if (utf8.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), 0, 0);
    std::wstring w(n, L'\0'); MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), &w[0], n); return w;
}
static std::string jsonEscape(const std::string& s)
{
    std::string o; for (char c : s) { if (c == '"' || c == '\\') { o.push_back('\\'); o.push_back(c); } else if ((unsigned char)c < 32) o += ' '; else o.push_back(c); } return o;
}

// ---- RenderWare engine ----------------------------------------------------
static HWND gWnd = 0;
static bool openEngine()
{
    gWnd = CreateWindowExA(0, "STATIC", "UI Maker renderer", WS_OVERLAPPEDWINDOW, 0, 0, 640, 480, 0, 0, GetModuleHandleA(0), 0);
    if (!gWnd || !RwEngineInit(0, 0, 1024 * 1024)) { fail("RenderWare engine initialization failed"); return false; }
    if (!RpWorldPluginAttach()) { fail("RpWorld plugin attach failed"); return false; }
    RwEngineOpenParams op = {}; op.displayID = gWnd;
    if (!RwEngineOpen(&op)) { fail("RenderWare engine open failed"); return false; }
    int mode = -1;
    for (int i = 0; i < RwEngineGetNumVideoModes(); ++i) { RwVideoMode info; if (RwEngineGetVideoModeInfo(&info, i) && !(info.flags & rwVIDEOMODEEXCLUSIVE)) { mode = i; break; } }
    if (mode < 0 || !RwEngineSetVideoMode(mode) || !RwEngineStart()) { fail("RenderWare D3D9 engine start failed (graphics device unavailable)"); return false; }
    return true;
}

// Same state the client sets in BeginGuiRenderState (NtlPLRenderState.cpp).
static void beginGuiRenderState()
{
    RwD3D9SetTexture(NULL, 0); RwD3D9SetTexture(NULL, 1);
    RwD3D9SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_MODULATE);
    RwD3D9SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
    RwD3D9SetTextureStageState(0, D3DTSS_COLORARG2, D3DTA_DIFFUSE);
    RwD3D9SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_MODULATE);
    RwD3D9SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
    RwD3D9SetTextureStageState(0, D3DTSS_ALPHAARG2, D3DTA_DIFFUSE);
    RwRenderStateSet(rwRENDERSTATESHADEMODE, (void*)rwSHADEMODEGOURAUD);
    RwRenderStateSet(rwRENDERSTATEZTESTENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE, (void*)FALSE);
    RwRenderStateSet(rwRENDERSTATETEXTURERASTER, (void*)NULL);
    RwRenderStateSet(rwRENDERSTATEVERTEXALPHAENABLE, (void*)TRUE);
    RwRenderStateSet(rwRENDERSTATESRCBLEND, (void*)rwBLENDSRCALPHA);
    RwRenderStateSet(rwRENDERSTATEDESTBLEND, (void*)rwBLENDINVSRCALPHA);
}

// Any image D3DX reads (PNG, TGA, DDS, BMP, JPG) → 8888 RW raster → gui::CTexture,
// exactly like LoadEmbeddedHudArt builds client textures.
static RwTexture* loadRwTexture(const std::string& path)
{
    IDirect3DDevice9* device = (IDirect3DDevice9*)RwD3D9GetCurrentD3DDevice();
    IDirect3DTexture9* src = 0; D3DXIMAGE_INFO info = {};
    if (!device || FAILED(D3DXCreateTextureFromFileExA(device, path.c_str(), D3DX_DEFAULT_NONPOW2, D3DX_DEFAULT_NONPOW2, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, D3DX_FILTER_NONE, D3DX_FILTER_NONE, 0, &info, 0, &src)) || !src)
        return 0;
    D3DSURFACE_DESC desc; src->GetLevelDesc(0, &desc);
    D3DLOCKED_RECT lr;
    if (FAILED(src->LockRect(0, &lr, 0, D3DLOCK_READONLY))) { src->Release(); return 0; }
    RwRaster* raster = RwRasterCreate((RwInt32)desc.Width, (RwInt32)desc.Height, 32, rwRASTERTYPETEXTURE | rwRASTERFORMAT8888);
    unsigned char* bits = raster ? (unsigned char*)RwRasterLock(raster, 0, rwRASTERLOCKWRITE) : 0;
    if (bits)
    {
        const int stride = RwRasterGetStride(raster);
        for (unsigned y = 0; y < desc.Height; ++y) std::memcpy(bits + y * stride, (unsigned char*)lr.pBits + y * lr.Pitch, desc.Width * 4);
        RwRasterUnlock(raster);
    }
    src->UnlockRect(0); src->Release();
    if (!bits) { if (raster) RwRasterDestroy(raster); return 0; }
    RwTexture* texture = RwTextureCreate(raster);
    if (!texture) { RwRasterDestroy(raster); return 0; }
    RwTextureSetFilterMode(texture, rwFILTERLINEAR);
    RwTextureSetAddressing(texture, rwTEXTUREADDRESSCLAMP);
    return texture;
}
static gui::CTexture* loadTexture(const std::string& path)
{
    RwTexture* texture = loadRwTexture(path);
    if (!texture) return 0;
    gui::CTexture* result = NTL_NEW gui::CTexture; result->Load(texture); return result;
}

// RwTextureRead replacement for pages loaded from .rsr: same lookup the client does
// (image path ".\\texture\\gui\\" + name, png/bmp/dds, also names without extension),
// but from plain files prepared by the Studio.
static std::set<std::string> gMissingTextures;
static bool fileExists(const std::string& p) { DWORD a = GetFileAttributesA(p.c_str()); return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY); }
static RwTexture* GuiTextureRead(const RwChar* name, const RwChar*)
{
    std::string n = name ? name : "";
    for (auto& ch : n) if (ch == '/') ch = '\\';
    std::string base = RwImageGetPath() ? RwImageGetPath() : "";
    const char* exts[] = { "", ".png", ".dds", ".tga", ".bmp" };
    for (const std::string& root : { base, std::string(".\\texture\\gui\\"), std::string() })
        for (const char* e : exts)
        {
            std::string p = root + n + e;
            if (!fileExists(p)) continue;
            RwTexture* t = loadRwTexture(p);
            if (t) { RwTextureSetName(t, name); return t; }
        }
    gMissingTextures.insert(n);
    return 0;
}

// A surface that maps source pixels (sx,sy,sw,sh) of tex onto the full component (w x h).
// tex == NULL makes a flat colour quad (same as the client's ColorSurface helper).
static gui::CSurface makeSurface(gui::CTexture* tex, int w, int h, int sx, int sy, int sw, int sh, unsigned argb)
{
    gui::CSurface s; s.m_pTexture = tex;
    PlaneSnapShot& p = s.m_Original;
    p.rtRect.left = 0; p.rtRect.top = 0; p.rtRect.right = w; p.rtRect.bottom = h;
    p.uAlpha = (argb >> 24) & 255; p.uRed = (argb >> 16) & 255; p.uGreen = (argb >> 8) & 255; p.uBlue = argb & 255;
    if (tex)
    {
        float tw = (float)tex->GetWidth(), th = (float)tex->GetHeight();
        if (sw < 0) sw = (int)tw; if (sh < 0) sh = (int)th;   // 0 = degenerate UV, as some .srf define
        p.UVs[0] = sx / tw; p.UVs[1] = sy / th; p.UVs[2] = (sx + sw) / tw; p.UVs[3] = (sy + sh) / th;
    }
    s.m_SnapShot = p; return s;
}

// ---- Flash (Scaleform GFx 3.3 through NtlFlasher, same code path as the client) ----
// The client serves flash files from flash.pak through this callback; here they come from
// plain files under the working directory (flash\X\X.swf, flash\gfxfontlib.swf).
static std::set<std::string> gMissingFlash;
static void FlashFileLoad(const char* name, void** data, int* size)
{
    *data = 0; *size = 0;
    std::string n = name ? name : "";
    for (auto& ch : n) if (ch == '/') ch = '\\';
    char full[MAX_PATH] = {};
    if (!GetFullPathNameA(n.c_str(), MAX_PATH, full, 0)) return;
    HANDLE h = CreateFileA(full, GENERIC_READ, FILE_SHARE_READ, 0, OPEN_EXISTING, 0, 0);
    if (h == INVALID_HANDLE_VALUE) { gMissingFlash.insert(n); return; }
    DWORD len = GetFileSize(h, 0), got = 0;
    BYTE* buffer = NTL_NEW BYTE[len ? len : 1];   // FlasherMemoryFile releases it with NTL_ARRAY_DELETE
    if (!ReadFile(h, buffer, len, &got, 0) || got != len) { CloseHandle(h); NTL_ARRAY_DELETE(buffer); return; }
    CloseHandle(h); *data = buffer; *size = (int)len;
}
static void registerComponentTypes()
{
    using namespace gui;
    CComponentManager::m_stlCompType["frame"] = NTL_NEW CComponentType_Frame;
    CComponentManager::m_stlCompType["panel"] = NTL_NEW CComponentType_Panel;
    CComponentManager::m_stlCompType["anipanel"] = NTL_NEW CComponentType_AniPanel;
    CComponentManager::m_stlCompType["button"] = NTL_NEW CComponentType_Button;
    CComponentManager::m_stlCompType["scrollbar"] = NTL_NEW CComponentType_ScrollBar;
    CComponentManager::m_stlCompType["sliderbar"] = NTL_NEW CComponentType_SliderBar;
    CComponentManager::m_stlCompType["inputbox"] = NTL_NEW CComponentType_InputBox;
    CComponentManager::m_stlCompType["outputbox"] = NTL_NEW CComponentType_OutputBox;
    CComponentManager::m_stlCompType["hypertextbox"] = NTL_NEW CComponentType_HyperTextBox;
    CComponentManager::m_stlCompType["staticbox"] = NTL_NEW CComponentType_StaticBox;
    CComponentManager::m_stlCompType["dialog"] = NTL_NEW CComponentType_Dialog;
    CComponentManager::m_stlCompType["progressbar"] = NTL_NEW CComponentType_ProgressBar;
    CComponentManager::m_stlCompType["listbox"] = NTL_NEW CComponentType_ListBox;
    CComponentManager::m_stlCompType["balloonbox"] = NTL_NEW CComponentType_Balloon;
    CComponentManager::m_stlCompType["tooltipbox"] = NTL_NEW CComponentType_ToolTip;
    CComponentManager::m_stlCompType["combobox"] = NTL_NEW CComponentType_ComboBox;
    CComponentManager::m_stlCompType["tabbutton"] = NTL_NEW CComponentType_TabButton;
    CComponentManager::m_stlCompType["mdstaticbox"] = NTL_NEW CComponentType_MDStaticBox;
    CComponentManager::m_stlCompType["htmlbox"] = NTL_NEW CComponentType_HtmlBox;
    CComponentManager::m_stlCompType["flash"] = NTL_NEW CComponentType_Flash;
    CComponentManager::m_stlCompType["particlebox"] = NTL_NEW CComponentType_ParticleBox;
}
static void collectFlash(gui::CComponent* c, std::vector<gui::CFlash*>& out)
{
    if (auto* f = dynamic_cast<gui::CFlash*>(c)) out.push_back(f);
    for (gui::CComponent* ch : c->GetChildren()) collectFlash(ch, out);
}

struct Metric { std::string id, type; int x, y, w, h, textW, textH, lines; };

// ---- live component tree dump (used to import .frm windows) -----------------
static std::string utf8(const std::wstring& w)
{
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), 0, 0, 0, 0);
    std::string s(n, '\0'); WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, 0, 0); return s;
}
static const char* typeOf(gui::CComponent* c)
{
    if (dynamic_cast<gui::CDialog*>(c)) return "dialog";
    if (dynamic_cast<gui::CButton*>(c)) return "button";
    if (dynamic_cast<gui::CStaticBox*>(c)) return "staticbox";
    if (dynamic_cast<gui::CInputBox*>(c)) return "inputbox";
    if (dynamic_cast<gui::CProgressBar*>(c)) return "progressbar";
    if (dynamic_cast<gui::CScrollBar*>(c)) return "scrollbar";
    if (dynamic_cast<gui::CSliderBar*>(c)) return "sliderbar";
    if (dynamic_cast<gui::CListBox*>(c)) return "listbox";
    if (dynamic_cast<gui::CHtmlBox*>(c)) return "htmlbox";
    if (dynamic_cast<gui::COutputBox*>(c)) return "outputbox";
    if (dynamic_cast<gui::CFlash*>(c)) return "flash";
    if (dynamic_cast<gui::CComboBox*>(c)) return "combobox";
    if (dynamic_cast<gui::CHyperTextBox*>(c)) return "hypertextbox";
    if (dynamic_cast<gui::CAniPanel*>(c)) return "anipanel";
    if (dynamic_cast<gui::CFrame*>(c)) return "frame";
    if (dynamic_cast<gui::CPanel*>(c)) return "panel";
    return "component";
}
static void dumpSurfaces(std::ostream& o, const char* slot, std::list<gui::CSurface>* list, gui::CComponent* owner, bool& first)
{
    if (!list) return;
    CRectangle sr = owner->GetScreenRect();
    for (auto& s : *list)
    {
        const PlaneSnapShot& p = s.m_SnapShot;
        const PlaneSnapShot& q = s.m_Original;
        const char* tn = s.m_pTexture ? s.m_pTexture->GetName() : 0;
        o << (first ? "" : ",") << "{\"slot\":\"" << slot << "\",\"tex\":\"" << jsonEscape(tn ? tn : "")
          << "\",\"tw\":" << (s.m_pTexture ? s.m_pTexture->GetWidth() : 0) << ",\"th\":" << (s.m_pTexture ? s.m_pTexture->GetHeight() : 0)
          << ",\"uv\":[" << p.UVs[0] << "," << p.UVs[1] << "," << p.UVs[2] << "," << p.UVs[3] << "]"
          << ",\"orig\":[" << q.rtRect.left << "," << q.rtRect.top << "," << q.rtRect.GetWidth() << "," << q.rtRect.GetHeight() << "]"
          << ",\"snap\":[" << (p.rtRect.left - sr.left) << "," << (p.rtRect.top - sr.top) << "," << p.rtRect.GetWidth() << "," << p.rtRect.GetHeight() << "]"
          << ",\"argb\":" << (((unsigned)p.uAlpha << 24) | ((unsigned)p.uRed << 16) | ((unsigned)p.uGreen << 8) | p.uBlue) << "}";
        first = false;
    }
}
static void dumpComponent(std::ostream& o, gui::CComponent* c)
{
    CRectangle pos = c->GetPosition(), sr = c->GetScreenRect();
    std::wstring text; bool hasText = false;
    if (auto* sb = dynamic_cast<gui::CStaticBox*>(c)) { text = sb->GetText(); hasText = true; }
    else if (auto* bt = dynamic_cast<gui::CButton*>(c)) if (bt->GetTextComponent()) { text = bt->GetTextComponent()->GetText(); hasText = true; }
    o << "{\"id\":\"" << jsonEscape(c->GetID()) << "\",\"type\":\"" << typeOf(c) << "\",\"rect\":[" << pos.left << "," << pos.top << "," << pos.GetWidth() << "," << pos.GetHeight()
      << "],\"screen\":[" << sr.left << "," << sr.top << "," << sr.GetWidth() << "," << sr.GetHeight() << "],\"visible\":" << (c->IsVisible() ? "true" : "false");
    if (hasText) o << ",\"text\":\"" << jsonEscape(utf8(text)) << "\"";
    o << ",\"surfaces\":[";
    bool first = true;
    if (auto* b = dynamic_cast<gui::CButton*>(c))
    {
        dumpSurfaces(o, "up", b->GetSurfaceUp(), c, first); dumpSurfaces(o, "down", b->GetSurfaceDown(), c, first);
        dumpSurfaces(o, "focus", b->GetSurfaceFocus(), c, first); dumpSurfaces(o, "disable", b->GetSurfaceDisable(), c, first);
    }
    else if (auto* d = dynamic_cast<gui::CDialog*>(c)) dumpSurfaces(o, "base", d->GetSurface(), c, first);
    else if (auto* s = dynamic_cast<gui::CStaticBox*>(c)) dumpSurfaces(o, "base", s->GetSurface(), c, first);
    else if (auto* pb = dynamic_cast<gui::CProgressBar*>(c)) dumpSurfaces(o, "base", pb->GetSurface(), c, first);
    else if (auto* sc = dynamic_cast<gui::CScrollBar*>(c)) { dumpSurfaces(o, "base", sc->GetSurfaceLayout(), c, first); dumpSurfaces(o, "base", sc->GetSurfaceSlider(), c, first); }
    else if (auto* sl = dynamic_cast<gui::CSliderBar*>(c)) { dumpSurfaces(o, "base", sl->GetSurface(), c, first); dumpSurfaces(o, "base", sl->GetSurfaceSlider(), c, first); }
    else if (auto* lb = dynamic_cast<gui::CListBox*>(c)) dumpSurfaces(o, "base", lb->GetSurface(), c, first);
    else if (auto* cb = dynamic_cast<gui::CComboBox*>(c)) { dumpSurfaces(o, "base", cb->GetSurface(), c, first); dumpSurfaces(o, "base", cb->GetStaticBoxSurface(), c, first); }
    else if (auto* ib = dynamic_cast<gui::CInputBox*>(c)) dumpSurfaces(o, "base", ib->GetSurface(), c, first);
    else if (auto* ob = dynamic_cast<gui::COutputBox*>(c)) dumpSurfaces(o, "base", ob->GetSurface(), c, first);
    else if (auto* ht = dynamic_cast<gui::CHyperTextBox*>(c)) dumpSurfaces(o, "base", ht->GetSurface(), c, first);
    else if (auto* ap = dynamic_cast<gui::CAniPanel*>(c)) dumpSurfaces(o, "base", ap->GetSurface(), c, first);
    else if (auto* fr = dynamic_cast<gui::CFrame*>(c)) dumpSurfaces(o, "base", fr->GetSurface(), c, first);
    else if (auto* p = dynamic_cast<gui::CPanel*>(c)) dumpSurfaces(o, "base", p->GetSurface(), c, first);
    o << "],\"children\":[";
    // A button's own text box is part of the button, not a separate component.
    bool firstChild = true;
    gui::CComponent* ownText = 0;
    if (auto* bt = dynamic_cast<gui::CButton*>(c)) ownText = bt->GetTextComponent();
    for (gui::CComponent* ch : c->GetChildren())
    {
        if (ch == ownText) continue;
        o << (firstChild ? "" : ","); dumpComponent(o, ch); firstChild = false;
    }
    o << "]}";
}

int main(int argc, char** argv)
{
    if (argc == 5 && std::string(argv[1]) == "--diff")
    {
        // ui-render.exe --diff a.png b.png diff.png: per-pixel comparison (threshold 24 per channel).
        if (!openEngine()) return 11;
        IDirect3DDevice9* device = (IDirect3DDevice9*)RwD3D9GetCurrentD3DDevice();
        IDirect3DTexture9* img[2] = {};
        for (int i = 0; i < 2; ++i)
            if (FAILED(D3DXCreateTextureFromFileExA(device, argv[2 + i], D3DX_DEFAULT_NONPOW2, D3DX_DEFAULT_NONPOW2, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, D3DX_FILTER_NONE, D3DX_FILTER_NONE, 0, 0, 0, &img[i])))
            { fail("cannot read image for diff"); return 2; }
        D3DSURFACE_DESC da, db; img[0]->GetLevelDesc(0, &da); img[1]->GetLevelDesc(0, &db);
        if (da.Width != db.Width || da.Height != db.Height) { std::printf("{\"ok\":false,\"error\":\"size mismatch\"}\n"); return 3; }
        D3DLOCKED_RECT la, lb; img[0]->LockRect(0, &la, 0, 0); img[1]->LockRect(0, &lb, 0, D3DLOCK_READONLY);
        unsigned long long diff = 0, total = (unsigned long long)da.Width * da.Height;
        for (unsigned y = 0; y < da.Height; ++y)
        {
            unsigned* pa = (unsigned*)((BYTE*)la.pBits + y * la.Pitch); const unsigned* pb = (const unsigned*)((const BYTE*)lb.pBits + y * lb.Pitch);
            for (unsigned x = 0; x < da.Width; ++x)
            {
                int m = 0;
                for (int s = 0; s < 24; s += 8) { int d = (int)((pa[x] >> s) & 255) - (int)((pb[x] >> s) & 255); if (d < 0) d = -d; if (d > m) m = d; }
                if (m > 24) { ++diff; pa[x] = 0xFFFF2050; } else { unsigned g = (((pa[x] >> 16) & 255) + ((pa[x] >> 8) & 255) + (pa[x] & 255)) / 6; pa[x] = 0xFF000000 | (g << 16) | (g << 8) | g; }
            }
        }
        img[0]->UnlockRect(0); img[1]->UnlockRect(0);
        IDirect3DSurface9* s0 = 0; img[0]->GetSurfaceLevel(0, &s0); D3DXSaveSurfaceToFileA(argv[4], D3DXIFF_PNG, s0, 0, 0); s0->Release();
        std::printf("{\"ok\":true,\"differentPixels\":%llu,\"totalPixels\":%llu,\"percent\":%.4f}\n", diff, total, total ? 100.0 * diff / total : 0.0);
        std::fflush(stdout); ExitProcess(0);
    }
    if (argc != 2) { fail("usage: ui-render.exe <scene.tsv> | --diff a.png b.png out.png"); return 2; }
    std::ifstream in(argv[1], std::ios::binary);
    if (!in) { fail("cannot read scene file"); return 2; }
    std::vector<std::vector<std::string>> lines; std::string line;
    while (std::getline(in, line)) if (!line.empty() && line[0] != '#') lines.push_back(split(line));

    int sw = 1280, sh = 720; unsigned clear = 0xFF000000; std::string outPng, outMetrics;
    for (auto& t : lines)
    {
        if (t[0] == "screen") { sw = I(t, 1); sh = I(t, 2); }
        else if (t[0] == "clear") clear = U(t, 1);
        else if (t[0] == "out") outPng = t[1];
        else if (t[0] == "metrics") outMetrics = t[1];
    }
    if (sw < 64 || sh < 64 || sw > 4096 || sh > 4096 || outPng.empty()) { fail("invalid screen size or missing out"); return 2; }
    if (!openEngine()) return 11;

    // GUI managers, in the client's order (CNtlPLGuiManager::Create).
    gui::CResourceManager* resMgr = NTL_NEW gui::CResourceManager;
    gui::CSurfaceManager* surMgr = NTL_NEW gui::CSurfaceManager(resMgr);
    gui::CComponentManager* compMgr = NTL_NEW gui::CComponentManager;
    gui::CGUIManager* guiMgr = NTL_NEW gui::CGUIManager(CRectangle(0, 0, sw, sh));
    if (!gui::GetGuiFontManager()->Initialize("gui\\Font\\GuiFont.def")) { fail("font manager initialization failed (gui\\Font\\GuiFont.def)"); return 12; }

    std::map<std::string, gui::CTexture*> textures;
    std::vector<std::string> warnings;
    // Same component types and Flash manager the client sets up in CSetupGUI::Init.
    registerComponentTypes();
    RwTextureSetReadCallBack(GuiTextureRead);
    LinkFlashResourceLoad(FlashFileLoad);
    bool flashReady = gui::CFlash_Generic::CreateFlashManager(gWnd) != FALSE;
    if (!flashReady) warnings.push_back("flash manager could not be created (GFx)");
    else
    {
        if (fileExists("gui\\Font\\FlashFont.def") && !gui::CFlash_Generic::ParseFlashFont("gui\\Font\\FlashFont.def")) warnings.push_back("FlashFont.def could not be parsed");
        gui::CFlash_Generic::SetScreenSize(sw, sh);
    }
    COMPPAGE_HANDLE frmPage = 0; bool hasFrm = false;
    gui::CFlash* lastFlash = 0;
    std::vector<gui::CComponent*> parentStack; int clipDepth = 0;
    auto clipIf = [&](gui::CComponent* comp) { if (clipDepth > 0) comp->SetClippingMode(true); return comp; };
    float flashTime = 0.f;
    std::string dumpPath;
    gui::CComponent* parent = guiMgr;
    // Backdrop dialog: screen-space layer for background capture and anything outside the window.
    gui::CDialog* backdrop = NTL_NEW gui::CDialog(CRectangle(0, 0, sw, sh), guiMgr, surMgr);
    parent = backdrop;
    gui::CButton* lastButton = 0; std::string lastButtonState;
    int dialogX = 0, dialogY = 0;
    std::vector<Metric> metrics;
    std::vector<std::pair<gui::CStaticBox*, size_t>> labelMetrics;
    std::vector<std::pair<gui::CButton*, size_t>> buttonMetrics;

    auto tex = [&](const std::string& name) -> gui::CTexture* {
        if (name.empty() || name == "-") return 0;
        auto it = textures.find(name); return it == textures.end() ? 0 : it->second;
    };

    for (auto& t : lines)
    {
        const std::string& k = t[0];
        if (k == "tex")
        {
            gui::CTexture* p = loadTexture(t[2]);
            if (!p) warnings.push_back("texture not loaded: " + t[2]);
            textures[t[1]] = p;
        }
        else if (k == "dialog")
        {
            dialogX = I(t, 1); dialogY = I(t, 2);
            CRectangle rc; rc.SetRectWH(dialogX, dialogY, I(t, 3), I(t, 4));
            parent = NTL_NEW gui::CDialog(rc, guiMgr, surMgr);
        }
        else if (k == "panel")
        {
            // panel id x y w h tex sx sy sw sh argb
            CRectangle rc; rc.SetRectWH(I(t, 2), I(t, 3), I(t, 4), I(t, 5));
            gui::CTexture* p = tex(t[6]);
            if (!p && t[6] != "-") warnings.push_back(t[1] + ": missing texture " + t[6]);
            clipIf(NTL_NEW gui::CPanel(rc, parent, surMgr, makeSurface(p, I(t, 4), I(t, 5), I(t, 7), I(t, 8), I(t, 9), I(t, 10), U(t, 11))));
            metrics.push_back({ t[1], "panel", I(t, 2), I(t, 3), I(t, 4), I(t, 5), 0, 0, 0 });
        }
        else if (k == "label")
        {
            // label id x y w h size attr style rgb effect effectRgb textHex
            CRectangle rc; rc.SetRectWH(I(t, 2), I(t, 3), I(t, 4), I(t, 5));
            gui::CStaticBox* p = NTL_NEW gui::CStaticBox(rc, parent, surMgr, (DWORD)I(t, 8)); clipIf(p);
            std::string font = t.size() > 13 && !t[13].empty() ? t[13] : std::string(DEFAULT_FONT);
            p->CreateFontStd((CHAR*)font.c_str(), I(t, 6), I(t, 7));
            unsigned rgb = U(t, 9);
            p->SetTextColor(RGB((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
            if (I(t, 10)) { unsigned e = U(t, 11); p->SetEffectMode(I(t, 10)); p->SetEffectColor(RGB((e >> 16) & 255, (e >> 8) & 255, e & 255)); }
            p->SetText(fromHex(t.size() > 12 ? t[12] : "").c_str());
            metrics.push_back({ t[1], "label", I(t, 2), I(t, 3), I(t, 4), I(t, 5), 0, 0, 0 });
            labelMetrics.push_back({ p, metrics.size() - 1 });
        }
        else if (k == "button")
        {
            // button id x y w h size attr style rgb enabled state textHex
            CRectangle rc; rc.SetRectWH(I(t, 2), I(t, 3), I(t, 4), I(t, 5));
            std::wstring text = fromHex(t.size() > 12 ? t[12] : "");
            gui::CButton* p = NTL_NEW gui::CButton(rc, std::wstring(), parent, surMgr); clipIf(p);
            std::string font = t.size() > 13 && !t[13].empty() ? t[13] : std::string(DEFAULT_FONT);
            p->SetTextFont((CHAR*)font.c_str(), I(t, 6), I(t, 7));
            p->SetTextStyle((DWORD)I(t, 8));
            unsigned rgb = U(t, 9); COLORREF c = RGB((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            lastButtonState = t.size() > 11 ? t[11] : "normal";
            // Forced preview states paint with that state's text colour.
            p->SetTextUpColor(c); p->SetTextFocusColor(c); p->SetTextDownColor(lastButtonState == "down" ? RGB(255, 210, 29) : c);
            p->SetTextDisableColor(c);
            if (!text.empty()) p->SetText(text);
            if (!I(t, 10)) p->Enable(false);
            lastButton = p;
            metrics.push_back({ t[1], "button", I(t, 2), I(t, 3), I(t, 4), I(t, 5), 0, 0, 0 });
            buttonMetrics.push_back({ p, metrics.size() - 1 });
        }
        else if (k == "bsurf" && lastButton)
        {
            // bsurf slot tex sx sy sw sh argb w h — "up" receives the forced preview state.
            // bsurf slot tex sx sy sw sh argb w h [dx dy]
            gui::CSurface s = makeSurface(tex(t[2]), I(t, 8), I(t, 9), I(t, 3), I(t, 4), I(t, 5), I(t, 6), U(t, 7));
            if (t.size() > 11) { int dx = I(t, 10), dy = I(t, 11); s.m_Original.rtRect.left += dx; s.m_Original.rtRect.right += dx; s.m_Original.rtRect.top += dy; s.m_Original.rtRect.bottom += dy; s.m_SnapShot = s.m_Original; }
            const std::string& slot = t[1];
            if (slot == "all") { lastButton->AddSurfaceUp(s); lastButton->AddSurfaceDown(s); lastButton->AddSurfaceFocus(s); lastButton->AddSurfaceDisabled(s); }
            else if (slot == "up") lastButton->AddSurfaceUp(s);
            else if (slot == "down") lastButton->AddSurfaceDown(s);
            else if (slot == "focus") lastButton->AddSurfaceFocus(s);
            else if (slot == "disabled") lastButton->AddSurfaceDisabled(s);
        }
        else if (k == "button-disable" && lastButton) lastButton->Enable(false);
        else if (k == "rsr") { if (!resMgr->AddPage(t[1])) warnings.push_back("rsr page not loaded: " + t[1]); }
        else if (k == "srf") { if (!surMgr->AddPage(t[1])) warnings.push_back("srf page not loaded: " + t[1]); }
        else if (k == "frm")
        {
            // frm <path> <root id|-> <center 0|1>: the window exactly as the client builds it from its scripts.
            std::list<gui::CComponent*> before = guiMgr->GetChildren();
            frmPage = compMgr->AddPage(t[1]);
            if (!compMgr->CreateComponents(frmPage, guiMgr, surMgr)) { fail(("frm could not be created: " + t[1]).c_str()); return 15; }
            hasFrm = true;
            gui::CComponent* root = (t.size() > 2 && t[2] != "-") ? compMgr->GetComponent(frmPage, t[2]) : 0;
            for (gui::CComponent* ch : guiMgr->GetChildren())
            {
                if (std::find(before.begin(), before.end(), ch) != before.end()) continue;
                if (!root) root = ch;
                else if (ch != root) ch->Show(false);   // other top-level dialogs of the same frm
            }
            if (root)
            {
                root->Show(true);
                if (I(t, 3)) { CRectangle r = root->GetScreenRect(); root->SetPosition((sw - r.GetWidth()) / 2, (sh - r.GetHeight()) / 2); }
                CRectangle r = root->GetScreenRect(); dialogX = r.left; dialogY = r.top;
            }
        }
        else if ((k == "frm-text" || k == "frm-show") && hasFrm)
        {
            gui::CComponent* comp = compMgr->GetComponent(frmPage, t[1]);
            if (!comp) { warnings.push_back(k + ": no component " + t[1]); continue; }
            if (k == "frm-show") comp->Show(I(t, 2) != 0);
            else if (auto* sb = dynamic_cast<gui::CStaticBox*>(comp)) sb->SetText(fromHex(t[2]).c_str());
            else if (auto* bt = dynamic_cast<gui::CButton*>(comp)) bt->SetText(fromHex(t[2]));
        }
        else if (k == "flash")
        {
            // flash id x y w h <name.swf> <frame>: loads flash\<name>\<name>.swf like CFlash in the client.
            CRectangle rc; rc.SetRectWH(I(t, 2), I(t, 3), I(t, 4), I(t, 5));
            if (!flashReady) { warnings.push_back(t[1] + ": flash manager unavailable"); continue; }
            lastFlash = NTL_NEW gui::CFlash(rc, parent, surMgr, t[6].c_str(), TRUE, FALSE, FALSE); clipIf(lastFlash);
            if (I(t, 7) >= 0) lastFlash->GotoFrame((DWORD)I(t, 7));
            metrics.push_back({ t[1], "flash", I(t, 2), I(t, 3), I(t, 4), I(t, 5), lastFlash->GetFrameCount(), (int)lastFlash->GetCurrentFrame(), 0 });
        }
        else if (k == "flashvar" && lastFlash) lastFlash->SetVariable(t[1].c_str(), fromHex(t[2]).c_str());
        else if (k == "flashtime") flashTime = (float)std::atof(t[1].c_str());
        else if (k == "dump") dumpPath = t[1];
        else if (k == "group-begin")
        {
            // Clipping container (layout "clip": true): children are clipped to it, as in NtlGui.
            CRectangle rc; rc.SetRectWH(I(t, 2), I(t, 3), I(t, 4), I(t, 5));
            gui::CDialog* g = NTL_NEW gui::CDialog(rc, parent, surMgr); clipIf(g);
            parentStack.push_back(parent); parent = g; ++clipDepth;
        }
        else if (k == "group-end" && !parentStack.empty()) { parent = parentStack.back(); parentStack.pop_back(); --clipDepth; }
    }

    // Flash: run frame actions (Advance(0)) and optionally play forward like the client's Update(dt).
    if (flashReady)
    {
        std::vector<gui::CFlash*> flashes; collectFlash(guiMgr, flashes);
        // .frm flash with an empty rect is sized by client code at runtime (typically to its dialog): fill the parent.
        for (auto* fl : flashes)
        {
            CRectangle r = fl->GetPosition();
            if ((r.GetWidth() <= 1 || r.GetHeight() <= 1) && fl->GetParent())
            {
                CRectangle pr = fl->GetParent()->GetScreenRect();
                CRectangle frame = fl->GetFrameResolution();
                if (frame.GetWidth() > 1 && frame.GetHeight() > 1 && frame.GetWidth() < pr.GetWidth() && frame.GetHeight() < pr.GetHeight())
                    fl->SetPosition(CRectangle((pr.GetWidth() - frame.GetWidth()) / 2, (pr.GetHeight() - frame.GetHeight()) / 2, (pr.GetWidth() + frame.GetWidth()) / 2, (pr.GetHeight() + frame.GetHeight()) / 2));
                else fl->SetPosition(CRectangle(0, 0, pr.GetWidth(), pr.GetHeight()));
                warnings.push_back("flash " + fl->GetID() + ": rect vacio en el .frm, se ajusto al contenedor (en el juego lo posiciona el codigo)");
            }
        }
        for (auto* fl : flashes) fl->Update(0.f);
        for (float tm = 0.f; tm < flashTime; tm += 1.f / 30.f) for (auto* fl : flashes) fl->Update(1.f / 30.f);
    }
    if (!dumpPath.empty())
    {
        std::ofstream dj(dumpPath, std::ios::binary);
        dj << "{\"screen\":[" << sw << "," << sh << "],\"components\":[";
        bool first = true;
        for (gui::CComponent* ch : guiMgr->GetChildren())
        {
            if (ch == backdrop || !ch->IsVisible()) continue;
            dj << (first ? "" : ","); dumpComponent(dj, ch); first = false;
        }
        dj << "],\"missingTextures\":[";
        first = true; for (auto& m : gMissingTextures) { dj << (first ? "" : ",") << "\"" << jsonEscape(m) << "\""; first = false; }
        dj << "],\"missingFlash\":[";
        first = true; for (auto& m : gMissingFlash) { dj << (first ? "" : ",") << "\"" << jsonEscape(m) << "\""; first = false; }
        dj << "]}";
    }
    for (auto& m : gMissingTextures) warnings.push_back("gui texture not found: " + m);
    for (auto& m : gMissingFlash) warnings.push_back("flash file not found: " + m);

    for (auto& lm : labelMetrics)
    {
        Metric& m = metrics[lm.second];
        m.textW = lm.first->GetFitWidthToString(); m.textH = lm.first->GetFitHeightToString(); m.lines = lm.first->GetLineCount();
    }

    // Render to an offscreen camera texture with the GUI render state.
    RwRaster* target = RwRasterCreate(sw, sh, 0, rwRASTERTYPECAMERATEXTURE);
    RwRaster* zbuffer = RwRasterCreate(sw, sh, 0, rwRASTERTYPEZBUFFER);
    RwCamera* camera = RwCameraCreate(); RwFrame* frame = RwFrameCreate();
    if (!target || !zbuffer || !camera || !frame) { fail("offscreen camera creation failed"); return 13; }
    RwCameraSetFrame(camera, frame); RwCameraSetRaster(camera, target); RwCameraSetZRaster(camera, zbuffer);
    RwCameraSetNearClipPlane(camera, 0.1f); RwCameraSetFarClipPlane(camera, 100.f);
    RwRGBA bg = { (RwUInt8)((clear >> 16) & 255), (RwUInt8)((clear >> 8) & 255), (RwUInt8)(clear & 255), (RwUInt8)((clear >> 24) & 255) };
    RwCameraClear(camera, &bg, rwCAMERACLEARIMAGE | rwCAMERACLEARZ);
    bool saved = false;
    if (RwCameraBeginUpdate(camera))
    {
        beginGuiRenderState();
        g_GuiRenderer.ResetRender();
        guiMgr->Show();
        g_GuiRenderer.Render();
        IDirect3DDevice9* device = (IDirect3DDevice9*)RwD3D9GetCurrentD3DDevice();
        IDirect3DSurface9* rt = 0;
        if (device && SUCCEEDED(device->GetRenderTarget(0, &rt)) && rt)
        {
            saved = SUCCEEDED(D3DXSaveSurfaceToFileA(outPng.c_str(), D3DXIFF_PNG, rt, 0, 0));
            rt->Release();
        }
        RwCameraEndUpdate(camera);
    }
    if (!saved) { fail("could not save the rendered frame"); return 14; }

    if (!outMetrics.empty())
    {
        std::ofstream mj(outMetrics, std::ios::binary);
        mj << "{\"screen\":[" << sw << "," << sh << "],\"dialog\":[" << dialogX << "," << dialogY << "],\"components\":[";
        for (size_t i = 0; i < metrics.size(); ++i)
        {
            const Metric& m = metrics[i];
            mj << (i ? "," : "") << "{\"id\":\"" << jsonEscape(m.id) << "\",\"type\":\"" << m.type << "\",\"rect\":[" << m.x << "," << m.y << "," << m.w << "," << m.h
               << "],\"textWidth\":" << m.textW << ",\"textHeight\":" << m.textH << ",\"lines\":" << m.lines << "}";
        }
        mj << "],\"warnings\":[";
        for (size_t i = 0; i < warnings.size(); ++i) mj << (i ? "," : "") << "\"" << jsonEscape(warnings[i]) << "\"";
        mj << "]}";
    }
    std::printf("{\"ok\":true,\"png\":\"%s\"}\n", jsonEscape(outPng).c_str());
    // Process exit releases the device; the GUI tree has no persistent state.
    std::fflush(stdout);
    ExitProcess(0);
}
