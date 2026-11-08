// Original arena: native tool data/export and the production Navigator reader.
// Resource6010 is provisional within this private candidate; no runtime writes.
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
#include <cstring>
#include <climits>
static void Need(bool b,const char*s){if(!b){fprintf(stderr,"FAIL %s\n",s);exit(1);}}
static void Dir(const std::string&s){Need(GetFileAttributesA(s.c_str())==INVALID_FILE_ATTRIBUTES&&CreateDirectoryA(s.c_str(),NULL),"fresh native directory");}
class MapLogger:public INtlNaviLog{public:unsigned errors=0;void Log(const char*fmt,...)override{char s[8192];va_list a;va_start(a,fmt);vsnprintf(s,sizeof(s),fmt,a);va_end(a);std::string l=s;std::transform(l.begin(),l.end(),l.begin(),[](unsigned char c){return char(tolower(c));});if(l.find("failed")!=l.npos||l.find("can not")!=l.npos||l.find("error")!=l.npos)++errors;printf("NAVI %s\n",s);}};
int main(int argc,char**argv){
    if(argc!=9&&argc!=10)return 2; // Optional sealed world folder; old private profile remains supported.
    setvbuf(stdout,NULL,_IONBF,0);SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    float radius=float(atof(argv[4])),height=float(atof(argv[5])),pillar=float(atof(argv[6]));unsigned id=unsigned(strtoul(argv[7],NULL,10)),label=unsigned(strtoul(argv[8],NULL,10));
    std::string folder=argc==10?argv[9]:"dbo_assets_power_arena_001", worldId=std::to_string(id);
    Need(folder.size()<=32&&folder.find("dbo_assets_")==0&&folder.find_first_not_of("abcdefghijklmnopqrstuvwxyz0123456789_")==folder.npos&&id&&id!=UINT_MAX&&label&&label!=UINT_MAX,"bounded authored map identity");
    Need((argc==10||(id==6010&&label==600100010))&&std::isfinite(radius)&&radius>=25&&radius<=150&&std::isfinite(height)&&fabs(height)<=100&&std::isfinite(pillar)&&pillar>=1&&pillar<=5,"bounded private arena profile");
    std::string root=argv[3],model="ASSETS_MAP_"+worldId;Dir(root);Dir(root+"\\tool");Dir(root+"\\tool\\ToolShareData");Dir(root+"\\tool\\ToolShareData\\NaviModel");Dir(root+"\\we");Dir(root+"\\we\\"+worldId);Dir(root+"\\navi");
    Need(CopyFileA(argv[1],(root+"\\tool\\ToolShareData\\NaviModel\\"+model+"_PE_Nav_000.tok").c_str(),TRUE),"sealed original arena ground");
    CNtlNaviDataMng data;Need(data.Create((root+"\\we\\"+worldId).c_str()),"native new-world tool data");auto*w=data.Create_WorldInDoor();w->SetWorldID(id);w->SetWorldName(folder.c_str());w->SetWorldMinPos(-1024,-1024);w->SetWorldMaxPos(1024,1024);w->SetBlockSize(64);
    const std::vector<float> radii{.3f,.5f,1.f,2.f,3.f};for(float r:radii)w->AttachAgency(r);auto*g=data.Create_GroupInDoor();g->SetModelName(model.c_str());
    for(unsigned i=0;i<1024;i++){auto*p=data.Create_PropInDoor();p->SetBlockID(i);p->SetBlockProp(label);p->SetGameProp(0);}
    Need(data.Flush(),"native tool serialization");CNtlNaviDataMng back;Need(back.Create((root+"\\we\\"+worldId).c_str())&&back.Load_WorldInDoor()&&back.Load_GroupInDoor(),"native indoor tool readback");for(unsigned i=0;i<1024;i++){auto*p=back.Load_PropInDoor(i);Need(p&&p->GetBlockProp()==label&&p->GetGameProp()==0,"native all new-world block defaults readback");}
    Need(SetCurrentDirectoryA(root.c_str()),"isolated exporter cwd");MapLogger logger;auto*n=GetNaviEngine();Need(n->Create(&logger,argv[2],false),"native Navi Create");Need(n->ImportWorldToolData((root+"\\we").c_str()),"native indoor import");vecdef_WorldIDList ids;n->GetListImportedWorldIDList(ids);Need(ids.size()==1&&ids[0]==id,"only sealed new-world resource");
    Need(n->ExportPathEngineData((root+"\\navi").c_str())&&n->UpdateToolData()&&!logger.errors,"native complete export");n->Delete();Need(n->Create(&logger,argv[2],false),"fresh production Navigator");vecdef_WorldIDList excluded;
    Need(n->LoadPathEngineData((root+"\\navi").c_str(),excluded,ePATH_DATA_LOAD_FLAG_LOAD_ALL)&&n->IsPropDataLoaded(id)&&n->IsPathDataLoaded(id)&&!logger.errors,"production full indoor reload");auto h=n->CreateInstanceHandler(id);Need(h!=NULL,"new world instance");
    FILE* routes=fopen((root+"\\routes.tsv").c_str(),"wbx");Need(routes!=NULL,"fresh routes evidence");fprintf(routes,"radius\tdirection\tpoint\tx\ty\tz\n");
    for(float r:radii){
        Need(n->CanSupportAgent(h,r),"Taiwan agent radius support");sNAVI_POS a(-radius*.55f,height,0),b(radius*.55f,height,0);auto oa=a,ob=b;
        const float angle=3.14159265358979323846f*(1.17f+1.f/3.f);sNAVI_POS spectator((radius+9)*cosf(angle),height+8,(radius+9)*sinf(angle));auto expected=spectator;
        Need(n->FindNearestPos(h,r,spectator)&&fabs(spectator.x-expected.x)<.05&&fabs(spectator.z-expected.z)<.05&&fabs(spectator.y-expected.y)<.02,"spectator balcony intended native floor for each radius");
        vecdef_NaviPosList toStands;Need(!n->FindPath(h,r,a,spectator,toStands),"combat deck cannot walk onto spectator island");
        // Each authored seating tread must resolve on its actual floor across
        // the complete sector for every Taiwan actor radius. Keep inter-tier
        // risers absent from NCP; a path between neighboring floors would turn
        // the empty vertical gap into an unintended ramp.
        const float standStart=3.14159265358979323846f*1.17f;
        for(unsigned step=0;step<5;++step){
            const float quantizedMargin=.01f,edgeMargin=.04f;
            const float clearance=r+quantizedMargin;
            const float rawInner=radius+14+step*3,rawOuter=radius+17+step*3;
            const float usableWidth=(rawOuter-rawInner)-2*(edgeMargin+clearance);
            if(r>1.f){
                Need(usableWidth<=0,"large agent is explicitly outside the three-metre tread capability");
                printf("TIER radius=%.1f supported=false usable-width=%.3f reason=actor-clearance-exceeds-tread\n",r,usableWidth);
                continue;
            }
            Need(usableWidth>0,"supported agent has positive tread clearance");
            const float inner=rawInner+edgeMargin+clearance,outer=rawOuter-edgeMargin-clearance,floor=height+8+step*2;
            const float sectorSweep=2.f*3.14159265358979323846f/3.f;
            const float angleStart=standStart+asinf(clearance/rawInner);
            const float angleEnd=standStart+sectorSweep-asinf(clearance/rawOuter);
            Need(angleEnd>angleStart,"supported agent has positive angular tread clearance");
            for(unsigned angular=0;angular<=192;++angular){
                const float angle=angleStart+(angleEnd-angleStart)*(float(angular)/192.f);
                for(float fraction:{.1f,.5f,.9f}){
                    const float walkRadius=inner+(outer-inner)*fraction;
                    sNAVI_POS tread(walkRadius*cosf(angle),floor,walkRadius*sinf(angle));auto intended=tread;
                    Need(n->FindNearestPos(h,r,tread)&&fabs(tread.x-intended.x)<.05&&fabs(tread.z-intended.z)<.05&&fabs(tread.y-floor)<.02,"every seating tier surface has intended native floor for each radius");
                }
            }
            const float middle=(inner+outer)*.5f;
            sNAVI_POS tierA(middle*cosf((angleStart+angleEnd)*.5f-(angleEnd-angleStart)*.25f),floor,middle*sinf((angleStart+angleEnd)*.5f-(angleEnd-angleStart)*.25f));
            sNAVI_POS tierB(middle*cosf((angleStart+angleEnd)*.5f+(angleEnd-angleStart)*.25f),floor,middle*sinf((angleStart+angleEnd)*.5f+(angleEnd-angleStart)*.25f));
            vecdef_NaviPosList across,backAcross;
            Need(n->FindPath(h,r,tierA,tierB,across)&&!across.empty()&&n->FindPath(h,r,tierB,tierA,backAcross)&&!backAcross.empty(),"each tier has a bidirectional walk surface");
            if(step<4){
                const float nextFloor=floor+2,nextMiddle=middle+3,nextAngle=(angleStart+angleEnd)*.5f;
                sNAVI_POS next(nextMiddle*cosf(nextAngle),nextFloor,nextMiddle*sinf(nextAngle));
                vecdef_NaviPosList up,down;
                Need(!n->FindPath(h,r,tierA,next,up)&&!n->FindPath(h,r,next,tierA,down),"two-metre risers remain disconnected in NCP");
            }
        }
        Need(n->FindNearestPos(h,r,a)&&n->FindNearestPos(h,r,b)&&fabs(a.x-oa.x)<.02&&fabs(a.y-height)<.02&&fabs(a.z)<.02&&fabs(b.x-ob.x)<.02&&fabs(b.y-height)<.02&&fabs(b.z)<.02,"intended entry/opponent physical deck");
        // Reproduce the versioned PowerArena profile's two rings, 32 starts each.
        // Prove every slot on the real production Navigator, in both directions.
        for(float fraction:{.6875f,.4375f})for(unsigned slot=0;slot<32;++slot){
            const float angle=6.2831853071795864769f*slot/32;
            sNAVI_POS spawn(radius*fraction*cosf(angle),height,radius*fraction*sinf(angle));auto intended=spawn;
            Need(n->FindNearestPos(h,r,spawn)&&fabs(spawn.x-intended.x)<.05&&fabs(spawn.y-height)<.02&&fabs(spawn.z-intended.z)<.05,"every configured ring spawn has intended native floor");
            vecdef_NaviPosList there,back;Need(n->FindPath(h,r,a,spawn,there)&&!there.empty()&&n->FindPath(h,r,spawn,a,back)&&!back.empty(),"all64 ring spawns bidirectionally reachable");
        }
        printf("SPAWNS radius=%.1f ring-layout64-certified=true\n",r);
        auto direct=n->CollisionTest(h,r,a,b);Need(direct==eCOL_TEST_RESULT_COL,"straight route must collide with pillar footprint");
        vecdef_NaviPosList forward,reverse;Need(n->FindPath(h,r,a,b,forward)&&!forward.empty()&&n->FindPath(h,r,b,a,reverse)&&!reverse.empty(),"bidirectional route around pillar");
        for(int direction=0;direction<2;direction++){auto&route=direction?reverse:forward;for(size_t i=0;i<route.size();i++){auto p=route[i];Need(fabs(p.y-height)<.02&&std::sqrt(p.x*p.x+p.z*p.z)>=pillar-.03&&std::sqrt(p.x*p.x+p.z*p.z)<=radius+.03,"path points remain on deck outside pillar");fprintf(routes,"%.1f\t%d\t%zu\t%.9g\t%.9g\t%.9g\n",r,direction,i,p.x,p.y,p.z);}}
        sNAVI_POS edge(radius-8,height,0),outside(radius+20,height,0),hit;Need(n->FindNearestPos(h,r,edge),"valid near-edge deck");
        auto firstEdgeResult=n->FirstCollisionTest(h,r,edge,outside,hit);
        Need(firstEdgeResult==eCOL_TEST_RESULT_COL&&std::isfinite(hit.x)&&std::isfinite(hit.y)&&std::isfinite(hit.z)&&fabs(hit.y-height)<.02,"initialized native first-collision floor at arena edge");
        auto edgeResult=n->CollisionTest(h,r,edge,outside);vecdef_NaviPosList beyond;bool outsideRoute=n->FindPath(h,r,edge,outside,beyond);
        auto projected=outside;bool hasOutside=n->FindNearestPos(h,r,projected);
        printf("EDGE radius=%.1f collision-result=%d first-collision-result=%d outside-route=%d projected=%d %.3f,%.3f,%.3f; first-collision-certified=true\n",r,int(edgeResult),int(firstEdgeResult),outsideRoute,hasOutside,projected.x,projected.y,projected.z);
        Need((edgeResult==eCOL_TEST_RESULT_COL||edgeResult==eCOL_TEST_RESULT_INVALID_DEST_POS)&&!outsideRoute&&(!hasOutside||fabs(projected.x-outside.x)>10),"outside deck has no walk surface or route");
        sNAVI_POS hole(0,height,0);bool center=n->FindNearestPos(h,r,hole);Need(!center||std::sqrt(hole.x*hole.x+hole.z*hole.z)>=pillar-.02,"center cannot resolve inside pillar");
        printf("AGENT radius=%.1f route=%zu reverse=%zu pillar-blocked=%d outside-destination-rejected=true center-excluded=true height=%.3f\n",r,forward.size(),reverse.size(),int(direct),height);
    }
    Need(fclose(routes)==0&&!logger.errors,"complete route evidence");n->DeleteInstanceHandler(h);n->Delete();
    printf("PASS original arena production Navi resource%u blocks1024 agents5 bidirectional-pillar-routes outside-destination-rejected center-excluded spectator-balcony-isolated; NOT client rendering or gameplay\n",id);return 0;
}
