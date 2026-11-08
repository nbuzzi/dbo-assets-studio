// Current-client Toon metadata/binding observer. Does not draw or write assets.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "precomp_ntlpresentation.h"
#include "NtlPLEntityRenderHelpers.h"
#define main MediaPreviewEntryNotInvoked
#include "media-helper.cpp"
#undef main
#include <sstream>

static void ToonRequire(bool ok,const char* why){if(!ok){fprintf(stderr,"FAIL %s\n",why);exit(1);}}
struct ToonObservation {SToonData* data;bool after=false,first=true;unsigned atomics=0,present=0;std::ostringstream json;};
static RpAtomic* ToonAtomic(RpAtomic* atomic,void* user){
    auto& out=*(ToonObservation*)user;RpGeometry* geometry=RpAtomicGetGeometry(atomic);
    ToonRequire(geometry&&++out.atomics<=1024,"bounded native atomic geometry");
    int verts=RpGeometryGetNumVertices(geometry),triangles=RpGeometryGetNumTriangles(geometry);
    ToonRequire(verts>0&&verts<=1000000&&triangles>=0&&triangles<=1000000,"native geometry bounds");
    auto* toon=RpToonGeometryGetToonGeo(geometry);bool skin=RpSkinGeometryGetSkin(geometry)!=0;
    if(!out.first)out.json<<",";out.first=false;
    out.json<<"{\"atomic\":"<<out.atomics-1<<",\"vertices\":"<<verts<<",\"triangles\":"<<triangles
        <<",\"skin\":"<<(skin?"true":"false")<<",\"toonPresent\":"<<(toon?"true":"false")
        <<",\"nativeAtomicFlags\":"<<RpNtlAtomicGetFlag(atomic)<<",\"skinType\":";
    // RpSkinAtomicGetType dereferences the skin pipeline. Rigid atomics may
    // have no pipeline at this point; the production helper also gates on skin.
    if(skin)out.json<<(int)RpSkinAtomicGetType(atomic);else out.json<<"null";
    if(toon){
        ++out.present;
        ToonRequire(toon->numVerts==verts&&toon->numTriangles==triangles&&toon->parentGeometry==geometry&&
            toon->numEdges>=0&&toon->numEdges<=2000000&&toon->numCreaseEdges>=0&&toon->numCreaseEdges<=toon->numEdges&&
            toon->inkIDCount>0&&toon->inkIDCount<=256&&toon->inkIDList&&toon->inks&&
            (toon->numEdges==0||(toon->edges&&toon->edgeInkIDs))&&toon->vertexThicknesses&&
            (triangles==0||toon->triangles),"streamed Toon counts/parents/arrays");
        for(int i=0;i<verts;++i)ToonRequire(std::isfinite(toon->vertexThicknesses[i]),"finite Toon vertex thickness");
        for(int i=0;i<triangles;++i)for(int v=0;v<3;++v)
            ToonRequire(toon->triangles[i].vertIndex[v]<verts,"Toon triangle vertex index");
        for(int i=0;i<toon->numEdges;++i){
            for(int v=0;v<2;++v)ToonRequire(toon->edges[i].v[v]<verts&&
                (toon->edges[i].face[v]==RPTOONEDGE_NONEIGHBOURFACE||toon->edges[i].face[v]<triangles),"Toon edge vertex/face index");
            for(int ink=0;ink<2;++ink)ToonRequire(toon->edgeInkIDs[i].inkId[ink]<toon->inkIDCount,"Toon edge ink ID");
        }
        bool paint=out.after&&RpToonGeoGetPaint(toon)==out.data->pToonPaint;
        bool ink=out.after&&RpToonGeoGetSilhouetteInk(toon)==out.data->pToonInk;
        bool gradient=paint&&RpToonPaintGetGradientTexture(RpToonGeoGetPaint(toon))==out.data->pTexture;
        int flags=RpNtlAtomicGetFlag(atomic);RpAtomicCallBackRender expected;
        const char* callback;
        if(flags&NTL_TOON_NOT_EDGE){expected=RpNtlToonNotEdgeTwoSideCallBack;callback="ntl-no-edge-two-side";}
        else if(flags&NTL_TOON_DETAIL_EDGE){expected=RpNtlToonEdgeTwoSideCallBack;callback="ntl-detail-edge-two-side";}
        else {expected=RpD3D9ToonFastSilhouetteAtomicRenderCallback;callback="rw-fast-silhouette";}
        bool assigned=out.after&&RpAtomicGetRenderCallBack(atomic)==expected;
        if(out.after)ToonRequire(paint&&ink&&gradient&&assigned&&(!skin||RpSkinAtomicGetType(atomic)==rpSKINTYPETOON),"production Toon ink/paint/gradient/callback/skin binding");
        out.json<<",\"edges\":"<<toon->numEdges<<",\"creaseEdges\":"<<toon->numCreaseEdges<<",\"inkIDs\":"<<toon->inkIDCount
            <<",\"topologyAndThicknessValidated\":true,\"paintBound\":"<<(paint?"true":"false")
            <<",\"silhouetteInkBound\":"<<(ink?"true":"false")<<",\"gradientBound\":"<<(gradient?"true":"false")
            <<",\"expectedCallback\":\""<<callback<<"\",\"callbackAssigned\":"<<(assigned?"true":"false");
    }
    out.json<<"}";return atomic;
}
int main(int argc,char** argv){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    ToonRequire(argc==3,"model DFF and exact native smooth PNG");
    std::vector<unsigned char> bytes;ToonRequire(readFile(argv[1],bytes),"bounded DFF");
    unsigned gradientBytes;ToonRequire(fileSize(argv[2],gradientBytes)&&gradientBytes<=1048576,"bounded gradient PNG");
    HWND window=0;ToonRequire(openEngine(argv[1],window)==0,"native RenderWare initialization");
    SNtlPackImage image;image.strPackFileName=argv[2];image.uiPackOffset=0;image.uiPackSize=gradientBytes;image.pName="smooth.png";
    SToonData data;data.pTexture=Ntl_PNGTextureRead(&image);ToonRequire(data.pTexture!=0,"native packed PNG reader");
    RwRaster* raster=RwTextureGetRaster(data.pTexture);ToonRequire(raster&&RwRasterGetWidth(raster)>0&&RwRasterGetHeight(raster)>0,"native gradient raster");
    // Model surfaces are placeholders: this observer tests native Toon metadata
    // and production setup with the real baseline gradient, never rendering.
    RwTextureSetReadCallBack([](const RwChar* name,const RwChar* mask)->RwTexture*{
        if(!name||strlen(name)>127||(mask&&strlen(mask)>127))return 0;
        RwRaster* raster=RwRasterCreate(4,4,32,rwRASTERTYPETEXTURE|rwRASTERFORMAT8888);
        RwTexture* texture=raster?RwTextureCreate(raster):0;
        if(texture){RwTextureSetName(texture,name);if(mask)RwTextureSetMaskName(texture,mask);}return texture;
    });
    RwMemory memory={bytes.data(),(RwUInt32)bytes.size()};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);RwChunkHeaderInfo header={};
    RpClump* clump=stream&&RwStreamReadChunkHeaderInfo(stream,&header)&&header.type==rwID_CLUMP?RpClumpStreamRead(stream):0;
    ToonRequire(clump&&stream->Type.memory.position==bytes.size(),"native DFF EOF");RwStreamClose(stream,0);
    ToonObservation before;before.data=&data;RpClumpForAllAtomics(clump,ToonAtomic,&before);
    data.pToonInk=RpToonInkCreate();data.pToonPaint=RpToonPaintCreate();ToonRequire(data.pToonInk&&data.pToonPaint,"native ink/paint");
    RwRGBA color={0,0,0,255};RpToonInkSetColor(data.pToonInk,color);RpToonInkSetName(data.pToonInk,"silhouette");
    RpToonInkSetOverallThickness(data.pToonInk,DEFAULT_INK_THICKNESS);RpToonPaintSetGradientTexture(data.pToonPaint,data.pTexture);
    // Actual matching client's function; no rewritten copy or synthetic shader.
    Helper_SetToonClump(clump,&data);
    ToonObservation after;after.data=&data;after.after=true;RpClumpForAllAtomics(clump,ToonAtomic,&after);
    ToonRequire(before.atomics>0&&before.atomics==after.atomics&&before.present==after.present,"stable native atomic/Toon counts");
    std::ostringstream result;result<<"{\"profile\":\"taiwan-native-character-toon-v1\",\"nativeDffEOF\":true,\"atomics\":"<<after.atomics
        <<",\"toonAtomics\":"<<after.present<<",\"allAtomicsHaveToon\":"<<(after.present==after.atomics?"true":"false")
        <<",\"gradientWidth\":"<<RwRasterGetWidth(raster)<<",\"gradientHeight\":"<<RwRasterGetHeight(raster)
        <<",\"gradientDepth\":"<<RwRasterGetDepth(raster)<<",\"before\":["<<before.json.str()<<"],\"after\":["<<after.json.str()
        <<"],\"productionToonSetupCalled\":true,\"texturesSubstituted\":true,\"geometryGenerated\":false,\"renderingTested\":false,\"worldTested\":false}";
    RpClumpDestroy(clump);RpToonPaintDestroy(data.pToonPaint);RpToonInkDestroy(data.pToonInk);RwTextureDestroy(data.pTexture);closeEngine(window);
    printf("%s\n",result.str().c_str());return 0;
}
