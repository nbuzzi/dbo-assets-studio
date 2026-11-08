// Native content chunks from explicitly selected WORLD-space ground triangles.
// Not a whole-map Navi generator, floor classifier or runtime installer.
#include <Windows.h>
#include "PathEngine/i_pathengine.h"
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <vector>
#include <cstdint>
#include <cstring>
#include "NtlNaviDefine.h"
#include "Source/NtlNaviPENaviFVMesh.h"
static void Need(bool ok,const char* text){if(!ok){fprintf(stderr,"FAIL %s\n",text);exit(1);}}
static unsigned Read(FILE*f){unsigned x;Need(fread(&x,4,1,f)==1,"complete integer");return x;}
struct Errors: iErrorHandler { unsigned count=0;eAction handle(const char* type,const char* description,const char* const*) override {
    ++count;fprintf(stderr,"PE %s: %s\n",type,description);return CONTINUE;} };
struct Vertex {tSigned32 x,y;float z;};
struct Face {unsigned a,b,c;};
struct Mesh: iFaceVertexMesh {
    std::vector<Vertex> v;std::vector<Face> f;
    tSigned32 faces()const override{return static_cast<tSigned32>(f.size());}
    tSigned32 vertices()const override{return static_cast<tSigned32>(v.size());}
    tSigned32 vertexIndex(tSigned32 face,tSigned32 corner)const override{
        // Native CNtlNaviPENaviFVMesh swaps corners1/2 again on import.
        const auto& t=f[face];return static_cast<tSigned32>(corner==0?t.a:corner==1?t.c:t.b);
    }
    tSigned32 vertexX(tSigned32 i)const override{return v[i].x;}
    tSigned32 vertexY(tSigned32 i)const override{return v[i].y;}
    float vertexZ(tSigned32 i)const override{return v[i].z;}
    tSigned32 faceAttribute(tSigned32,tSigned32)const override{return -1;}
};
struct Memory: iOutputStream {std::vector<char> bytes;void put(const char* p,tUnsigned32 n)override{
    Need(bytes.size()+n<=128*1024*1024,"bounded content chunk");bytes.insert(bytes.end(),p,p+n);} };
static void Same(const Mesh& source,const iFaceVertexMesh* target){
    Need(target&&target->vertices()==source.vertices()&&target->faces()==source.faces(),"native chunk complete geometry");
    for(tSigned32 i=0;i<source.vertices();++i)Need(source.vertexX(i)==target->vertexX(i)&&source.vertexY(i)==target->vertexY(i)&&source.vertexZ(i)==target->vertexZ(i),"native chunk vertex preservation");
    for(tSigned32 i=0;i<source.faces();++i)for(tSigned32 c=0;c<3;++c)Need(source.vertexIndex(i,c)==target->vertexIndex(i,c),"native chunk winding/index preservation");
}
static void NativeImporter(const Mesh& source,iContentChunkInstance* instance){
    // Exercise the actual Taiwan exporter adapter, not a test reimplementation.
    // World-baked geometry is attached with identity SRT; exporter adds inverseZ.
    CNtlNaviMatrix3 reverse(true);reverse.Scale(1.f,1.f,-1.f);
    CNtlNaviMatrix4 world(true);world.SetElem(reverse);
    CNtlNaviPENaviFVMesh imported(instance,world);
    Need(imported.vertices()==source.vertices()&&imported.faces()==source.faces(),"production importer complete geometry");
    for(tSigned32 i=0;i<source.vertices();++i){
        Need(std::abs(imported.vertexX(i)-source.v[i].x)<=1&&std::abs(imported.vertexY(i)+source.v[i].y)<=1&&
            std::abs(imported.vertexZ(i)-source.v[i].z)<=.001f,"production importer world coordinates within native quantization");
    }
    for(tSigned32 i=0;i<source.faces();++i){const auto& f=source.f[i];
        Need(imported.vertexIndex(i,0)==static_cast<tSigned32>(f.a)&&imported.vertexIndex(i,1)==static_cast<tSigned32>(f.b)&&
            imported.vertexIndex(i,2)==static_cast<tSigned32>(f.c),"production importer original winding");
    }
}
int main(int argc,char**argv){
    if(argc!=4)return 2; // private DLL, canonical NCP1 snapshot, NEW TOK
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    FILE* file=fopen(argv[2],"rb");Need(file!=NULL,"ground piece snapshot");char magic[4];
    Need(fread(magic,4,1,file)==1&&memcmp(magic,"NCP1",4)==0,"piece header");unsigned nv=Read(file),nt=Read(file);
    Need(nv>=3&&nv<=1000000&&nt>0&&nt<=1000000,"bounded piece counts");Mesh mesh;
    for(unsigned i=0;i<nv;++i){float p[3];Need(fread(p,4,3,file)==3,"world vertex");
        for(float x:p)Need(std::isfinite(x)&&std::abs(x)<=2048,"Devildom vertex bounds");
        // Importer reverses localZ BEFORE identity SRT. Store inverseZ,
        // PathEngine horizontalXY=worldX/-worldZ, heightZ=worldY, ratio100.
        mesh.v.push_back({static_cast<tSigned32>(p[0]*100.f),static_cast<tSigned32>(-p[2]*100.f),p[1]*100.f});
    }
    for(unsigned i=0;i<nt;++i){Face f{Read(file),Read(file),Read(file)};
        Need(f.a<nv&&f.b<nv&&f.c<nv&&f.a!=f.b&&f.a!=f.c&&f.b!=f.c,"triangle indices");mesh.f.push_back(f);
        auto a=mesh.v[f.a],b=mesh.v[f.c],c=mesh.v[f.b];
        std::int64_t area=std::int64_t(b.x-a.x)*(c.y-a.y)-std::int64_t(c.x-a.x)*(b.y-a.y);
        Need(area<0,"clockwise nondegenerate quantized ground; no automatic winding repair");
    }
    Need(fgetc(file)==EOF&&!ferror(file),"complete snapshot EOF");fclose(file);
    HMODULE dll=LoadLibraryA(argv[1]);Need(dll!=NULL,"private PathEngine DLL");
    typedef iPathEngine* (__stdcall *GetInterface)(iErrorHandler*);auto get=reinterpret_cast<GetInterface>(GetProcAddress(dll,(LPCSTR)1));Errors errors;
    iPathEngine* pe=get?get(&errors):nullptr;Need(pe&&pe->getInterfaceMajorVersion()==PATHENGINE_INTERFACE_MAJOR_VERSION&&pe->getInterfaceMinorVersion()>=PATHENGINE_INTERFACE_MINOR_VERSION,"compatible native PathEngine interface");
    pe->setErrorHandler(&errors);iContentChunk* chunk=pe->newContentChunk(&mesh,nullptr,nullptr);
    Need(chunk&&chunk->hasGroundComponent()&&!chunk->hasAnchorsAndShapesComponent()&&chunk->preValidate(1,0)&&!errors.count,"native ground content chunk");
    auto* instance=chunk->instantiate(0,1,0,0,0,-1);Need(instance!=nullptr,"native identity instance");Same(mesh,instance->getGroundPointer());
    Memory saved;chunk->save(&saved);Need(!saved.bytes.empty()&&!errors.count,"native TOK serialization");
    auto* reloaded=pe->loadContentChunk(saved.bytes.data(),static_cast<tUnsigned32>(saved.bytes.size()));
    Need(reloaded&&reloaded->preValidate(1,0)&&!errors.count,"native TOK readback");
    auto* back=reloaded->instantiate(0,1,0,0,0,-1);Need(back!=nullptr,"native reloaded identity instance");Same(mesh,back->getGroundPointer());NativeImporter(mesh,back);
    Memory savedAgain;reloaded->save(&savedAgain);Need(savedAgain.bytes==saved.bytes&&!errors.count,"native byte-exact TOK roundtrip");
    FILE* output=fopen(argv[3],"wbx");Need(output!=NULL,"fresh TOK output");Need(fwrite(saved.bytes.data(),1,saved.bytes.size(),output)==saved.bytes.size()&&fclose(output)==0,"native TOK write");
    printf("PASS native selected-ground TOK vertices=%u triangles=%u bytes=%zu ratio=100 inverseZ/corner-swap production-importer=true toleranceXZ=0.01 toleranceY=0.00001; NOT whole-map navigation or collision acceptance\n",nv,nt,saved.bytes.size());
    pe->deleteAllObjects();FreeLibrary(dll);return 0;
}
