// Read-only Devildom6001 diagnostic using the same Navigator as GameServer.
// This does not export geometry, modify caches or certify a live fight.
#include <Windows.h>
#include "NtlNavi.h"
#include <cstdio>
#include <cstdarg>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <fstream>
#include <vector>
#include <queue>
#include <algorithm>
#include "PathEngine/i_pathengine.h"
static void Need(bool ok,const char* label){if(!ok){fprintf(stderr,"FAIL %s\n",label);exit(1);}}
class NaviLogger:public INtlNaviLog { public: void Log(const char* fmt,...) override {
    char buf[8192];va_list args;va_start(args,fmt);vsnprintf(buf,sizeof(buf),fmt,args);va_end(args);printf("NAVI %s\n",buf);
}};
struct RawErrors:iErrorHandler{eAction handle(const char*type,const char*text,const char*const*)override{printf("RAW_PE %s %s\n",type,text);return CONTINUE;}};
static void RawDiagnostic(const char*root,const char*dllPath){
    std::ifstream in(std::string(root)+"/6009/pathengine/0.mf",std::ios::binary|std::ios::ate);auto size=in.tellg();Need(in.good()&&size>0&&size<128*1024*1024,"read-only bounded raw mesh");std::vector<char> bytes((size_t)size);in.seekg(0);Need(bool(in.read(bytes.data(),(std::streamsize)bytes.size())),"complete raw mesh read");
    HMODULE dll=LoadLibraryA(dllPath);typedef iPathEngine*(__stdcall*Get)(iErrorHandler*);auto get=dll?reinterpret_cast<Get>(GetProcAddress(dll,(LPCSTR)1)):nullptr;RawErrors errors;auto*pe=get?get(&errors):nullptr;Need(pe!=nullptr,"raw interface");auto*m=pe->loadMeshFromBuffer("tok",bytes.data(),(tUnsigned32)bytes.size(),nullptr);Need(m!=nullptr,"raw native mesh");
    tSigned32 coords[][3]={{2300,-8000,4974},{-10000,3100,4974},{-14000,4000,5010},{-14600,4000,4956}};std::vector<cPosition> points;for(auto&c:coords)points.push_back(m->positionNear3DPoint(c,100,100));
    int faceCount=m->getNumberOf3DFaces();Need(faceCount>0&&faceCount<=2000000,"bounded raw face count");std::vector<bool> seen(faceCount);std::queue<int> q;int start=points[0].cell<0?-1:m->get3DFaceAtPosition(points[0]);Need(start<faceCount,"raw start face range");if(start>=0){seen[start]=true;q.push(start);}while(!q.empty()){int f=q.front();q.pop();for(int e=0;e<3;e++){int next=m->get3DFaceConnection(f,e);Need(next<faceCount,"raw neighbor face range");if(next>=0&&!seen[next]){seen[next]=true;q.push(next);}}}
    printf("RAW_COMPONENT faces=%d reachable=%zu\n",m->getNumberOf3DFaces(),std::count(seen.begin(),seen.end(),true));
    for(int f=0;f<m->getNumberOf3DFaces();f++){printf("RAW_FACE %d %d",f,int(seen[f]));for(int k=0;k<3;k++){tSigned32 x,y;float z;m->get3DFaceVertex(f,k,x,y,z);printf(" %d %d %.6f",x,y,z);}for(int e=0;e<3;e++)printf(" %d",m->get3DFaceConnection(f,e));puts("");}
    for(int radius:{1,30}){tSigned32 square[]={-radius,-radius,-radius,radius,radius,radius,radius,-radius};auto*s=pe->newShape(4,square);Need(s!=nullptr,"raw diagnostic shape");m->generateCollisionPreprocessFor(s,nullptr);m->generatePathfindPreprocessFor(s,nullptr);for(size_t i=0;i<points.size();i++){auto&p=points[i];int face=p.cell<0?-1:m->get3DFaceAtPosition(p);Need(face<faceCount,"raw probe face range");auto*path=p.cell<0||points[0].cell<0?nullptr:m->findShortestPath(s,nullptr,points[0],p);printf("RAW radius=%d point=%zu cell=%d face=%d connected=%d collision=%d path=%d\n",radius,i,p.cell,face,face>=0&&seen[face],p.cell>=0&&m->testPointCollision(s,nullptr,p),path!=nullptr);if(path)path->destroy();}}
    pe->deleteAllObjects();FreeLibrary(dll);
}
int main(int argc,char** argv){
    if(argc!=3&&argc!=5&&argc!=6)return 2; // Legacy Devildom, or explicit Snake Way points.
    unsigned world=6001;
    if(argc>=5){Need(strcmp(argv[3],"6009")==0,"explicit reviewed Snake Way resource6009");world=6009;}
    if(argc==6)Need(strcmp(argv[5],"--raw-diagnostic")==0,"explicit raw diagnostic opt-in");
    setvbuf(stdout,NULL,_IONBF,0);
    NaviLogger logger;INtlNavi* n=GetNaviEngine();Need(n->Create(&logger,argv[2],false),"Navigator create");
    vecdef_WorldIDList excluded;
    Need(n->LoadPathEngineData(argv[1],excluded,ePATH_DATA_LOAD_FLAG_LOAD_ALL)
        &&n->IsPropDataLoaded(world)&&n->IsPathDataLoaded(world),"complete selected-world reload");
    NAVI_INST_HANDLE h=n->CreateInstanceHandler(world);Need(h!=NULL,"selected-world instance");
    if(argc>=5){
        FILE*f=fopen(argv[4],"rb");Need(f!=NULL,"read-only literal probe selection");
        char magic[32];unsigned count=0;Need(fscanf(f,"%31s %u",magic,&count)==2&&strcmp(magic,"NAVPOINTS1")==0&&count>0&&count<=32,"bounded probe header");
        for(unsigned i=0;i<count;i++){
            char label[64];float x,y,z;Need(fscanf(f,"%63s %f %f %f",label,&x,&y,&z)==4&&std::isfinite(x)&&std::isfinite(y)&&std::isfinite(z)&&fabs(x)<=2048&&fabs(y)<=2048&&fabs(z)<=2048,"literal finite probe point");
            for(float radius:{.3f,.5f,1.f}){
                sNAVI_POS requested(x,y,z),point=requested,entry(20.9693508f,49.7398071f,-83.2736893f);bool found=n->FindNearestPos(h,radius,point),start=n->FindNearestPos(h,radius,entry);
                vecdef_NaviPosList path,reverse;bool forward=found&&start&&n->FindPath(h,radius,entry,point,path),back=found&&start&&n->FindPath(h,radius,point,entry,reverse);
                printf("POINT %s radius=%.1f requested=%.6f,%.6f,%.6f found=%d actual=%.6f,%.6f,%.6f fromEntry=%d points=%zu toEntry=%d reverse=%zu\n",label,radius,x,y,z,found,point.x,point.y,point.z,forward,path.size(),back,reverse.size());
                if(radius==.3f&&forward)for(size_t k=0;k<path.size();k++)printf("PATH %s index=%zu XYZ=%.6f,%.6f,%.6f\n",label,k,path[k].x,path[k].y,path[k].z);
            }
        }
        char extra[2];Need(fscanf(f,"%1s",extra)==EOF&&!ferror(f),"exact probe EOF");fclose(f);n->DeleteInstanceHandler(h);n->Delete();
        if(argc==6)RawDiagnostic(argv[1],argv[2]);puts("AUDIT Snake Way points complete; read-only, disconnected routes remain failures; NOT gameplay acceptance");return 0;
    }
    for(float radius: {0.3f,0.5f,1.0f,2.0f,3.0f}){
        sNAVI_POS boss(-220.551f,50.f,-101.902f),player(-228.551f,50.f,-101.902f);
        const bool supported=n->CanSupportAgent(h,radius);
        const auto line=n->FastCanMoveNearestDest(h,radius,boss,player);
        vecdef_NaviPosList route;const bool path=n->FindPath(h,radius,boss,player,route);
        printf("AGENT radius=%.1f supported=%d sightResult=%d path=%d points=%zu\n",radius,supported?1:0,int(line),path?1:0,route.size());
        for(auto bridge:{sNAVI_POS(-260.f,48.3405f,-110.f),sNAVI_POS(-340.f,48.3405f,-130.f),sNAVI_POS(-420.f,48.3405f,-102.f)}){
            const auto requested=bridge;const bool found=n->FindNearestPos(h,radius,bridge);
            vecdef_NaviPosList bridgeRoute;const bool connected=found&&n->FindPath(h,radius,player,bridge,bridgeRoute);
            printf("BRIDGE radius=%.1f requested=%.1f,%.1f nearest=%d resolved=%.5f,%.5f,%.5f path=%d points=%zu\n",radius,requested.x,requested.z,found?1:0,bridge.x,bridge.y,bridge.z,connected?1:0,bridgeRoute.size());
        }
    }
    n->DeleteInstanceHandler(h);n->Delete();
    printf("AUDIT complete; no export/install/restart; NOT gameplay acceptance\n");return 0;
}
