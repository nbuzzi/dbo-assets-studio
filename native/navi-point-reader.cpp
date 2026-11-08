// Read-only bounded point/path observations through GameServer's INtlNavi.
// Loads only explicitly selected resources. Does not export or repair Navi.
#include <Windows.h>
#include "NtlNavi.h"
#include <cstdio>
#include <cstdlib>
#include <cstdarg>
#include <cstring>
#include <cmath>
#include <vector>
#include <set>
#include <string>
static void Need(bool value,const char* text){if(!value){fprintf(stderr,"FAIL %s\n",text);exit(1);}}
struct Logger:INtlNaviLog{void Log(const char*fmt,...)override{char b[4096];va_list a;va_start(a,fmt);vsnprintf(b,sizeof(b),fmt,a);va_end(a);printf("NAVI %s\n",b);}};
struct Point{std::string name;sNAVI_POS pos;};
struct World{unsigned id;std::vector<Point> points;};
int main(int argc,char**argv){
    if(argc!=4)return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    setvbuf(stdout,NULL,_IONBF,0);
    FILE*f=fopen(argv[3],"rb");Need(f!=NULL,"read-only point selection");
    char magic[32];unsigned nw=0;Need(fscanf(f,"%31s %u",magic,&nw)==2&&strcmp(magic,"NAVIPOINTS1")==0&&nw>0&&nw<=8,"bounded point header");
    std::vector<World> worlds;std::set<unsigned> ids;unsigned total=0;
    for(unsigned i=0;i<nw;i++){
        World w;unsigned count=0;Need(fscanf(f,"%u %u",&w.id,&count)==2&&w.id<10000000&&count>0&&count<=32&&ids.insert(w.id).second,"unique bounded resource");
        std::set<std::string> labels;
        for(unsigned j=0;j<count;j++){
            char label[64];float x,y,z;Need(fscanf(f,"%63s %f %f %f",label,&x,&y,&z)==4,"point record");
            for(char*c=label;*c;c++)Need((*c>='a'&&*c<='z')||(*c>='A'&&*c<='Z')||(*c>='0'&&*c<='9')||*c=='_',"literal label");
            Need(labels.insert(label).second&&std::isfinite(x)&&std::isfinite(y)&&std::isfinite(z)&&fabs(x)<=16384&&fabs(z)<=16384&&fabs(y)<=2048,"finite bounded point");
            w.points.push_back({label,sNAVI_POS(x,y,z)});Need(++total<=64,"aggregate point bound");
        }
        worlds.push_back(w);
    }
    char extra[2];Need(fscanf(f,"%1s",extra)==EOF&&!ferror(f),"exact point EOF");fclose(f);
    // CNtlNaviPEDataImportMng treats this list as exclusions. Preserve its
    // production behavior while preventing an accidental whole-cache load.
    vecdef_WorldIDList excluded;WIN32_FIND_DATAA d;std::string pattern=std::string(argv[1])+"/*";
    HANDLE find=FindFirstFileA(pattern.c_str(),&d);Need(find!=INVALID_HANDLE_VALUE,"Navi cache root");
    do{if(!(d.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)||d.cFileName[0]=='.')continue;
       unsigned id=0;char tail=0;Need(sscanf(d.cFileName,"%u%c",&id,&tail)==1,"numeric resource directory");
       if(!ids.count(id))excluded.push_back(id);
    }while(FindNextFileA(find,&d));FindClose(find);
    Logger logger;INtlNavi*n=GetNaviEngine();Need(n->Create(&logger,argv[2],false),"Navigator create");
    Need(n->LoadPathEngineData(argv[1],excluded,ePATH_DATA_LOAD_FLAG_LOAD_ALL),"selected cache load");
    unsigned result=0;Need(n->WaitUntilLoadingFinish(60000,result),"complete selected loading");
    for(const auto&w:worlds){
        Need(n->IsPropDataLoaded(w.id)&&n->IsPathDataLoaded(w.id),"complete selected world properties/path");
        auto h=n->CreateInstanceHandler(w.id);Need(h!=NULL,"isolated selected instance");
        for(float radius:{.3f,.5f,1.f,2.f,3.f}){
            bool support=n->CanSupportAgent(h,radius);auto start=w.points.front().pos;
            bool foundStart=support&&n->FindNearestPos(h,radius,start);
            for(const auto&p:w.points){
                printf("PROPERTY resource=%u name=%s zone=%u attribute=%u\n",w.id,p.name.c_str(),n->GetZoneIndex(h,p.pos.x,p.pos.z),n->GetAttribute(h,p.pos.x,p.pos.z));
                auto actual=p.pos;bool found=support&&n->FindNearestPos(h,radius,actual);vecdef_NaviPosList route;
                bool path=found&&foundStart&&n->FindPath(h,radius,start,actual,route);
                printf("POINT resource=%u name=%s radius=%.1f supported=%d found=%d requested=%.6f,%.6f,%.6f actual=%.6f,%.6f,%.6f path=%d points=%zu deltaY=%.6f\n",
                    w.id,p.name.c_str(),radius,support,found,p.pos.x,p.pos.y,p.pos.z,actual.x,actual.y,actual.z,path,route.size(),fabs(actual.y-p.pos.y));
            }
        }
        n->DeleteInstanceHandler(h);
    }
    n->Delete();puts("AUDIT read-only observation complete; no export/install/restart; NOT gameplay acceptance");return 0;
}
