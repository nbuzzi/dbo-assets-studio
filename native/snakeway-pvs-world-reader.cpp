// Read-only native probe for the packed Snake Way BSP. It does not render or
// alter geometry; gameplay renderer validation belongs to the full client.
#include "precomp_ntlpresentation.h"
#include "NtlPLResourcePack.h"
#include "NtlAtomic.h"
#include "NtlMaterialExt.h"
#include "NtlPLGlobal.h"
#include "NtlPLPicking.h"
#include <rtfsyst.h>
#include <rtanim.h>
#include <rpcollis.h>
#include <rppvs.h>
#include <rpltmap.h>
#include <rphanim.h>
#include <rpskin.h>
#include <rptoon.h>
#include <rpusrdat.h>
#include <rpid.h>
#include <rpuvanim.h>
#include <cstdio>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <set>
#include <string>

static void Need(bool value, const char* message)
{
    if (!value)
    {
        std::fprintf(stderr, "FAIL %s\n", message);
        std::exit(1);
    }
}

static std::set<std::string> gTextures;

static RwTexture* LoadPackedTexture(const RwChar* name, const RwChar* mask)
{
    Need(name && std::strncmp(name, "orig_sw_", 8) == 0 && (!mask || !mask[0]), "Snake Way texture namespace and mask");
    for (const unsigned char* ch = reinterpret_cast<const unsigned char*>(name); *ch; ++ch)
        Need((*ch >= 'a' && *ch <= 'z') || (*ch >= 'A' && *ch <= 'Z') ||
            (*ch >= '0' && *ch <= '9') || *ch == '_' || *ch == '-', "safe packed texture name");
    RwTexture* texture = GetNtlResourcePackManager()->LoadTexture(
        "texture/ntlwe/indoor/;texture/object/;texture/ntlwe/ltmap/6009/bsplm/", name, mask);
    Need(texture && RwTextureGetRaster(texture) && RwRasterGetWidth(RwTextureGetRaster(texture)) > 0,
        "actual client PAK texture load");
    gTextures.insert(name);
    return texture;
}

struct WorldCounts
{
    unsigned sectors = 0;
    unsigned vertices = 0;
    unsigned triangles = 0;
};

static RpWorldSector* CountSector(RpWorldSector* sector, void* data)
{
    WorldCounts& counts = *static_cast<WorldCounts*>(data);
    ++counts.sectors;
    counts.vertices += sector->numVertices;
    counts.triangles += sector->numTriangles;
    return sector;
}

struct CollisionHit
{
    bool found = false;
    float ratio = 1.0f;
    float y = 0.0f;
    unsigned materialIndex = 0;
};

static RpCollisionTriangle* RecordHit(RpIntersection* intersection, RpWorldSector* sector,
    RpCollisionTriangle* triangle, RwReal ratio, void* data)
{
    CollisionHit& hit = *static_cast<CollisionHit*>(data);
    if (ratio < hit.ratio)
    {
        hit.found = true;
        hit.ratio = ratio;
        hit.y = intersection->t.line.start.y +
            (intersection->t.line.end.y - intersection->t.line.start.y) * ratio;
        Need(sector && triangle->index >= 0 && triangle->index < sector->numTriangles, "native collision triangle index");
        hit.materialIndex = sector->matListWindowBase + sector->triangles[triangle->index].matIndex;
    }
    return triangle;
}

static CollisionHit ProbeLine(RpWorld* world, const RwLine& line)
{
    RpIntersection ray = {};
    ray.type = rpINTERSECTLINE;
    ray.t.line = line;
    CollisionHit hit;
    RpCollisionWorldForAllIntersections(world, &ray, RecordHit, &hit);
    return hit;
}

static CollisionHit ProbeCollision(RpWorld* world, const RwBBox* bounds, float x, float z)
{
    RwLine line = {};
    line.start = { x, bounds->sup.y, z };
    line.end = { x, bounds->inf.y, z };
    return ProbeLine(world, line);
}

static CollisionHit ProbeProductionLine(RpWorld* world, float x, float z, float baseY,
    bool& found, float& hitY)
{
    RwLine line = {};
    line.start = { x, baseY + 2.0f, z };
    line.end = { x, baseY - 200.0f, z };
    CNtlPLGlobal::SetActiveWorld(world);
    SWorldIntersect intersection = {};
    found = Collision_IndoorIntersectionLineTopDown(line, intersection) != FALSE;
    hitY = found ? intersection.vCollPos.y : 0.0f;
    CollisionHit material = ProbeLine(world, line);
    Need(found == material.found && (!found || std::fabs(hitY - material.y) < 0.001f),
        "production collision ray agrees with native material trace");
    return material;
}

static void PrintCollision(const char* probe, bool attempted, float x, float z,
    float startY, float endY, bool found, float y, bool hasMaterial, unsigned materialIndex)
{
    static bool first = true;
    std::printf("%s{\"probe\":\"%s\",\"attempted\":%s,\"x\":%.9g,\"z\":%.9g,\"rayStartY\":%.9g,\"rayEndY\":%.9g,\"hit\":%s,\"y\":",
        first ? "" : ",", probe, attempted ? "true" : "false", x, z, startY, endY, found ? "true" : "false");
    first = false;
    if (found) std::printf("%.9g", y); else std::printf("null");
    std::printf(",\"materialIndex\":");
    if (found && hasMaterial) std::printf("%u", materialIndex); else std::printf("null");
    std::printf("}");
}

static void AttachPlugins()
{
    Need(RpWorldPluginAttach() && RpPVSPluginAttach() && RpCollisionPluginAttach() &&
        RtAnimInitialize() && RpLtMapPluginAttach() && RpHAnimPluginAttach() &&
        RpSkinPluginAttach() && RpToonPluginAttach() && RpUserDataPluginAttach() &&
        RpIdPluginAttach() && RpMatFXPluginAttach() && RpUVAnimPluginAttach() &&
        RpNtlAtomicPluginAttach() && RpNtlMatFxPluginAttach(), "native client RenderWare plugins");
}

int main(int argc, char** argv)
{
    if (argc != 2)
        return 2;

    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    Need(SetCurrentDirectoryA(argv[1]) != 0, "private client root");
    HWND window = CreateWindowA("STATIC", "AssetsSnakeWayPvsRead", 0, 0, 0, 64, 64,
        NULL, NULL, GetModuleHandle(NULL), NULL);
    Need(window && RwEngineInit(NULL, 0, 1024 * 1024), "hidden native RenderWare init");
    AttachPlugins();
    RwEngineOpenParams open = {};
    open.displayID = window;
    Need(RwEngineOpen(&open), "hidden native device open");
    int mode = -1;
    for (int i = 0; i < RwEngineGetNumVideoModes(); ++i)
    {
        RwVideoMode video = {};
        if (RwEngineGetVideoModeInfo(&video, i) && !(video.flags & rwVIDEOMODEEXCLUSIVE))
        {
            mode = i;
            break;
        }
    }
    Need(mode >= 0 && RwEngineSetVideoMode(mode) && RwEngineStart(), "native RenderWare engine start");

    Need(RtFSManagerOpen(RTFSMAN_UNLIMITED_NUM_FS), "native filesystem manager");
    char drive[] = "D:", driveName[] = "D";
    auto* fileSystem = RtWinFSystemInit(100, drive, driveName);
    Need(fileSystem && RtFSManagerRegister(fileSystem), "native drive registration");
    RtFSManagerSetDefaultFileSystem(fileSystem);

    CNtlPLResoucePackManager* packs = GetNtlResourcePackManager();
    Need(packs->LoadPackHeader() == NTL_FILEPACK_SUCCESS, "native client PAK headers");
    packs->SetActiveTypeFlags(NTL_PACK_TYPE_FLAG_TEXTURE | NTL_PACK_TYPE_FLAG_TERRAIN);
    RwTextureSetReadCallBack(LoadPackedTexture);

    const char* resource = "world/dbo_assets_snakeway_001/dbo_assets_snakeway_001.bsp";
    SPackResFileData packed = {};
    Need(packs->LoadTerrain(resource, packed), "actual client LoadTerrain");
    char resolvedPack[MAX_PATH] = {};
    Need(_fullpath(resolvedPack, packed.strPackFileName.c_str(), MAX_PATH) != NULL, "resolved PAK data path");
    HANDLE packGuard = CreateFileA(resolvedPack, GENERIC_READ, FILE_SHARE_READ, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    Need(packGuard != INVALID_HANDLE_VALUE, "read-only PAK data lock");
    void* file = RwFopen(packed.strPackFileName.c_str(), "rb");
    Need(file && RwFseek(file, packed.uiOffset, RTFILE_POS_BEGIN) == 0, "resolved PAK BSP offset");
    RwStream* stream = RwStreamOpen(rwSTREAMFILE, rwSTREAMREAD, file);
    RwChunkHeaderInfo header = {};
    Need(stream && RwStreamReadChunkHeaderInfo(stream, &header) && header.type == rwID_WORLD &&
        header.length + 12 == packed.uiSize, "native BSP resource boundary");
    RpWorld* world = RpWorldStreamRead(stream);
    Need(world != NULL, "actual packed RpWorldStreamRead");
    RwStreamClose(stream, NULL);
    RwFclose(file);
    CloseHandle(packGuard);

    WorldCounts counts;
    RpWorldForAllWorldSectors(world, CountSector, &counts);
    const RwBBox* bounds = RpWorldGetBBox(world);
    Need(bounds && counts.vertices == 6138 && counts.triangles == 5824 &&
        RpWorldGetNumMaterials(world) == 10 && gTextures.size() == 7,
        "exact v4 BSP geometry and packed texture bindings");
    CollisionHit bboxSpawn = ProbeCollision(world, bounds, 20.96935f, -83.27369f);
    CollisionHit bboxNico = ProbeCollision(world, bounds, 21.8f, -82.7f);
    bool productionSpawnFound = false, productionNicoFound = false, fallbackFound = false;
    float productionSpawnY = 0.0f, productionNicoY = 0.0f, fallbackY = 0.0f;
    CollisionHit productionSpawn = ProbeProductionLine(world, 20.96935f, -83.27369f, 49.739807f,
        productionSpawnFound, productionSpawnY);
    CollisionHit productionNico = ProbeProductionLine(world, 21.8f, -82.7f, 40.8f,
        productionNicoFound, productionNicoY);
    CollisionHit fallback;
    const bool fallbackAttempted = !productionNicoFound;
    if (fallbackAttempted)
        fallback = ProbeProductionLine(world, 21.8f, -82.7f, 49.74f, fallbackFound, fallbackY);

    std::string packPath = packed.strPackFileName;
    for (char& ch : packPath) if (ch == '\\') ch = '/';
    std::printf("{\"resourcePath\":\"%s\",\"packFile\":\"%s\",\"payloadOffset\":%u,\"payloadBytes\":%u,"
        "\"hasPvs\":%s,\"bbox\":{\"inf\":[%.9g,%.9g,%.9g],\"sup\":[%.9g,%.9g,%.9g]},"
        "\"sectors\":%u,\"vertices\":%u,\"triangles\":%u,\"materials\":%d,\"textureCount\":%u,\"collision\":[",
        resource, packPath.c_str(), packed.uiOffset, packed.uiSize,
        RpPVSQuery(world) ? "true" : "false", bounds->inf.x, bounds->inf.y, bounds->inf.z,
        bounds->sup.x, bounds->sup.y, bounds->sup.z, counts.sectors,
        counts.vertices, counts.triangles, RpWorldGetNumMaterials(world), static_cast<unsigned>(gTextures.size()));
    PrintCollision("bbox-all-geometry", true, 20.96935f, -83.27369f, bounds->sup.y, bounds->inf.y,
        bboxSpawn.found, bboxSpawn.y, bboxSpawn.found, bboxSpawn.materialIndex);
    PrintCollision("bbox-all-geometry", true, 21.8f, -82.7f, bounds->sup.y, bounds->inf.y,
        bboxNico.found, bboxNico.y, bboxNico.found, bboxNico.materialIndex);
    PrintCollision("production-spawn", true, 20.96935f, -83.27369f, 49.739807f + 2.0f, 49.739807f - 200.0f,
        productionSpawnFound, productionSpawnY, productionSpawnFound, productionSpawn.materialIndex);
    PrintCollision("production-review", true, 21.8f, -82.7f, 40.8f + 2.0f, 40.8f - 200.0f,
        productionNicoFound, productionNicoY, productionNicoFound, productionNico.materialIndex);
    PrintCollision("production-review-expected-height-fallback", fallbackAttempted, 21.8f, -82.7f,
        49.74f + 2.0f, 49.74f - 200.0f, fallbackFound, fallbackY, fallbackFound, fallback.materialIndex);
    std::printf("],\"textures\":[");
    bool first = true;
    for (const std::string& texture : gTextures)
    {
        std::printf("%s\"%s\"", first ? "" : ",", texture.c_str());
        first = false;
    }
    std::printf("]}\n");

    CNtlPLGlobal::SetActiveWorld(NULL);
    RpWorldDestroy(world);
    RtFSManagerClose();
    RwEngineStop();
    RwEngineClose();
    RwEngineTerm();
    DestroyWindow(window);
    return 0;
}
