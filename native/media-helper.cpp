#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>
#include <algorithm>
#include <cmath>
#include <d3dx9.h>

#include <rwcore.h>
#include <rpworld.h>
#include <rtanim.h>
#include <rphanim.h>
#include <rpskin.h>
#include <rptoon.h>
#include <rpusrdat.h>
#include <rpid.h>
#include <rpmatfx.h>
#include <rpuvanim.h>
#include <rtfsyst.h>
#include <rpcollis.h>
#include <rppvs.h>
#include <rpltmap.h>
#include "NtlAtomic.h"
#include "NtlMaterialExt.h"
#include "NtlPackImage.h"
#include "fmod.h"

static const unsigned MAX_FILE = 128u * 1024u * 1024u;
static const unsigned MAX_VERTICES = 1000000u;
static const unsigned MAX_TRIANGLES = 1000000u;
static const unsigned MAX_MATERIALS = 4096u;
static const unsigned MAX_MESHES = 512u;
static std::vector<std::pair<std::string,std::string>> gTextureRequests;
static bool gTextureRequestsFailed=false;
struct TextureBinding { std::string name,mask,path; unsigned bytes; bool used; };
static std::vector<TextureBinding> gTextureBindings;
static bool gRealTextureBindings=false;
static std::string gBindingAnimation;
static std::string gBindingUv;
static bool gAllowInactiveUvMaps=false;
static bool gAllowTwoEntryInactiveUvMaps=false;
static bool gAllowSourceMultiFrameUv=false;
static bool gAllowIdenticalNamedUv=false;
static bool gWindInactiveAuxiliary=false;
static unsigned gWindMissingRequests=0;
static bool gPopoSourceAbsentPrimary=false;
static unsigned gPopoMissingRequests=0;
static bool bundleU32(const std::vector<unsigned char>& b,size_t& p,unsigned& n){if(p>b.size()||b.size()-p<4)return false;std::memcpy(&n,b.data()+p,4);p+=4;return true;}
static bool bundleString(const std::vector<unsigned char>& b,size_t& p,std::string& s){unsigned n=0;if(!bundleU32(b,p,n)||n>31||p>b.size()||n>b.size()-p)return false;s.assign((const char*)b.data()+p,n);p+=n;return s.find('\0')==std::string::npos;}
static bool bundleFile(const std::vector<unsigned char>& b,size_t& p,const std::string& path,unsigned& n){
    if(!bundleU32(b,p,n)||!n||n>MAX_FILE||p>b.size()||n>b.size()-p)return false;
    HANDLE f=CreateFileA(path.c_str(),GENERIC_WRITE,0,0,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY,0);if(f==INVALID_HANDLE_VALUE)return false;
    DWORD written=0;bool ok=WriteFile(f,b.data()+p,n,&written,0)!=0&&written==n;CloseHandle(f);if(!ok){DeleteFileA(path.c_str());return false;}p+=n;return true;
}
static int modelBindings(const char*,const char*,bool bsp=false);
static void hex(std::ofstream& out,const std::string& value){static const char* digits="0123456789abcdef";for(unsigned char c:value){out.put(digits[c>>4]);out.put(digits[c&15]);}}
static RwStream* WorldMaterialReader(RwStream* stream,RwInt32 length,void*,RwInt32,RwInt32) {
    return RwStreamSkip(stream,(RwUInt32)length);
}
static void message(const char* s) { std::fprintf(stderr,"media-helper: %s\n",s); }
static bool fileSize(const char* p, unsigned& n) {
    std::ifstream f(p,std::ios::binary|std::ios::ate); if(!f)return false;
    std::streamoff z=f.tellg(); if(z<=0 || z>(std::streamoff)MAX_FILE)return false; n=(unsigned)z; return true;
}
static bool readFile(const char* p,std::vector<unsigned char>& d) {
    unsigned n=0; if(!fileSize(p,n))return false; d.resize(n);
    std::ifstream f(p,std::ios::binary); return f && !!f.read((char*)d.data(),n);
}
static bool writeU32(std::ofstream& f,uint32_t v){f.write((const char*)&v,4);return !!f;}
static bool writeF32(std::ofstream& f,float v){f.write((const char*)&v,4);return !!f;}

struct MeshOut { std::vector<float> v; std::vector<uint32_t> tri; RwRGBA color; char tex[128]; };
static std::vector<MeshOut>* gMeshes=0;
static std::vector<RpMaterial*> gSeenMaterials;
static RwMatrix* gLtm=0;
static RpGeometry* gGeometry=0;
static bool gCollectionFailed=false;
static uint64_t gCollectedVertices=0,gCollectedTriangles=0;
static RpMesh* CollectMesh(RpMesh* mesh,RpMeshHeader*,void*) {
    if(!mesh || !gGeometry || !gMeshes)return 0;
    if(gMeshes->size()>=MAX_MESHES){gCollectionFailed=true;return 0;}
    RpMaterial* material=mesh->material;
    if(std::find(gSeenMaterials.begin(),gSeenMaterials.end(),material)!=gSeenMaterials.end())return mesh;
    const unsigned nv=(unsigned)RpGeometryGetNumVertices(gGeometry);
    const unsigned ntri=(unsigned)RpGeometryGetNumTriangles(gGeometry);
    if(nv==0 || nv>MAX_VERTICES || ntri==0 || ntri>MAX_TRIANGLES){gCollectionFailed=true;return 0;}
    int materialIndex=-1;const int nmat=RpGeometryGetNumMaterials(gGeometry);
    for(int i=0;i<nmat;++i)if(RpGeometryGetMaterial(gGeometry,i)==material){materialIndex=i;break;}
    if(materialIndex<0){gCollectionFailed=true;return 0;}
    const RpTriangle* tris=RpGeometryGetTriangles(gGeometry);unsigned materialTriangles=0;
    for(unsigned i=0;i<ntri;++i)if(tris[i].matIndex==materialIndex)++materialTriangles;
    if(!materialTriangles)return mesh;
    if(gCollectedVertices+nv>MAX_VERTICES || gCollectedTriangles+materialTriangles>MAX_TRIANGLES){gCollectionFailed=true;return 0;}
    const RpMorphTarget* mt=RpGeometryGetMorphTarget(gGeometry,0);
    const RwV3d* src=mt?RpMorphTargetGetVertices(mt):0;
    if(!src){gCollectionFailed=true;return 0;}
    std::vector<RwV3d> positions(src,src+nv);
    if(gLtm) RwV3dTransformPoints(positions.data(),positions.data(),(RwInt32)nv,gLtm);
    const RwTexCoords* uv=RpGeometryGetVertexTexCoords(gGeometry,rwTEXTURECOORDINATEINDEX0);
    MeshOut out; out.v.reserve((size_t)nv*5);
    for(unsigned i=0;i<nv;++i){
        float u=uv?uv[i].u:0.f,v=uv?uv[i].v:0.f;
        if(!std::isfinite(positions[i].x)||!std::isfinite(positions[i].y)||!std::isfinite(positions[i].z)||!std::isfinite(u)||!std::isfinite(v)){gCollectionFailed=true;return 0;}
        out.v.push_back(positions[i].x);out.v.push_back(positions[i].y);out.v.push_back(positions[i].z);out.v.push_back(u);out.v.push_back(v);
    }
    out.tri.reserve((size_t)materialTriangles*4);
    for(unsigned i=0;i<ntri;++i)if(tris[i].matIndex==materialIndex){
        if(tris[i].vertIndex[0]>=nv||tris[i].vertIndex[1]>=nv||tris[i].vertIndex[2]>=nv){gCollectionFailed=true;return 0;}
        out.tri.push_back(tris[i].vertIndex[0]);out.tri.push_back(tris[i].vertIndex[1]);out.tri.push_back(tris[i].vertIndex[2]);out.tri.push_back(0);
    }
    out.color=material?*RpMaterialGetColor(material):RwRGBA{255,255,255,255};
    std::memset(out.tex,0,sizeof(out.tex));
    RwTexture* tx=material?RpMaterialGetTexture(material):0;
    if(tx){const char* name=RwTextureGetName(tx);if(name){std::strncpy(out.tex,name,sizeof(out.tex)-1);}}
    gMeshes->push_back(out);gSeenMaterials.push_back(material);gCollectedVertices+=nv;gCollectedTriangles+=materialTriangles;return mesh;
}
static RpAtomic* CollectAtomic(RpAtomic* atomic,void*) {
    RpGeometry* geom=atomic?RpAtomicGetGeometry(atomic):0; if(!geom)return atomic;
    RwFrame* frame=RpAtomicGetFrame(atomic);gLtm=frame?RwFrameGetLTM(frame):0;gGeometry=geom;
    gSeenMaterials.clear();RpGeometryForAllMeshes(geom,CollectMesh,0);gGeometry=0;gLtm=0;return atomic;
}
static int openEngine(const char* input,HWND& wnd){
    wnd=CreateWindowExA(0,"STATIC","Taiwan table studio native media helper",WS_OVERLAPPEDWINDOW,0,0,640,480,0,0,GetModuleHandleA(0),0);
    if(!wnd || !RwEngineInit(0,0,1024*1024)){message("RenderWare engine initialization failed");return 11;}
    bool plugins=RpWorldPluginAttach()&&RpPVSPluginAttach()&&RpCollisionPluginAttach()&&RtAnimInitialize()&&RpLtMapPluginAttach()&&RpHAnimPluginAttach()&&RpSkinPluginAttach()&&RpToonPluginAttach()&&RpUserDataPluginAttach()&&RpIdPluginAttach()&&RpMatFXPluginAttach()&&RpUVAnimPluginAttach()&&RpNtlAtomicPluginAttach()&&RpNtlMatFxPluginAttach();
    if(!plugins){message("Taiwan RenderWare plugin attach failed");return 12;}
    const RwUInt32 worldId=MAKECHUNKID(rwVENDORID_CRITERIONTK,0x77);
    if(RpMaterialRegisterPlugin(sizeof(void*),worldId,0,0,0)<0 || RpMaterialRegisterPluginStream(worldId,WorldMaterialReader,0,0)<0){message("world-material plugin registration failed");return 13;}
    RwEngineOpenParams op={};op.displayID=wnd;
    if(!RwEngineOpen(&op)){message("RenderWare engine open failed");return 14;}
    int mode=-1;for(int i=0;i<RwEngineGetNumVideoModes();++i){RwVideoMode info;if(RwEngineGetVideoModeInfo(&info,i)&&!(info.flags&rwVIDEOMODEEXCLUSIVE)){mode=i;break;}}
    if(mode<0 || !RwEngineSetVideoMode(mode) || !RwEngineStart()){message("RenderWare D3D9 engine start failed (graphics device unavailable)");return 15;}
    if(!RtFSManagerOpen(RTFSMAN_UNLIMITED_NUM_FS)){message("RenderWare filesystem open failed");return 15;}
    char drive[]="D:",name[]="D";
    if(input[0] && input[1]==':'){drive[0]=input[0];name[0]=input[0];}
    else {char cwd[MAX_PATH]={};if(GetCurrentDirectoryA(MAX_PATH,cwd)>1&&cwd[1]==':'){drive[0]=cwd[0];name[0]=cwd[0];}}
    RtFileSystem* fs=RtWinFSystemInit(100,drive,name);
    if(!fs || !RtFSManagerRegister(fs)){message("RenderWare filesystem registration failed");return 15;}
    RtFSManagerSetDefaultFileSystem(fs);
    return 0;
}
static void closeEngine(HWND wnd){RtFSManagerClose();RwEngineStop();RwEngineClose();RwEngineTerm();DestroyWindow(wnd);}
struct WindAuxiliaryAudit { unsigned atomics=0,materials=0,skin=0,toon=0,customPipelines=0; bool valid=true; };
static RpAtomic* AuditWindAuxiliary(RpAtomic* atomic,void* data){
    auto& audit=*(WindAuxiliaryAudit*)data;++audit.atomics;
    RpGeometry* geometry=RpAtomicGetGeometry(atomic);RxPipeline* pipeline=0;RpAtomicGetPipeline(atomic,&pipeline);
    std::fprintf(stderr,"wind-audit atomic geometry=%d vertices=%d triangles=%d materials=%d defaultCallback=%d pipeline=%d skin=%d toon=%d\n",
        geometry!=0,geometry?RpGeometryGetNumVertices(geometry):0,geometry?RpGeometryGetNumTriangles(geometry):0,geometry?RpGeometryGetNumMaterials(geometry):0,
        RpAtomicGetRenderCallBack(atomic)==AtomicDefaultRenderCallBack,pipeline!=0,geometry&&RpSkinGeometryGetSkin(geometry)!=0,geometry&&RpToonGeometryGetToonGeo(geometry)!=0);
    if(pipeline)++audit.customPipelines;
    if(!geometry||RpAtomicGetRenderCallBack(atomic)!=AtomicDefaultRenderCallBack){audit.valid=false;return atomic;}
    if(RpSkinGeometryGetSkin(geometry))++audit.skin;
    if(RpToonGeometryGetToonGeo(geometry))++audit.toon;
    if(RpGeometryGetNumVertices(geometry)!=45||RpGeometryGetNumTriangles(geometry)!=56||RpGeometryGetNumMaterials(geometry)!=1)audit.valid=false;
    for(int i=0;i<RpGeometryGetNumMaterials(geometry);++i){
        ++audit.materials;RpMaterial* material=RpGeometryGetMaterial(geometry,i);RxPipeline* materialPipeline=0;
        RpMaterialGetPipeline(material,&materialPipeline);if(materialPipeline)++audit.customPipelines;
        RwTexture* primary=RpMaterialGetTexture(material);
        RwMatrix* baseTransform=0;RwMatrix* dualTransform=0;
        if(RpMatFXMaterialGetEffects(material)==rpMATFXEFFECTUVTRANSFORM)RpMatFXMaterialGetUVTransformMatrices(material,&baseTransform,&dualTransform);
        std::fprintf(stderr,"wind-audit material primary=%s raster=%d auxiliary=%d uvTexture=%d envTexture=%d flags=0x%x matfx=%d pipeline=%d\n",
            primary?RwTextureGetName(primary):"<null>",primary&&RwTextureGetRaster(primary)!=0,RpNtlMaterialExtGetTexture(material)!=0,
            RpNtlMaterialExtGetUVTexture(material)!=0,RpNtlMaterialExtGetEnvMapTexture(material)!=0,(unsigned)RpNtlMaterialExtGetFlag(material),
            (int)RpMatFXMaterialGetEffects(material),materialPipeline!=0);
        if(!primary||!RwTextureGetRaster(primary)||std::strcmp(RwTextureGetName(primary),"u17m_ccbf319ed58cc2e6")||
            RpNtlMaterialExtGetTexture(material)!=0||RpNtlMaterialExtGetUVTexture(material)!=0||RpNtlMaterialExtGetEnvMapTexture(material)!=0||
            RpNtlMaterialExtGetFlag(material)!=0||RpMatFXMaterialGetEffects(material)!=rpMATFXEFFECTUVTRANSFORM||baseTransform||dualTransform)audit.valid=false;
    }
    return atomic;
}
struct PopoPrimaryAudit { unsigned atomics=0,triangles=0;bool valid=true; };
static RpAtomic* AuditPopoPrimary(RpAtomic* atomic,void* data){
    auto& a=*(PopoPrimaryAudit*)data;++a.atomics;
    RpGeometry* g=atomic?RpAtomicGetGeometry(atomic):0;
    if(!g||RpGeometryGetNumVertices(g)!=7323||RpGeometryGetNumTriangles(g)!=10725||RpGeometryGetNumMaterials(g)!=5){a.valid=false;return atomic;}
    for(int i=0;i<5;++i){
        RpMaterial* m=RpGeometryGetMaterial(g,i);RwTexture* t=m?RpMaterialGetTexture(m):0;
        if(!m){a.valid=false;continue;}
        if(i==0){
            const RwRGBA* c=RpMaterialGetColor(m);const RwSurfaceProperties* s=RpMaterialGetSurfaceProperties(m);
            if(t||!c||c->red!=150||c->green!=150||c->blue!=150||c->alpha!=255||!s||
                s->ambient!=1.0f||s->diffuse!=1.0f||s->specular!=0.0f||RpNtlMaterialExtGetFlag(m)!=0||
                RpNtlMaterialExtGetTexture(m)||RpNtlMaterialExtGetUVTexture(m)||RpNtlMaterialExtGetEnvMapTexture(m)||
                RpMatFXMaterialGetEffects(m)!=rpMATFXEFFECTNULL)a.valid=false;
        }else{
            char expected[32];std::sprintf(expected,"NPC_Popo_0%d",i);
            if(!t||!RwTextureGetRaster(t)||std::strcmp(RwTextureGetName(t),expected))a.valid=false;
        }
    }
    const RpTriangle* triangles=RpGeometryGetTriangles(g);
    for(int i=0;i<RpGeometryGetNumTriangles(g);++i)if(triangles[i].matIndex==0)++a.triangles;
    if(a.triangles!=2733)a.valid=false;
    return atomic;
}
#include "animation-binding.h"
#include "uv-binding.h"
#include "static-bsp-scene.h"
#include "map-workshop.h"
static int model(const char* input,const char* output,bool metadata=false,bool bsp=false) {
    std::vector<unsigned char> bytes;if(!readFile(input,bytes)){message("model input missing, empty, or larger than 128 MiB");return 10;}
    HWND wnd=0;int initialized=openEngine(input,wnd);if(initialized)return initialized;
    RpUVAnim* uvAnimation=0;RtDict* uvDictionary=0;
    if(!gBindingUv.empty()){
        uvDictionary=loadBindingUv(gBindingUv.c_str(),uvAnimation);
        if(!uvDictionary){message("bounded native UV dictionary rejected");closeEngine(wnd);return 42;}
        RtDictSchemaSetCurrentDict(RpUVAnimGetDictSchema(),uvDictionary);
    }
    gTextureRequests.clear();gTextureRequestsFailed=false;gWindMissingRequests=0;gPopoMissingRequests=0;
    RwTextureSetReadCallBack([](const RwChar* n,const RwChar* mask)->RwTexture* {
        if(!n||std::strlen(n)>127||(mask&&std::strlen(mask)>127)||gTextureRequests.size()>=16384){gTextureRequestsFailed=true;return 0;}
        gTextureRequests.push_back({n,mask?mask:""});
        if(gRealTextureBindings){
            for(auto& binding:gTextureBindings)if(binding.name==n&&binding.mask==(mask?mask:"")){
                SNtlPackImage image;image.strPackFileName=binding.path;image.uiPackOffset=0;image.uiPackSize=binding.bytes;image.pName=n;
                const char* ext=std::strrchr(binding.path.c_str(),'.');
                RwTexture* texture=std::strcmp(ext,".dds")==0?Ntl_D3D9DDSTextureRead(&image):std::strcmp(ext,".png")==0?Ntl_PNGTextureRead(&image):Ntl_BMPTextureRead(&image);
                RwRaster* raster=texture?RwTextureGetRaster(texture):0;
                if(!raster||RwRasterGetWidth(raster)<1||RwRasterGetHeight(raster)<1||RwRasterGetWidth(raster)>16384||RwRasterGetHeight(raster)>16384){if(texture)RwTextureDestroy(texture);gTextureRequestsFailed=true;return 0;}
                RwTextureSetName(texture,n);if(mask)RwTextureSetMaskName(texture,mask);binding.used=true;return texture;
            }
            if(gWindInactiveAuxiliary&&std::strcmp(n,"apo")==0&&(!mask||!*mask)&&gWindMissingRequests==0){++gWindMissingRequests;return 0;}
            if(gPopoSourceAbsentPrimary&&std::strcmp(n,"popo")==0&&(!mask||!*mask)&&gPopoMissingRequests==0){++gPopoMissingRequests;return 0;}
            gTextureRequestsFailed=true;return 0;
        }
        RwRaster* r=RwRasterCreate(4,4,32,rwRASTERTYPETEXTURE|rwRASTERFORMAT8888);if(!r)return 0;
        RwTexture* t=RwTextureCreate(r);if(t){RwTextureSetName(t,n);if(mask)RwTextureSetMaskName(t,mask);}return t;
    });
    void* fp=RwFopen(input,"rb");RwStream* stream=fp?RwStreamOpen(rwSTREAMFILE,rwSTREAMREAD,fp):0;
    RpClump* clump=0;RwChunkHeaderInfo h={};bool gotHeader=stream&&RwStreamReadChunkHeaderInfo(stream,&h)!=0;if(gotHeader&&h.type==rwID_CLUMP)clump=RpClumpStreamRead(stream);
    if(stream)RwStreamClose(stream,0);if(fp)RwFclose(fp);
    if(!clump){RwError e={};RwErrorGet(&e);char detail[192];std::sprintf(detail,"clump stream read failed (header=%d type=0x%x version=0x%x error=0x%x plugin=0x%x)",(int)gotHeader,(unsigned)h.type,(unsigned)h.version,(unsigned)e.errorCode,(unsigned)e.pluginID);message(detail);if(uvDictionary){RtDictSchemaSetCurrentDict(RpUVAnimGetDictSchema(),0);RtDictDestroy(uvDictionary);}closeEngine(wnd);return 16;}
    std::vector<MeshOut> meshes;gCollectionFailed=false;gCollectedVertices=0;gCollectedTriangles=0;gMeshes=&meshes;RpClumpForAllAtomics(clump,CollectAtomic,0);gMeshes=0;
    bool ok=!gTextureRequestsFailed&&!gCollectionFailed&&!meshes.empty()&&meshes.size()<=MAX_MESHES;
    WindAuxiliaryAudit wind;
    if(gWindInactiveAuxiliary){
        RpClumpForAllAtomics(clump,AuditWindAuxiliary,&wind);
        ok=ok&&bytes.size()==3632&&gRealTextureBindings&&gBindingAnimation.empty()&&gBindingUv.empty()&&gWindMissingRequests==1&&
            gTextureRequests.size()==2&&wind.valid&&wind.atomics==1&&wind.materials==1&&!wind.skin&&!wind.toon&&!wind.customPipelines;
        if(ok){
            RpClump* clone=RpClumpClone(clump);WindAuxiliaryAudit copied;
            if(clone){RpClumpForAllAtomics(clone,AuditWindAuxiliary,&copied);RpClumpDestroy(clone);}
            ok=clone&&copied.valid&&copied.atomics==1&&copied.materials==1&&!copied.skin&&!copied.toon&&!copied.customPipelines;
        }
        if(!ok)message("exact wind inactive auxiliary consumer state rejected");
    }
    if(gRealTextureBindings)for(const auto& binding:gTextureBindings)if(!binding.used)ok=false;
    PopoPrimaryAudit popo;
    if(gPopoSourceAbsentPrimary){
        RpClumpForAllAtomics(clump,AuditPopoPrimary,&popo);
        ok=ok&&gRealTextureBindings&&gTextureBindings.size()==4&&gTextureRequests.size()==5&&gPopoMissingRequests==1&&
            gBindingUv.empty()&&popo.valid&&popo.atomics==1;
        if(ok){RpClump* copy=RpClumpClone(clump);PopoPrimaryAudit cloned;
            if(copy){RpClumpForAllAtomics(copy,AuditPopoPrimary,&cloned);RpClumpDestroy(copy);}
            ok=copy&&cloned.valid&&cloned.atomics==1&&cloned.triangles==2733;}
        if(!ok)message("exact source-absent Popo primary material/clone rejected");
    }
    AnimationBindingResult animation;
    if(ok&&!gBindingAnimation.empty()){ok=playbackBinding(clump,gBindingAnimation.c_str(),animation);if(!ok)message("model/animation hierarchy binding rejected");}
    UvBindingResult uv;
    if(ok&&uvDictionary){ok=playbackBindingUv(clump,uvAnimation,uv);if(!ok)message("model/UV material binding rejected");}
    if(bsp){
        ok=ok&&gRealTextureBindings&&gBindingAnimation.empty()&&gBindingUv.empty()&&BakeStaticBsp(clump,output);
        RpClumpDestroy(clump);closeEngine(wnd);
        if(!ok){DeleteFileA(output);message("static BSP conversion or complete attribute preservation rejected");return 44;}return 0;
    }
    std::ofstream out(output,std::ios::binary|std::ios::trunc);
    if(!out)ok=false;
    if(ok&&!metadata){out.write("OSM1",4);ok=writeU32(out,(uint32_t)meshes.size());}
    unsigned long totalV=0,totalT=0;
    for(size_t mi=0;ok&&mi<meshes.size();++mi){MeshOut& m=meshes[mi];unsigned nv=(unsigned)(m.v.size()/5),nt=(unsigned)(m.tri.size()/4);totalV+=nv;totalT+=nt;if(totalV>MAX_VERTICES||totalT>MAX_TRIANGLES){ok=false;break;}
        if(metadata)continue;
        ok=writeU32(out,nv)&&writeU32(out,nt)&&writeU32(out,1);
        for(size_t i=0;ok&&i<m.v.size();++i)ok=writeF32(out,m.v[i]);
        for(size_t i=0;ok&&i<m.tri.size();++i)ok=writeU32(out,m.tri[i]);
        uint32_t rgba=(uint32_t)m.color.red|((uint32_t)m.color.green<<8)|((uint32_t)m.color.blue<<16)|((uint32_t)m.color.alpha<<24);
        ok=ok&&writeU32(out,rgba);out.write(m.tex,128);ok=ok&&!!out;
    }
    if(ok&&metadata){
        out<<"{\"meshes\":"<<meshes.size()<<",\"vertices\":"<<totalV<<",\"triangles\":"<<totalT<<",\"textureRequests\":[";
        for(size_t i=0;i<gTextureRequests.size();++i){if(i)out<<",";out<<"{\"nameHex\":\"";hex(out,gTextureRequests[i].first);out<<"\",\"maskHex\":\"";hex(out,gTextureRequests[i].second);out<<"\"}";}
        out<<"],\"texturesSubstituted\":"<<(gRealTextureBindings?"false":"true")<<",\"renderingTested\":false";
        if(gWindInactiveAuxiliary)out<<",\"inactiveAuxiliary\":{\"profile\":\"ext-wind1-inactive-auxiliary-v1\",\"name\":\"apo\",\"missingRequests\":"<<gWindMissingRequests
            <<",\"primaryRasterBound\":true,\"auxiliaryTextureNull\":true,\"defaultRenderCallback\":true,\"materialFlags\":0,\"skinAtomics\":"<<wind.skin
            <<",\"toonAtomics\":"<<wind.toon<<",\"customPipelines\":"<<wind.customPipelines<<",\"matfxMode\":5,\"uvMatricesNull\":true,\"nativeCloneValidated\":true,\"modelBytesPreserved\":true}";
        if(gPopoSourceAbsentPrimary)out<<",\"sourceAbsentPrimary\":{\"profile\":\"ext-popo-source-absent-primary-v1\",\"name\":\"popo\",\"missingRequests\":"<<gPopoMissingRequests
            <<",\"primaryTextureNull\":true,\"otherFourRastersBound\":true,\"materialTriangles\":"<<popo.triangles
            <<",\"materialRgba\":[150,150,150,255],\"nativeCloneValidated\":true,\"modelBytesPreserved\":true,\"visualSourceEquivalenceProven\":false}";
        if(!gBindingAnimation.empty())out<<",\"animationBinding\":{\"nativeHierarchyPlayback\":true,\"nodes\":"<<animation.nodes<<",\"frames\":"<<animation.frames<<",\"duration\":"<<animation.duration<<",\"steps\":"<<animation.steps<<",\"skinAtomics\":"<<animation.skinAtomics<<",\"matrixChanged\":"<<(animation.changed?"true":"false")<<",\"eventsTested\":false,\"renderingTested\":false}";
        if(uvDictionary){out<<",\"uvBinding\":{\"nativeMaterialPlayback\":true,\"profile\":\""<<(gAllowIdenticalNamedUv?"ext-identical-named-param-pair-v1":gAllowSourceMultiFrameUv?(uv.frames==3?"ext-single-param-three-frame-v1":"ext-single-param-seven-frame-v1"):gAllowTwoEntryInactiveUvMaps?"two-entry-two-frame-linear-unused-map-v1":gAllowInactiveUvMaps?"single-two-frame-unused-map-v1":uv.entries==2?"two-entry-two-frame-v1":"single-two-frame-v1")<<"\",\"entries\":"<<uv.entries<<",\"frames\":"<<uv.frames<<",\"materials\":"<<uv.materials<<",\"slots\":"<<uv.slots<<",\"duration\":"<<uv.duration<<",\"steps\":"<<uv.steps<<",\"matrixChanged\":"<<(uv.changed?"true":"false")<<",\"nameHex\":\"";hex(out,uv.name);out<<"\",\"renderingTested\":false}";}
        if(!gPoseSamples.empty()){
            out<<",\"boneFrameCatalog\":{\"profile\":\"taiwan-client-frame-map-observation-v1\",\"rootExcludedFromMap\":true,\"duplicateSelection\":\"last-mapped-frame\",\"frames\":[";
            for(size_t i=0;i<animation.boneFrames.size();++i){auto& frame=animation.boneFrames[i];if(i)out<<",";
                out<<"{\"index\":"<<i<<",\"parent\":"<<frame.parent<<",\"nameHex\":\"";hex(out,frame.name);
                out<<"\",\"userDataPresent\":"<<(frame.userDataPresent?"true":"false")<<",\"clientMapped\":"<<(frame.clientMapped?"true":"false")<<"}";
            }out<<"]}";
            out<<",\"poseSamples\":[";
            for(size_t i=0;i<gPoseSamples.size();++i){auto& sample=gPoseSamples[i];if(i)out<<",";
                out<<"{\"time\":"<<sample.time<<",\"boneHex\":\"";hex(out,sample.bone);
                out<<"\",\"position\":["<<sample.position.x<<","<<sample.position.y<<","<<sample.position.z<<"]}";
            }out<<"]";
        }
        out<<"}";ok=!!out;
    }
    RpClumpDestroy(clump);
    if(uvDictionary){RtDictSchemaSetCurrentDict(RpUVAnimGetDictSchema(),0);RtDictDestroy(uvDictionary);}
    closeEngine(wnd);
    if(!ok){out.close();DeleteFileA(output);message("clump had no static geometry, exceeded limits, or output write failed");return 17;}
    return 0;
}

static int modelBindings(const char* input,const char* output,bool bsp){
    gAllowInactiveUvMaps=false; gAllowTwoEntryInactiveUvMaps=false;gAllowSourceMultiFrameUv=false;gAllowIdenticalNamedUv=false;
    std::vector<unsigned char> bytes;if(!readFile(input,bytes)||bytes.size()<12||(std::memcmp(bytes.data(),"OMB1",4)&&std::memcmp(bytes.data(),"OMB2",4)&&std::memcmp(bytes.data(),"OMB3",4)&&std::memcmp(bytes.data(),"OMB4",4)&&std::memcmp(bytes.data(),"OMB5",4)&&std::memcmp(bytes.data(),"OMB6",4)&&std::memcmp(bytes.data(),"OMB7",4)&&std::memcmp(bytes.data(),"OMB8",4)&&std::memcmp(bytes.data(),"OMB9",4))){message("invalid binding bundle");return 40;}
    gPopoSourceAbsentPrimary=std::memcmp(bytes.data(),"OMB9",4)==0;
    if(gPopoSourceAbsentPrimary&&bsp){message("Popo source-absence profile cannot convert BSP");return 44;}
    gWindInactiveAuxiliary=std::memcmp(bytes.data(),"OMB6",4)==0;
    if(gWindInactiveAuxiliary&&bsp){message("wind auxiliary profile cannot convert BSP");return 44;}
    const bool hasAddition=bsp&&std::memcmp(bytes.data(),"OMB4",4)==0;
    if(bsp&&std::memcmp(bytes.data(),"OMB1",4)!=0&&!hasAddition){message("static BSP requires OMB1/OMB4 without animation/UV playback");return 44;}
    bool hasAnimation=std::memcmp(bytes.data(),"OMB2",4)==0;
    gAllowInactiveUvMaps=!bsp&&std::memcmp(bytes.data(),"OMB4",4)==0;
    gAllowTwoEntryInactiveUvMaps=!bsp&&std::memcmp(bytes.data(),"OMB5",4)==0;
    gAllowSourceMultiFrameUv=!bsp&&std::memcmp(bytes.data(),"OMB7",4)==0;
    gAllowIdenticalNamedUv=!bsp&&std::memcmp(bytes.data(),"OMB8",4)==0;
    const bool hasUv=std::memcmp(bytes.data(),"OMB3",4)==0||gAllowInactiveUvMaps||gAllowTwoEntryInactiveUvMaps||gAllowSourceMultiFrameUv||gAllowIdenticalNamedUv;
    size_t p=4;unsigned modelSize=0,count=0;std::vector<std::string> files;const std::string modelPath="binding-model.dff";
    if(gPopoSourceAbsentPrimary){unsigned flags=0;if(!bundleU32(bytes,p,flags)||flags>1){message("invalid exact Popo animation flag");return 40;}hasAnimation=flags==1;}
    if(hasUv){unsigned flags=0;if(!bundleU32(bytes,p,flags)||(flags!=2&&flags!=3)){message("invalid OMB3 flags");return 40;}hasAnimation=(flags&1)!=0;}
    if(!bundleFile(bytes,p,modelPath,modelSize)){message("invalid bundled model");return 40;}files.push_back(modelPath);
    bool ok=bundleU32(bytes,p,count)&&count<=4096;gTextureBindings.clear();
    for(unsigned i=0;ok&&i<count;++i){
        TextureBinding t;unsigned kind=0;t.used=false;
        ok=bundleString(bytes,p,t.name)&&!t.name.empty()&&bundleString(bytes,p,t.mask)&&t.mask.empty()&&bundleU32(bytes,p,kind)&&kind>=1&&kind<=3;
        if(!ok)break;
        for(const auto& existing:gTextureBindings)if(existing.name==t.name&&existing.mask==t.mask)ok=false;
        if(!ok)break;
        t.path="binding-texture-"+std::to_string(i)+(kind==1?".dds":kind==2?".png":".bmp");
        ok=bundleFile(bytes,p,t.path,t.bytes);if(ok){files.push_back(t.path);gTextureBindings.push_back(t);}
    }
    gBindingAnimation.clear();gBindingUv.clear();
    if(ok&&hasAnimation){unsigned animationBytes=0;ok=bundleFile(bytes,p,"binding-animation.anm",animationBytes);if(ok){files.push_back("binding-animation.anm");gBindingAnimation="binding-animation.anm";}}
    if(ok&&hasUv){unsigned uvBytes=0;ok=bundleFile(bytes,p,"binding-uv.uva",uvBytes);if(ok){files.push_back("binding-uv.uva");gBindingUv="binding-uv.uva";}}
    gBspAdditionPath.clear();
    if(ok&&hasAddition){unsigned additionBytes=0;ok=bundleFile(bytes,p,"binding-addition.mesh",additionBytes)&&additionBytes<=65536;if(ok){files.push_back("binding-addition.mesh");gBspAdditionPath="binding-addition.mesh";}}
    ok=ok&&p==bytes.size();int result=40;
    if(ok){gRealTextureBindings=true;result=model(modelPath.c_str(),output,!bsp,bsp);gRealTextureBindings=false;}
    else message("incomplete, duplicate or out-of-bounds binding bundle");
    gBspAdditionPath.clear();gTextureBindings.clear();gBindingAnimation.clear();gBindingUv.clear();gAllowInactiveUvMaps=false;gWindInactiveAuxiliary=false;gPopoSourceAbsentPrimary=false;for(const auto& f:files)DeleteFileA(f.c_str());return result;
}

// Read-only DFF/ANM pose probe. Textures are explicitly substituted, never
// certified; only native hierarchy matrices at requested times are inspected.
static int modelPoses(const char* input,const char* output){
    std::vector<unsigned char> bytes;if(!readFile(input,bytes)||bytes.size()<16||std::memcmp(bytes.data(),"OMP1",4)){message("invalid pose bundle");return 41;}
    size_t p=4;unsigned size=0,count=0;std::vector<std::string> files;
    bool ok=bundleFile(bytes,p,"pose-model.dff",size);
    if(ok)files.push_back("pose-model.dff");
    if(ok){ok=bundleFile(bytes,p,"pose-animation.anm",size);if(ok)files.push_back("pose-animation.anm");}
    if(ok)ok=bundleU32(bytes,p,count)&&count>0&&count<=128;
    gPoseSamples.clear();
    for(unsigned i=0;ok&&i<count;++i){unsigned time=0;AnimationPoseSample sample;
        ok=bundleU32(bytes,p,time)&&bundleString(bytes,p,sample.bone);std::memcpy(&sample.time,&time,4);
        ok=ok&&std::isfinite(sample.time)&&sample.time>=0;
        if(ok)gPoseSamples.push_back(sample);
    }
    ok=ok&&p==bytes.size();int result=41;
    if(ok){gBindingAnimation="pose-animation.anm";result=model("pose-model.dff",output,true);}
    else message("incomplete or out-of-bounds pose bundle");
    gPoseSamples.clear();gBindingAnimation.clear();for(const auto& f:files)DeleteFileA(f.c_str());return result;
}

// Inspection only: actual RenderWare-created D3D9 surface after the production
// packed reader (including PNG gamma correction). No mip-chain equivalence.
static int texture(const char* input,const char* output,bool pixels=false){
    unsigned size=0;if(!fileSize(input,size)){message("texture input missing/size limit");return 31;}
    const char* extension=std::strrchr(input,'.');if(!extension||(_stricmp(extension,".dds")&&_stricmp(extension,".png")&&_stricmp(extension,".bmp"))){message("unsupported texture extension");return 32;}
    HWND wnd=0;int initialized=openEngine(input,wnd);if(initialized)return initialized;
    SNtlPackImage image;image.strPackFileName=input;image.uiPackOffset=0;image.uiPackSize=size;image.pName="native-probe";
    RwTexture* tex=_stricmp(extension,".dds")==0?Ntl_D3D9DDSTextureRead(&image):_stricmp(extension,".png")==0?Ntl_PNGTextureRead(&image):Ntl_BMPTextureRead(&image);
    if(!tex){message("Taiwan packed texture reader rejected resource");closeEngine(wnd);return 33;}
    RwRaster* raster=RwTextureGetRaster(tex);
    int width=raster?RwRasterGetWidth(raster):0,height=raster?RwRasterGetHeight(raster):0;
    if(width<=0||height<=0||width>16384||height>16384){RwTextureDestroy(tex);closeEngine(wnd);message("native texture dimensions");return 34;}
    if(pixels){
        const uint64_t count=(uint64_t)width*height*4;
        if(count>MAX_FILE-40){RwTextureDestroy(tex);closeEngine(wnd);message("native pixel snapshot exceeds limit");return 36;}
        // RwImageSetFromRaster explicitly refuses compressed DXT rasters.
        // Obtain the actual bound native surface, not a second source decoder.
        IDirect3DDevice9* device=(IDirect3DDevice9*)RwD3D9GetCurrentD3DDevice();
        IDirect3DBaseTexture9* base=0;IDirect3DSurface9* source=0;IDirect3DSurface9* copy=0;
        bool ok=device&&RwD3D9SetTexture(tex,0)&&SUCCEEDED(device->GetTexture(0,&base))&&base&&base->GetType()==D3DRTYPE_TEXTURE;
        if(ok)ok=SUCCEEDED(((IDirect3DTexture9*)base)->GetSurfaceLevel(0,&source))&&source;
        D3DSURFACE_DESC description={};
        if(ok)ok=SUCCEEDED(source->GetDesc(&description))&&description.Width==(unsigned)width&&description.Height==(unsigned)height;
        if(ok)ok=SUCCEEDED(device->CreateOffscreenPlainSurface(width,height,D3DFMT_A8R8G8B8,D3DPOOL_SYSTEMMEM,&copy,0))&&copy;
        if(ok)ok=SUCCEEDED(D3DXLoadSurfaceFromSurface(copy,0,0,source,0,0,D3DX_FILTER_NONE,0));
        D3DLOCKED_RECT lock={};bool locked=false;
        if(ok){locked=SUCCEEDED(copy->LockRect(&lock,0,D3DLOCK_READONLY));ok=locked&&lock.pBits&&lock.Pitch>=width*4;}
        if(ok){
            std::ofstream out(output,std::ios::binary|std::ios::trunc);out.write("OTR1",4);
            ok=writeU32(out,width)&&writeU32(out,height)&&writeU32(out,RwRasterGetDepth(raster))&&
                writeU32(out,RwRasterGetFormat(raster))&&writeU32(out,RwRasterGetNumLevels(raster))&&
                writeU32(out,RwTextureGetFilterMode(tex))&&writeU32(out,RwTextureGetAddressingU(tex))&&
                writeU32(out,RwTextureGetAddressingV(tex))&&writeU32(out,(uint32_t)count);
            std::vector<unsigned char> row((size_t)width*4);
            for(int y=0;ok&&y<height;++y){
                const unsigned char* data=(const unsigned char*)lock.pBits+(size_t)y*lock.Pitch;
                for(int x=0;x<width;++x){row[x*4]=data[x*4+2];row[x*4+1]=data[x*4+1];row[x*4+2]=data[x*4];row[x*4+3]=data[x*4+3];}
                out.write((const char*)row.data(),row.size());ok=!!out;
            }
        }
        if(locked&&!SUCCEEDED(copy->UnlockRect()))ok=false;
        RwD3D9SetTexture(0,0);if(copy)copy->Release();if(source)source->Release();if(base)base->Release();
        RwTextureDestroy(tex);closeEngine(wnd);
        if(!ok){DeleteFileA(output);message("native RGBA readback failed");return 37;}return 0;
    }
    std::ofstream out(output,std::ios::binary|std::ios::trunc);
    out<<"{\"width\":"<<width<<",\"height\":"<<height<<",\"depth\":"<<RwRasterGetDepth(raster)<<",\"format\":"<<RwRasterGetFormat(raster)<<",\"levels\":"<<RwRasterGetNumLevels(raster)<<",\"nativeReaderPass\":true,\"renderingTested\":false}";
    bool ok=!!out;out.close();RwTextureDestroy(tex);closeEngine(wnd);if(!ok){message("texture metadata output failed");return 35;}return 0;
}

static void put16(std::ofstream& f,uint16_t v){f.write((char*)&v,2);}
static void put32(std::ofstream& f,uint32_t v){f.write((char*)&v,4);}
static int16_t sample16(const unsigned char* p,FMOD_SOUND_FORMAT fmt){
    if(fmt==FMOD_SOUND_FORMAT_PCM8)return (int16_t)(((int)p[0]-128)<<8);
    if(fmt==FMOD_SOUND_FORMAT_PCM16){int16_t v;std::memcpy(&v,p,2);return v;}
    if(fmt==FMOD_SOUND_FORMAT_PCM24){int32_t v=(int32_t)p[0]|((int32_t)p[1]<<8)|((int32_t)p[2]<<16);if(v&0x800000)v|=(int32_t)0xff000000;return (int16_t)(v>>8);}
    if(fmt==FMOD_SOUND_FORMAT_PCM32){int32_t v;std::memcpy(&v,p,4);return (int16_t)(v>>16);}
    if(fmt==FMOD_SOUND_FORMAT_PCMFLOAT){float v;std::memcpy(&v,p,4);if(!std::isfinite(v))v=0;v=std::max(-1.f,std::min(1.f,v));return (int16_t)(v*32767.f);}
    return 0;
}
static int audio(const char* input,const char* output) {
    unsigned inputBytes=0;if(!fileSize(input,inputBytes)){message("audio input missing, empty, or larger than 128 MiB");return 20;}
    FMOD_SYSTEM* system=0;FMOD_SOUND* sound=0;FMOD_RESULT r=FMOD_System_Create(&system);
    if(r!=FMOD_OK||!system){message("FMOD system create failed");return 21;}
    r=FMOD_System_SetOutput(system,FMOD_OUTPUTTYPE_NOSOUND);
    if(r!=FMOD_OK){FMOD_System_Release(system);message("FMOD silent decoder output setup failed");return 22;}
    r=FMOD_System_Init(system,1,FMOD_INIT_NORMAL,0);if(r!=FMOD_OK){FMOD_System_Release(system);message("FMOD init failed");return 23;}
    r=FMOD_System_CreateSound(system,input,FMOD_OPENONLY|FMOD_ACCURATETIME,0,&sound);
    if(r!=FMOD_OK||!sound){FMOD_System_Close(system);FMOD_System_Release(system);message("FMOD could not decode source");return 24;}
    FMOD_SOUND_TYPE type;FMOD_SOUND_FORMAT fmt;int channels=0,bits=0,priority=0;float rate=0;unsigned long long pcmBytes=0;
    r=FMOD_Sound_GetFormat(sound,&type,&fmt,&channels,&bits);
    unsigned int length=0;if(r==FMOD_OK)r=FMOD_Sound_GetLength(sound,&length,FMOD_TIMEUNIT_PCMBYTES);
    if(r==FMOD_OK)r=FMOD_Sound_GetDefaults(sound,&rate,&priority);
    int bytesPer=(fmt==FMOD_SOUND_FORMAT_PCM8)?1:(fmt==FMOD_SOUND_FORMAT_PCM16)?2:(fmt==FMOD_SOUND_FORMAT_PCM24)?3:4;
    if(r!=FMOD_OK||channels<1||channels>8||rate<8000||rate>192000||bits!=bytesPer*8||length==0){
        char detail[192];std::sprintf(detail,"unsupported FMOD stream format (result=%d type=%d format=%d channels=%d bits=%d rate=%.2f bytes=%u)",(int)r,(int)type,(int)fmt,channels,bits,rate,length);
        FMOD_Sound_Release(sound);FMOD_System_Close(system);FMOD_System_Release(system);message(detail);return 25;
    }
    pcmBytes=length;if(pcmBytes>MAX_FILE || pcmBytes%((unsigned long long)channels*bytesPer)){FMOD_Sound_Release(sound);FMOD_System_Close(system);FMOD_System_Release(system);message("decoded audio exceeds 128 MiB or has partial frames");return 26;}
    std::vector<unsigned char> pcm((size_t)pcmBytes);unsigned int read=0;r=FMOD_Sound_ReadData(sound,pcm.data(),(unsigned int)pcm.size(),&read);
    FMOD_Sound_Release(sound);FMOD_System_Close(system);FMOD_System_Release(system);
    if(r!=FMOD_OK||read!=pcm.size()){message("FMOD PCM decode returned a short read");return 27;}
    const uint32_t frames=(uint32_t)(pcm.size()/(channels*bytesPer));const uint64_t dataBytes=(uint64_t)frames*channels*2;
    if(dataBytes>MAX_FILE-44){message("converted WAV exceeds 128 MiB");return 28;}
    std::ofstream out(output,std::ios::binary|std::ios::trunc);if(!out){message("cannot create WAV output");return 29;}
    out.write("RIFF",4);put32(out,(uint32_t)(36+dataBytes));out.write("WAVEfmt ",8);put32(out,16);put16(out,1);put16(out,(uint16_t)channels);put32(out,(uint32_t)rate);put32(out,(uint32_t)rate*channels*2);put16(out,(uint16_t)(channels*2));put16(out,16);out.write("data",4);put32(out,(uint32_t)dataBytes);
    for(size_t i=0;i<pcm.size();i+=bytesPer)put16(out,(uint16_t)sample16(&pcm[i],fmt));
    if(!out){message("WAV output write failed");return 30;}return 0;
}
int main(int argc,char** argv){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    if(argc!=4){message("usage: media-helper.exe model input.dff output.mesh | audio input.ext output.wav");return 2;}
    if(std::strcmp(argv[1],"map-workshop")==0)return MapWorkshop(argv[2],argv[3]);
    if(std::strcmp(argv[1],"model-bsp-bindings")==0)return modelBindings(argv[2],argv[3],true);
    if(std::strcmp(argv[1],"model")==0)return model(argv[2],argv[3]);
    if(std::strcmp(argv[1],"model-dependencies")==0)return model(argv[2],argv[3],true);
    if(std::strcmp(argv[1],"model-bindings")==0)return modelBindings(argv[2],argv[3]);
    if(std::strcmp(argv[1],"model-bsp-bindings")==0)return modelBindings(argv[2],argv[3],true);
    if(std::strcmp(argv[1],"model-poses")==0)return modelPoses(argv[2],argv[3]);
    if(std::strcmp(argv[1],"texture")==0)return texture(argv[2],argv[3]);
    if(std::strcmp(argv[1],"texture-pixels")==0)return texture(argv[2],argv[3],true);
    if(std::strcmp(argv[1],"audio")==0)return audio(argv[2],argv[3]);
    message("unknown mode");return 2;
}
