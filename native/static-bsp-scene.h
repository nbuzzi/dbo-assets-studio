// Native static-clump -> standalone BSP. New artifact only; no source/runtime writes.
// RtWorldImport preserves every oriented triangle, UV, prelight, material and
// transformed normal; RpWorldStreamRead verifies the serialized result, not a stub.
#include <rtimport.h>
#include <array>
#include <map>
#include <algorithm>
#include <cmath>

struct BspFace { std::array<std::array<unsigned,6>,3> vertex; std::array<RwV3d,3> normal; std::string material; };
static unsigned BspFloat(float v){unsigned bits=0;if(v==0)v=0;std::memcpy(&bits,&v,4);return bits;}
static unsigned BspColor(RwRGBA c){return c.red|(unsigned(c.green)<<8)|(unsigned(c.blue)<<16)|(unsigned(c.alpha)<<24);}
static bool BspFinite(RwV3d p){return std::isfinite(p.x)&&std::isfinite(p.y)&&std::isfinite(p.z);}
static std::string BspMaterial(RpMaterial* mat){
    std::string key;RwTexture* t=RpMaterialGetTexture(mat);if(!t)return key;
    key=RwTextureGetName(t);key.push_back(0);key+=RwTextureGetMaskName(t);key.push_back(0);
    unsigned words[]={BspColor(*RpMaterialGetColor(mat)),BspFloat(RpMaterialGetSurfaceProperties(mat)->ambient),
        BspFloat(RpMaterialGetSurfaceProperties(mat)->diffuse),BspFloat(RpMaterialGetSurfaceProperties(mat)->specular),
        unsigned(RwTextureGetFilterMode(t)),unsigned(RwTextureGetAddressingU(t)),unsigned(RwTextureGetAddressingV(t))};
    key.append((const char*)words,sizeof(words));return key;
}
static std::string BspFaceKey(BspFace& f){
    int best=0;for(int i=1;i<3;i++)if(f.vertex[i]<f.vertex[best])best=i;
    if(best){std::rotate(f.vertex.begin(),f.vertex.begin()+best,f.vertex.end());std::rotate(f.normal.begin(),f.normal.begin()+best,f.normal.end());}
    std::string key=f.material;key.append((const char*)f.vertex.data(),sizeof(f.vertex));return key;
}
static std::array<unsigned,6> BspVertex(RwV3d p,RwTexCoords uv,RwRGBA color){
    return {{BspFloat(p.x),BspFloat(p.y),BspFloat(p.z),BspFloat(uv.u),BspFloat(uv.v),BspColor(color)}};
}
struct BspBuilder {RtWorldImport* import;bool ok;unsigned atomics,vertices,triangles,flags;std::map<std::string,std::vector<std::array<RwV3d,3>>> faces;};
static std::string gBspAdditionPath;
static bool BspAddStaticMesh(BspBuilder& b){
    if(gBspAdditionPath.empty())return true;
    std::ifstream in(gBspAdditionPath,std::ios::binary);char magic[4];unsigned count,nv,nt,nm;
    in.read(magic,4);in.read((char*)&count,4);in.read((char*)&nv,4);in.read((char*)&nt,4);in.read((char*)&nm,4);
    if(!in||memcmp(magic,"OSM1",4)||count!=1||nv<3||nv>600||!nt||nt>200||nm!=1)return false;
    struct V{float x,y,z,u,v;};std::vector<V> source(nv);std::vector<std::array<unsigned,4>> faces(nt);RwRGBA color;char name[128];
    in.read((char*)source.data(),nv*sizeof(V));in.read((char*)faces.data(),nt*16);in.read((char*)&color,4);in.read(name,128);
    if(!in||in.peek()!=EOF||!memchr(name,0,128)||strcmp(name,"orig_sw_shedao_platform"))return false;
    RwTexture* texture=RwTextureRead(name,0);if(!texture)return false;RpMaterial* mat=RpMaterialCreate();if(!mat){RwTextureDestroy(texture);return false;}
    RpMaterialSetTexture(mat,texture);RwTextureDestroy(texture);RpMaterialSetColor(mat,&color);
    int mi=RtWorldImportAddMaterial(b.import,mat);std::string key=BspMaterial(mat);RpMaterialDestroy(mat);
    if(mi<0||!RtWorldImportAddNumVertices(b.import,nv)||!RtWorldImportAddNumTriangles(b.import,nt))return false;
    auto* v=RtWorldImportGetVertices(b.import);auto* t=RtWorldImportGetTriangles(b.import);std::vector<RwV3d> normals(nv,RwV3d{0,0,0});
    for(const auto& f:faces){if(f[0]>=nv||f[1]>=nv||f[2]>=nv||f[3])return false;RwV3d a={source[f[0]].x,source[f[0]].y,source[f[0]].z},u={source[f[1]].x-a.x,source[f[1]].y-a.y,source[f[1]].z-a.z},w={source[f[2]].x-a.x,source[f[2]].y-a.y,source[f[2]].z-a.z},n;RwV3dCrossProduct(&n,&u,&w);if(RwV3dLength(&n)<=0)return false;for(int k=0;k<3;k++)RwV3dAdd(&normals[f[k]],&normals[f[k]],&n);}
    for(unsigned i=0;i<nv;i++){auto& q=v[b.vertices+i];q.OC={source[i].x,source[i].y,source[i].z};if(!BspFinite(q.OC)||fabs(q.OC.x)>2048||fabs(q.OC.y)>2048||fabs(q.OC.z)>2048||!std::isfinite(source[i].u)||!std::isfinite(source[i].v)||RwV3dLength(&normals[i])<=0)return false;RwV3dNormalize(&q.normal,&normals[i]);q.preLitCol={255,255,255,255};q.texCoords[0]={source[i].u,source[i].v};q.matIndex=mi;q.pUserdata=0;}
    for(unsigned i=0;i<nt;i++){BspFace face;face.material=key;t[b.triangles+i].matIndex=mi;t[b.triangles+i].pUserdata=0;for(int k=0;k<3;k++){unsigned index=b.vertices+faces[i][k];t[b.triangles+i].vertIndex[k]=index;face.vertex[k]=BspVertex(v[index].OC,v[index].texCoords[0],v[index].preLitCol);face.normal[k]=v[index].normal;}b.faces[BspFaceKey(face)].push_back(face.normal);}
    b.vertices+=nv;b.triangles+=nt;b.atomics++;return true;
}
static RpAtomic* BspCollectAtomic(RpAtomic* atomic,void* data){
    auto& b=*(BspBuilder*)data;if(!b.ok)return atomic;
    RpGeometry* g=RpAtomicGetGeometry(atomic);RwFrame* frame=RpAtomicGetFrame(atomic);RwMatrix* ltm=frame?RwFrameGetLTM(frame):0;
    if(!g||!ltm||RpGeometryGetNumMorphTargets(g)!=1||RpGeometryGetNumTexCoordSets(g)!=1||RpSkinGeometryGetSkin(g)||RpHAnimFrameGetHierarchy(frame)) {message("BSP rejected: static geometry/morph/UV/skin/hierarchy contract");b.ok=false;return atomic;}
    const unsigned nv=RpGeometryGetNumVertices(g),nt=RpGeometryGetNumTriangles(g);
    unsigned geometryFlags=RpGeometryGetFlags(g),worldFlags=rpWORLDNORMALS|rpWORLDTEXTURED;
    if(geometryFlags&rpGEOMETRYPRELIT)worldFlags|=rpWORLDPRELIT;
    if(geometryFlags&rpGEOMETRYLIGHT)worldFlags|=rpWORLDLIGHT;
    if(geometryFlags&rpGEOMETRYMODULATEMATERIALCOLOR)worldFlags|=rpWORLDMODULATEMATERIALCOLOR;
    if(b.atomics&&b.flags!=worldFlags){std::fprintf(stderr,"BSP rejected: mixed lighting flags prior=0x%x next=0x%x\n",b.flags,worldFlags);b.ok=false;return atomic;}b.flags=worldFlags;
    if(!nv||!nt||b.vertices+nv>60000||b.triangles+nt>100000||++b.atomics>128){std::fprintf(stderr,"BSP rejected: aggregate counts nv=%u nt=%u priorV=%u priorT=%u\n",nv,nt,b.vertices,b.triangles);b.ok=false;return atomic;}
    RwV3d basis[]={ltm->right,ltm->up,ltm->at};float length[3];
    for(int i=0;i<3;i++){length[i]=RwV3dLength(&basis[i]);if(!BspFinite(basis[i])||!std::isfinite(length[i])||length[i]<=0)b.ok=false;}
    if(!b.ok||fabs(length[0]-length[1])>0.00001f||fabs(length[0]-length[2])>0.00001f||
        fabs(RwV3dDotProduct(&basis[0],&basis[1]))>0.00001f||fabs(RwV3dDotProduct(&basis[0],&basis[2]))>0.00001f||fabs(RwV3dDotProduct(&basis[1],&basis[2]))>0.00001f){std::fprintf(stderr,"BSP rejected: nonuniform/sheared basis lengths %.9g %.9g %.9g\n",length[0],length[1],length[2]);b.ok=false;return atomic;}
    RpMorphTarget* morph=RpGeometryGetMorphTarget(g,0);RwV3d* position=RpMorphTargetGetVertices(morph);RwV3d* normal=RpMorphTargetGetVertexNormals(morph);
    // The native accessor takes a one-based RwTextureCoordinateIndex, unlike
    // the sector texCoords array. Match CollectMesh's first-coordinate access.
    RwTexCoords* uv=RpGeometryGetVertexTexCoords(g,rwTEXTURECOORDINATEINDEX0);RwRGBA* color=RpGeometryGetPreLightColors(g);
    if(!position||!normal||!uv||((worldFlags&rpWORLDPRELIT)&&!color)){std::fprintf(stderr,"BSP rejected: vertex channels geometryFlags=0x%x position=%d normal=%d uv=%d prelight=%d\n",geometryFlags,position!=0,normal!=0,uv!=0,color!=0);b.ok=false;return atomic;}
    std::vector<RwV3d> p(nv),n(nv);RwV3dTransformPoints(p.data(),position,nv,ltm);RwV3dTransformVectors(n.data(),normal,nv,ltm);
    for(unsigned i=0;i<nv;i++){if(!BspFinite(p[i])||!BspFinite(n[i])||!std::isfinite(uv[i].u)||!std::isfinite(uv[i].v)){std::fprintf(stderr,"BSP rejected: nonfinite vertex attribute at %u flags=0x%x XYZ=(%.9g,%.9g,%.9g) normal=(%.9g,%.9g,%.9g) UV=(%.9g,%.9g)\n",i,geometryFlags,p[i].x,p[i].y,p[i].z,n[i].x,n[i].y,n[i].z,uv[i].u,uv[i].v);b.ok=false;return atomic;}if(RwV3dLength(&n[i])>0)RwV3dNormalize(&n[i],&n[i]);}
    unsigned oldV=b.vertices,oldT=b.triangles;
    if(!RtWorldImportAddNumVertices(b.import,nv)||!RtWorldImportAddNumTriangles(b.import,nt)){message("BSP rejected: native import allocation");b.ok=false;return atomic;}
    auto* vertices=RtWorldImportGetVertices(b.import);auto* triangles=RtWorldImportGetTriangles(b.import);
    for(unsigned i=0;i<nv;i++){auto& v=vertices[oldV+i];v.OC=p[i];v.normal=n[i];v.preLitCol=color?color[i]:RwRGBA{255,255,255,255};v.texCoords[0]=uv[i];v.pUserdata=0;}
    const RpTriangle* source=RpGeometryGetTriangles(g);
    for(unsigned i=0;i<nt;i++){
        const auto& t=source[i];if(t.matIndex>=RpGeometryGetNumMaterials(g)||t.vertIndex[0]>=nv||t.vertIndex[1]>=nv||t.vertIndex[2]>=nv){message("BSP rejected: native triangle index/material");b.ok=false;return atomic;}
        RpMaterial* mat=RpGeometryGetMaterial(g,t.matIndex);std::string material=BspMaterial(mat);if(material.empty()){message("BSP rejected: material has no bound texture");b.ok=false;return atomic;}
        int mi=RtWorldImportGetMaterialIndex(b.import,mat);if(mi<0)mi=RtWorldImportAddMaterial(b.import,mat);if(mi<0){message("BSP rejected: native material allocation");b.ok=false;return atomic;}
        auto& target=triangles[oldT+i];target.matIndex=mi;target.pUserdata=0;BspFace face;face.material=material;
        for(int k=0;k<3;k++){unsigned vi=t.vertIndex[k];target.vertIndex[k]=oldV+vi;vertices[oldV+vi].matIndex=mi;
            face.vertex[k]=BspVertex(p[vi],uv[vi],vertices[oldV+vi].preLitCol);face.normal[k]=n[vi];}
        b.faces[BspFaceKey(face)].push_back(face.normal);
    }
    b.vertices+=nv;b.triangles+=nt;return atomic;
}
struct BspReadback {RpWorld* world;BspBuilder* source;bool ok;unsigned vertices,triangles;};
static RpWorldSector* BspCheckSector(RpWorldSector* s,void* data){
    auto& r=*(BspReadback*)data;r.vertices+=s->numVertices;r.triangles+=s->numTriangles;
    if(!s->vertices||!s->normals||!s->texCoords[0]||((r.source->flags&rpWORLDPRELIT)&&!s->preLitLum)){r.ok=false;return s;}
    for(int i=0;i<s->numTriangles;i++){
        auto& t=s->triangles[i];int mi=s->matListWindowBase+t.matIndex;
        if(mi<0||mi>=RpWorldGetNumMaterials(r.world)){r.ok=false;return s;}BspFace f;f.material=BspMaterial(RpWorldGetMaterial(r.world,mi));
        for(int k=0;k<3;k++){unsigned vi=t.vertIndex[k];if(vi>=unsigned(s->numVertices)){r.ok=false;return s;}
            f.vertex[k]=BspVertex(s->vertices[vi],s->texCoords[0][vi],s->preLitLum?s->preLitLum[vi]:RwRGBA{255,255,255,255});
            f.normal[k]=RwV3d{float(s->normals[vi].x)/128,float(s->normals[vi].y)/128,float(s->normals[vi].z)/128};}
        auto it=r.source->faces.find(BspFaceKey(f));if(it==r.source->faces.end()||it->second.empty()){std::fprintf(stderr,"BSP rejected: oriented face/UV/prelight/material mismatch triangle=%d material=%d texture=%s\n",i,mi,RwTextureGetName(RpMaterialGetTexture(RpWorldGetMaterial(r.world,mi))));r.ok=false;return s;}
        auto expected=it->second.back();it->second.pop_back();
        for(int k=0;k<3;k++)if(fabs(expected[k].x-f.normal[k].x)>0.02f||fabs(expected[k].y-f.normal[k].y)>0.02f||fabs(expected[k].z-f.normal[k].z)>0.02f)r.ok=false;
    }
    return s;
}
static bool BakeStaticBsp(RpClump* clump,const char* output){
    BspBuilder b={};b.import=RtWorldImportCreate();b.ok=b.import!=0;if(!b.ok)return false;
    RpClumpForAllAtomics(clump,BspCollectAtomic,&b);if(!b.ok||!BspAddStaticMesh(b)){message("BSP rejected during import collection/addition");RtWorldImportDestroy(b.import);return false;}
    std::fprintf(stderr,"BSP collected atomics=%u vertices=%u triangles=%u flags=0x%x\n",b.atomics,b.vertices,b.triangles,b.flags);
    RtWorldImportParameters params;RtWorldImportParametersInit(&params);
    params.worldSectorMaxSize=100000;params.maxWorldSectorPolygons=100000;params.flags=b.flags;
    params.numTexCoordSets=1;params.calcNormals=FALSE;params.conditionGeometry=FALSE;params.fixTJunctions=FALSE;
    RpWorld* world=RtWorldImportCreateWorld(b.import,&params);RtWorldImportDestroy(b.import);
    if(!world){message("BSP rejected: native RtWorldImportCreateWorld");return false;}
    if(!RpCollisionWorldBuildData(world,0)){message("BSP rejected: collision tree build");RpWorldDestroy(world);return false;}
    // Serialize to a private native memory stream and read it back before exposing bytes.
    RwMemory memory={};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMWRITE,&memory);
    bool ok=stream&&RpWorldStreamWrite(world,stream)!=0;if(stream)RwStreamClose(stream,&memory);RpWorldDestroy(world);
    if(!ok||!memory.start||!memory.length||memory.length>MAX_FILE){message("BSP rejected: bounded native serialization");if(memory.start)RwFree(memory.start);return false;}
    stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);RwChunkHeaderInfo h={};
    ok=stream&&RwStreamReadChunkHeaderInfo(stream,&h)&&h.type==rwID_WORLD&&h.length+12==memory.length;
    world=ok?RpWorldStreamRead(stream):0;if(stream)RwStreamClose(stream,0);
    BspReadback readback={world,&b,world!=0,0,0};if(world)RpWorldForAllWorldSectors(world,BspCheckSector,&readback);
    // RtWorldImport adds its sector-overlap layout flag and the explicit
    // one-coordinate-set stream bits. Keep all material/lighting flags exact.
    const unsigned expectedWorldFlags=b.flags|rpWORLDSECTORSOVERLAP|(1u<<16);
    ok=readback.ok&&readback.triangles==b.triangles&&unsigned(RpWorldGetFlags(world))==expectedWorldFlags;for(auto& f:b.faces)if(!f.second.empty())ok=false;
    if(!ok)std::fprintf(stderr,"BSP rejected: stream proof sourceV=%u sourceT=%u readV=%u readT=%u flags=0x%x expectedFlags=0x%x nativeReadback=%d\n",b.vertices,b.triangles,readback.vertices,readback.triangles,world?unsigned(RpWorldGetFlags(world)):0,b.flags,int(readback.ok));
    if(ok){std::ofstream out(output,std::ios::binary);out.write("SBP1",4);unsigned header[]={b.atomics,b.vertices,b.triangles,readback.vertices,readback.triangles,unsigned(RpWorldGetNumMaterials(world)),memory.length};out.write((char*)header,sizeof(header));out.write((char*)memory.start,memory.length);ok=!!out;}
    if(world)RpWorldDestroy(world);RwFree(memory.start);return ok;
}
