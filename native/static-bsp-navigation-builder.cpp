// Native indoor tool export and production Navigator reload for the isolated
// Snake Way candidate. Ground is a sealed content chunk, not decorative sky.
#include <Windows.h>
#include "NtlNavi.h"
#include "NtlNaviDataMng.h"
#include <cstdio>
#include <cstdlib>
#include <cstdarg>
#include <cmath>
#include <string>
#include <vector>
#include <algorithm>
static void Need(bool b,const char*s){if(!b){fprintf(stderr,"FAIL %s\n",s);exit(1);}}
static void Dir(const std::string&s){Need(GetFileAttributesA(s.c_str())==INVALID_FILE_ATTRIBUTES&&CreateDirectoryA(s.c_str(),NULL),"fresh native directory");}
static void Present(const std::string&s){WIN32_FILE_ATTRIBUTE_DATA d;Need(GetFileAttributesExA(s.c_str(),GetFileExInfoStandard,&d)&&!(d.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)&&!d.nFileSizeHigh&&d.nFileSizeLow,"complete native output");}
class NaviLogger:public INtlNaviLog{public:unsigned errors=0;void Log(const char*fmt,...)override{char s[8192];va_list a;va_start(a,fmt);vsnprintf(s,sizeof(s),fmt,a);va_end(a);std::string l=s;std::transform(l.begin(),l.end(),l.begin(),[](unsigned char c){return char(tolower(c));});if(l.find("failed")!=l.npos||l.find("can not")!=l.npos||l.find("error")!=l.npos)++errors;printf("NAVI %s\n",s);}};
int main(int argc,char**argv){
    if(argc!=5&&argc!=6&&argc!=8)return 2; // private TOK, new-world WFP, private PE DLL, fresh root
    const bool envelope=argc>=6;Need(!envelope||strcmp(argv[5],"--upper-envelope")==0,"explicit envelope profile");
    float terminalHeight=49.564596f;
    if(argc==8){char* tail=nullptr;Need(strcmp(argv[6],"--visible-terminal-height")==0,"explicit physical ramp height");terminalHeight=strtof(argv[7],&tail);Need(tail&&!*tail&&std::isfinite(terminalHeight)&&terminalHeight>=49.56f&&terminalHeight<=51.f,"bounded sealed visible terminal floor");}
    printf("EXPECTED_TERMINAL_FLOOR %.6f; canonical source/visible ramp height, unchanged2cm floor tolerance\n",terminalHeight);
    setvbuf(stdout,NULL,_IONBF,0);SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    const unsigned id=6009;std::string root=argv[4];Dir(root);Dir(root+"\\tool");Dir(root+"\\tool\\ToolShareData");Dir(root+"\\tool\\ToolShareData\\NaviModel");Dir(root+"\\we");Dir(root+"\\we\\6009");Dir(root+"\\navi");
    Need(CopyFileA(argv[1],(root+"\\tool\\ToolShareData\\NaviModel\\ASSETS_SNAKEWAY_PE_Nav_000.tok").c_str(),TRUE),"sealed native ground chunk");
    CNtlNaviDataMng data;Need(data.Create((root+"\\we\\6009").c_str()),"native tool data");auto*w=data.Create_WorldInDoor();w->SetWorldID(id);w->SetWorldName("dbo_assets_snakeway_001");w->SetWorldMinPos(-1024,-1024);w->SetWorldMaxPos(1024,1024);w->SetBlockSize(64);
    const std::vector<float> radii{.3f,.5f,1.f,2.f,3.f};for(float r:radii)w->AttachAgency(r);auto*g=data.Create_GroupInDoor();g->SetModelName("ASSETS_SNAKEWAY");
    FILE*f=fopen(argv[2],"rb");Need(f!=NULL,"native wrapper block properties");fseek(f,0,SEEK_END);Need(ftell(f)==443392,"WFP1024 blocks");rewind(f);
    for(unsigned i=0;i<1024;i++){unsigned char block[433];Need(fread(block,1,433,f)==433&&strcmp((char*)block,"600090010")==0,"new-world label");unsigned game;memcpy(&game,block+429,4);auto*p=data.Create_PropInDoor();p->SetBlockID(i);p->SetBlockProp(600090010);p->SetGameProp(game);}
    Need(fgetc(f)==EOF&&!ferror(f),"WFP EOF");fclose(f);Need(data.Flush(),"native tool serialization");CNtlNaviDataMng back;Need(back.Create((root+"\\we\\6009").c_str())&&back.Load_WorldInDoor()&&back.Load_GroupInDoor(),"native indoor tool readback");for(unsigned i=0;i<1024;i++){auto*p=back.Load_PropInDoor(i);Need(p&&p->GetBlockProp()==600090010,"native all block property readback");}
    Need(SetCurrentDirectoryA(root.c_str()),"private exporter cwd");NaviLogger logger;auto*n=GetNaviEngine();Need(n->Create(&logger,argv[3],false),"native Navi Create");Need(n->ImportWorldToolData((root+"\\we").c_str()),"native indoor import");vecdef_WorldIDList ids;n->GetListImportedWorldIDList(ids);Need(ids.size()==1&&ids[0]==id,"only new resource6009");
    Need(n->ExportPathEngineData((root+"\\navi").c_str())&&n->UpdateToolData()&&!logger.errors,"native indoor full export");std::string out=root+"\\navi\\6009";Present(out+"\\worldinfo.dat");Present(out+"\\pathengine\\0.mf");for(float r:radii){std::string p=out+"\\pathengine\\0_"+std::to_string(unsigned(std::lround(r*100)));Present(p+".cf");Present(p+".pf");}for(unsigned i=0;i<1024;i++){char name[100];NAVI_PROINFO_INDOOR_NAME(name,i);Present(out+"\\property\\"+name);}
    n->Delete();Need(n->Create(&logger,argv[3],false),"fresh production reader");vecdef_WorldIDList excluded;Need(n->LoadPathEngineData((root+"\\navi").c_str(),excluded,ePATH_DATA_LOAD_FLAG_LOAD_ALL)&&n->IsPropDataLoaded(id)&&n->IsPathDataLoaded(id)&&!logger.errors,"production full indoor reload");auto h=n->CreateInstanceHandler(id);Need(h!=NULL,"new world instance");
    for(float r:radii){Need(n->CanSupportAgent(h,r),"Taiwan agent support");sNAVI_POS a=envelope?sNAVI_POS(20.9693508f,49.7398071f,-83.2736893f):sNAVI_POS(20.9693508f,49.7398071f,-83.2736893f),b=envelope?sNAVI_POS(-146.f,49.564596f,40.f):sNAVI_POS(-142.711044f,49.353821f,36.104557f);auto beforeA=a,beforeB=b;bool fa=n->FindNearestPos(h,r,a),fb=n->FindNearestPos(h,r,b);printf("FLOOR radius=%.1f a=%d %.6f,%.6f,%.6f b=%d %.6f,%.6f,%.6f\n",r,fa,a.x,a.y,a.z,fb,b.x,b.y,b.z);if(r>1.f){printf("LARGE_AGENT radius=%.1f nearestA=%d nearestB=%d; narrow path acceptance not required\n",r,fa,fb);continue;}Need(fa&&fb&&std::abs(a.y-beforeA.y)<.1f&&std::abs(b.y-beforeB.y)<.5f&&std::abs(a.x-beforeA.x)<.5f&&std::abs(a.z-beforeA.z)<.5f&&std::abs(b.x-beforeB.x)<=r&&std::abs(b.z-beforeB.z)<=r,"Snake Way intended upper floor");vecdef_NaviPosList forward,reverse;Need(n->FindPath(h,r,a,b,forward)&&!forward.empty()&&n->FindPath(h,r,b,a,reverse)&&!reverse.empty(),"Snake Way full body bidirectional route");printf("AGENT radius=%.1f route=%zu reverse=%zu from=%.6f,%.6f,%.6f to=%.6f,%.6f,%.6f\n",r,forward.size(),reverse.size(),a.x,a.y,a.z,b.x,b.y,b.z);}
    sNAVI_POS sky(250,40,250);auto requested=sky;bool found=n->FindNearestPos(h,.5f,sky);Need(!found||std::abs(sky.x-requested.x)>.5f||std::abs(sky.z-requested.z)>.5f,"clouds excluded from walkable mesh");
    if(envelope){
        for(float r:{.3f,.5f,1.f}){
            sNAVI_POS entry(20.9693508f,49.7398071f,-83.2736893f),end(-146.f,terminalHeight,40.f),a(-65.f,42.005f,-96.f),b(-70.f,42.005f,-96.f);
            Need(n->FindNearestPos(h,r,entry)&&n->FindNearestPos(h,r,end)&&n->FindNearestPos(h,r,a)&&n->FindNearestPos(h,r,b),"envelope intended floors resolve");
            Need(fabs(end.y-terminalHeight)<.02f&&fabs(end.x+146)<.02f&&fabs(end.z-40)<.02f&&fabs(a.y-42.005f)<.02f&&fabs(b.y-42.005f)<.02f,"terminal and head deck intended physical floors");
            vecdef_NaviPosList forward,reverse,deck;Need(n->FindPath(h,r,entry,end,forward)&&!forward.empty()&&n->FindPath(h,r,end,entry,reverse)&&!reverse.empty(),"terminal platform connected both directions");Need(n->FindPath(h,r,a,b,deck)&&!deck.empty(),"head deck local traversal");
            printf("ENVELOPE radius=%.1f terminal-forward=%zu terminal-reverse=%zu head-deck-local=%zu; head/body altitude gap NOT a walk bridge\n",r,forward.size(),reverse.size(),deck.size());
        }
    }
    printf("PASS native Snake Way indoor export/reload resource6009 blocks1024 agents5 humanoid-routes3 bidirectional-body-route clouds-excluded; NOT client rendering or gameplay acceptance\n");n->DeleteInstanceHandler(h);n->Delete();return 0;
}
