// Complete private Navigator export/reload for an explicit WORLD-space floor.
// This is a native helper of PlacedGroundNavigationService, never a deployer.
#include <Windows.h>
#include "NtlNavi.h"
#include "NtlNaviDataMng.h"
#include <cstdio>
#include <cstdlib>
#include <cstdarg>
#include <cmath>
#include <string>
#include <vector>
#include <set>
#include <algorithm>
#include <cstring>
static void Need(bool b,const char*s){if(!b){fprintf(stderr,"FAIL %s\n",s);exit(1);}}
static void Dir(const std::string&s){Need(GetFileAttributesA(s.c_str())==INVALID_FILE_ATTRIBUTES&&CreateDirectoryA(s.c_str(),NULL),"fresh native directory");}
static void Present(const std::string&s){WIN32_FILE_ATTRIBUTE_DATA d;Need(GetFileAttributesExA(s.c_str(),GetFileExInfoStandard,&d)&&!(d.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)&&!d.nFileSizeHigh&&d.nFileSizeLow,"complete native output");}
class Logger:public INtlNaviLog{public:unsigned errors=0;void Log(const char*fmt,...)override{char s[8192];va_list a;va_start(a,fmt);vsnprintf(s,sizeof(s),fmt,a);va_end(a);std::string l=s;std::transform(l.begin(),l.end(),l.begin(),[](unsigned char c){return char(tolower(c));});if(l.find("failed")!=l.npos||l.find("can not")!=l.npos||l.find("error")!=l.npos)++errors;printf("NAVI %s\n",s);}};
struct Probe{std::string name;sNAVI_POS pos;};
int main(int argc,char**argv){
    if(argc!=5)return 2; // sealed TOK, PE DLL, explicit settings, fresh output
    setvbuf(stdout,NULL,_IONBF,0);SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    FILE*f=fopen(argv[3],"rb");Need(f!=NULL,"explicit floor settings");
    char magic[32],folder[64];unsigned id,label,np,no;
    Need(fscanf(f,"%31s %u %u %63s %u %u",magic,&id,&label,folder,&np,&no)==6&&strcmp(magic,"FLOORWORLD1")==0&&
        id>=6000&&id<=999999&&label&&label!=0xFFFFFFFF&&strlen(folder)<=40&&np>=2&&np<=16&&no>=1&&no<=16,"bounded floor identity/probes");
    for(const char*c=folder;*c;c++)Need((*c>='a'&&*c<='z')||(*c>='0'&&*c<='9')||*c=='_',"literal floor identity");
    std::vector<Probe> points,outside;std::set<std::string> names;
    for(unsigned i=0;i<np+no;i++){char name[64];float x,y,z;
        Need(fscanf(f,"%63s %f %f %f",name,&x,&y,&z)==4&&names.insert(name).second,"unique explicit point");
        for(float v:{x,y,z})Need(std::isfinite(v)&&fabs(v)<1024,"finite bounded point");
        (i<np?points:outside).push_back({name,sNAVI_POS(x,y,z)});
    }
    char extra[2];Need(fscanf(f,"%1s",extra)==EOF&&!ferror(f),"exact settings EOF");fclose(f);
    const std::vector<float> radii{.3f,.5f,1.f,2.f,3.f};
    std::string root=argv[4],sid=std::to_string(id),model="ASSETS_FLOOR_"+sid;
    Dir(root);Dir(root+"\\tool");Dir(root+"\\tool\\ToolShareData");Dir(root+"\\tool\\ToolShareData\\NaviModel");Dir(root+"\\we");Dir(root+"\\we\\"+sid);Dir(root+"\\navi");
    Need(CopyFileA(argv[1],(root+"\\tool\\ToolShareData\\NaviModel\\"+model+"_PE_Nav_000.tok").c_str(),TRUE),"sealed selected ground");
    printf("PHASE private tool inputs\n");
    CNtlNaviDataMng data;Need(data.Create((root+"\\we\\"+sid).c_str()),"native tool data");
    auto*w=data.Create_WorldInDoor();w->SetWorldID(id);w->SetWorldName(folder);w->SetWorldMinPos(-1024,-1024);w->SetWorldMaxPos(1024,1024);w->SetBlockSize(64);
    for(float r:radii)w->AttachAgency(r);auto*g=data.Create_GroupInDoor();g->SetModelName(model.c_str());
    for(unsigned i=0;i<1024;i++){auto*p=data.Create_PropInDoor();p->SetBlockID(i);p->SetBlockProp(label);p->SetGameProp(0);}
    printf("PHASE flush1024 private properties\n");Need(data.Flush(),"native tool serialization");CNtlNaviDataMng back;
    Need(back.Create((root+"\\we\\"+sid).c_str())&&back.Load_WorldInDoor()&&back.Load_GroupInDoor(),"native tool readback");
    for(unsigned i=0;i<1024;i++){auto*p=back.Load_PropInDoor(i);Need(p&&p->GetBlockProp()==label&&p->GetGameProp()==0,"all native private block properties");}
    printf("PHASE native tool readback complete\n");
    Need(SetCurrentDirectoryA(root.c_str()),"private exporter cwd");Logger logger;auto*n=GetNaviEngine();Need(n->Create(&logger,argv[2],false),"Navigator create");
    printf("PHASE native Navi created, importing\n");Need(n->ImportWorldToolData((root+"\\we").c_str()),"native import");vecdef_WorldIDList ids;n->GetListImportedWorldIDList(ids);Need(ids.size()==1&&ids[0]==id,"exact private world");
    printf("PHASE native export/update\n");Need(n->ExportPathEngineData((root+"\\navi").c_str())&&n->UpdateToolData()&&!logger.errors,"complete native export");
    std::string out=root+"\\navi\\"+sid;Present(out+"\\worldinfo.dat");Present(out+"\\pathengine\\0.mf");
    for(float r:radii){std::string base=out+"\\pathengine\\0_"+std::to_string(unsigned(std::lround(r*100)));Present(base+".cf");Present(base+".pf");}
    for(unsigned i=0;i<1024;i++){char name[100];NAVI_PROINFO_INDOOR_NAME(name,i);Present(out+"\\property\\"+name);}
    printf("PHASE exported, fresh production reload\n");n->Delete();Need(n->Create(&logger,argv[2],false),"fresh production Navigator");vecdef_WorldIDList excluded;
    Need(n->LoadPathEngineData((root+"\\navi").c_str(),excluded,ePATH_DATA_LOAD_FLAG_LOAD_ALL),"production reload");unsigned load=0;
    Need(n->WaitUntilLoadingFinish(60000,load)&&n->IsPropDataLoaded(id)&&n->IsPathDataLoaded(id)&&!logger.errors,"complete production load");
    auto h=n->CreateInstanceHandler(id);Need(h!=NULL,"private native instance");
    for(float radius:radii){
        Need(n->CanSupportAgent(h,radius),"Taiwan agent support");std::vector<sNAVI_POS> resolved;
        for(const auto&p:points){auto actual=p.pos;
            bool found=n->FindNearestPos(h,radius,actual);
            printf("FLOOR radius=%.1f name=%s found=%d requested=%.6f,%.6f,%.6f actual=%.6f,%.6f,%.6f\n",radius,p.name.c_str(),found,p.pos.x,p.pos.y,p.pos.z,actual.x,actual.y,actual.z);
            Need(found&&fabs(actual.x-p.pos.x)<.02f&&fabs(actual.y-p.pos.y)<.02f&&fabs(actual.z-p.pos.z)<.02f,"exact intended floor for every actor");resolved.push_back(actual);
        }
        for(unsigned i=0;i<resolved.size();i++)for(unsigned j=i+1;j<resolved.size();j++){
            vecdef_NaviPosList forward,reverse;
            Need(n->FindPath(h,radius,resolved[i],resolved[j],forward)&&!forward.empty()&&n->FindPath(h,radius,resolved[j],resolved[i],reverse)&&!reverse.empty(),"all actor pairs reachable both directions");
            for(const auto&route:{forward,reverse})for(const auto&p:route)Need(std::isfinite(p.x)&&std::isfinite(p.y)&&std::isfinite(p.z),"finite native path");
            printf("ROUTE radius=%.1f from=%s to=%s forward=%zu reverse=%zu\n",radius,points[i].name.c_str(),points[j].name.c_str(),forward.size(),reverse.size());
        }
        for(const auto&p:outside){auto actual=p.pos;bool found=n->FindNearestPos(h,radius,actual);vecdef_NaviPosList route;
            auto destination=p.pos;bool path=n->FindPath(h,radius,resolved[0],destination,route);
            Need(!path&&(!found||fabs(actual.x-p.pos.x)>1.f||fabs(actual.z-p.pos.z)>1.f),"outside destination has no floor or route");
            sNAVI_POS hit;destination=p.pos;auto first=n->FirstCollisionTest(h,radius,resolved[0],destination,hit);
            Need(first==eCOL_TEST_RESULT_COL&&std::isfinite(hit.x)&&std::isfinite(hit.y)&&std::isfinite(hit.z),"native boundary first collision");
            printf("OUTSIDE radius=%.1f name=%s projected=%d route=%d firstCollision=%d hit=%.6f,%.6f,%.6f\n",radius,p.name.c_str(),found,path,int(first),hit.x,hit.y,hit.z);
        }
    }
    Need(!logger.errors,"no native export/query errors");n->DeleteInstanceHandler(h);n->Delete();
    printf("PASS placed floor production Navi resource%u agents5 all-probes-bidirectional=true outside-rejected=true; explicit floor only, NOT client collision or gameplay\n",id);return 0;
}
