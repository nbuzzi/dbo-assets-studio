// Enumerate serialized boundaries and dependencies using the real Taiwan reader.
#include "precomp_ntlpresentation.h"
#include "NtlResourceEffect.h"
#include "NtlEffectSplinePath.h"
#include "NtlResourceParticleSystem.h"
#include "NtlResourceMeshSystem.h"
#include "NtlResourceImVertexSystem.h"
#include "NtlResourcePostEffectSystem.h"
#include "NtlResourceBeamSystem.h"
#include "NtlWorldCommon.h"
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <set>
#include <sstream>

static void Require(bool ok,const char* what){if(!ok){fprintf(stderr,"FAIL %s offset reader\n",what);exit(1);}}
static int Integer(FILE* f){int n;Require(fread(&n,4,1,f)==1,"integer");return n;}
static std::string Name(FILE* f){int n=Integer(f);Require(n>0&&n<256,"name bound");char name[256]={};Require(fread(name,n,1,f)==1,"name");return name;}
static void Dependency(const std::string& effect,const char* kind,const std::string& value){if(!value.empty())printf("D\t%s\t%s\t%s\n",effect.c_str(),kind,value.c_str());}
static const char* Field(const std::string& value){
    Require(value.find_first_of("\t\r\n") == std::string::npos && value.find('\0') == std::string::npos,"bounded TSV field");return value.c_str();
}
static void SystemMetadata(const std::string& effectName,unsigned index,CNtlResourceComponentSystem* system){
    auto mesh=dynamic_cast<CNtlResourceMeshSystem*>(system);
    auto particle=dynamic_cast<CNtlResourceParticleSystem*>(system);
    auto beam=dynamic_cast<CNtlResourceBeamSystem*>(system);
    std::string fadeFollow;
    const bool hasFade=system->IsEmitterDataFlag(rpPRTSTDEMITTERDATAFLAGPRTFADEOUT)!=FALSE;
    if(hasFade)fadeFollow=system->m_EmitterPrtFadeOut.strFollowSystem;
    const bool fadeEnabled=hasFade&&system->m_EmitterPrtFadeOut.follow!=FALSE;
    const std::string meshName=mesh?mesh->m_strMeshFileName:"";
    const std::string animation=mesh?mesh->m_strAniFileName:"";
    const std::string uv=mesh?mesh->m_strUvFileName:"";
    const std::string subsystem=particle?particle->m_strSubSystem:"";
    const std::string beamHead=beam?beam->m_EmitterStandard.strHeadSystem:"";
    printf("Y\t%s\t%u\t%d\t%s\t%d\t%u\t%u\t%d\t%.9g\t%.9g\t%d\t%d\t%s\t%s\t%s\t%s\t%s\t%s\n",
        Field(effectName),index,system->GetSystemType(),Field(system->m_strName),system->m_bSubSystem?1:0,
        (unsigned)system->m_nEmitterDataFlag,(unsigned)system->m_nPathEmitterDataFlag,system->m_bEternity?1:0,
        (double)system->m_fStartTime,(double)system->m_fLifeTime,hasFade?1:0,fadeEnabled?1:0,Field(fadeFollow),Field(meshName),
        Field(animation),Field(uv),Field(subsystem),Field(beamHead));
}
#ifdef CONTENT_EFFECT_RESOURCE_FIELDS
#include "effect-string-trace.h"
#endif
static bool FiniteMatrix(const RwMatrix& matrix){
    for(const RwV3d* v:{&matrix.right,&matrix.up,&matrix.at,&matrix.pos})
        if(!std::isfinite(v->x)||!std::isfinite(v->y)||!std::isfinite(v->z))return false;
    return true;
}
static void SplineObserve(CNtlEffectSplinePath& spline,const std::string& name){
    int duplicates=0,decreases=0,nonfinite=0,zeroQuat=0;
    for(int i=0;i<spline.m_Info.nControlCount;++i){auto& c=spline.m_pControlPoint[i];
        for(float v:{c.pos.x,c.pos.y,c.pos.z,c.rotate.x,c.rotate.y,c.rotate.z,c.fTime})if(!std::isfinite(v))++nonfinite;
    }
    for(int i=0;i<spline.m_nTotalCurveCount;++i){auto& c=spline.m_pCurveData[i];
        for(float v:{c.pos.x,c.pos.y,c.pos.z,c.quat.imag.x,c.quat.imag.y,c.quat.imag.z,c.quat.real,c.fTime})if(!std::isfinite(v))++nonfinite;
        if(i>0){if(c.fTime==spline.m_pCurveData[i-1].fTime)++duplicates;if(c.fTime<spline.m_pCurveData[i-1].fTime)++decreases;}
        const float q=c.quat.imag.x*c.quat.imag.x+c.quat.imag.y*c.quat.imag.y+c.quat.imag.z*c.quat.imag.z+c.quat.real*c.quat.real;
        if(!std::isfinite(q)||q<=0)++zeroQuat;
    }
    printf("O\t%s\t%d\t%d\t%.9g\t%.9g\t%d\t%d\t%d\t%d\n",Field(name),spline.m_Info.nControlCount,spline.m_nTotalCurveCount,
        spline.m_pCurveData[0].fTime,spline.m_pCurveData[spline.m_nTotalCurveCount-1].fTime,duplicates,decreases,nonfinite,zeroQuat);
}
static void SplinePlayback(CNtlEffectSplinePath& spline,const std::string& name,bool observedOrder=false){
    Require(spline.m_nTotalCurveCount>1&&spline.m_Info.nControlCount>=4,"playback spline count");
    Require(!observedOrder||spline.m_nTotalCurveCount<=2048,"bounded observed-order spline samples");
    for(int i=0;i<spline.m_Info.nControlCount;++i){auto& c=spline.m_pControlPoint[i];
        for(float v:{c.pos.x,c.pos.y,c.pos.z,c.rotate.x,c.rotate.y,c.rotate.z,c.fTime})Require(std::isfinite(v),"finite spline control");
    }
    for(int i=0;i<spline.m_nTotalCurveCount;++i){auto& c=spline.m_pCurveData[i];
        for(float v:{c.pos.x,c.pos.y,c.pos.z,c.quat.imag.x,c.quat.imag.y,c.quat.imag.z,c.quat.real,c.fTime})Require(std::isfinite(v),"finite spline curve");
        // Production FrameAnimation holds the first pose before its first key.
        // A positive first time is a legitimate delay, not a missing zero key.
        Require(c.fTime>=0&&(observedOrder||i==0||c.fTime>spline.m_pCurveData[i-1].fTime),"strict spline curve timeline");
        const float q=c.quat.imag.x*c.quat.imag.x+c.quat.imag.y*c.quat.imag.y+c.quat.imag.z*c.quat.imag.z+c.quat.real*c.quat.real;
        Require(std::isfinite(q)&&q>0,"nonzero spline rotation");
    }
    const float duration=spline.m_pCurveData[spline.m_nTotalCurveCount-1].fTime;
    Require(duration>0&&spline.m_pCurveData[0].fTime<=duration,"positive spline duration and first key bound");int samples=0;bool changed=false;
    if(spline.m_pCurveData[0].fTime>0){
        for(int loop=0;loop<2;++loop)for(int rotate=0;rotate<2;++rotate){RwMatrix first={},held={};
            spline.FrameAnimation(0,first,loop,rotate,1.f);
            spline.FrameAnimation(spline.m_pCurveData[0].fTime*.75f,held,loop,rotate,1.f);
            Require(FiniteMatrix(first)&&FiniteMatrix(held),"finite delayed first spline pose");
            for(const auto& pair:{std::make_pair(&first.right,&held.right),std::make_pair(&first.up,&held.up),
                std::make_pair(&first.at,&held.at),std::make_pair(&first.pos,&held.pos)})
                Require(std::fabs(pair.first->x-pair.second->x)<=1e-6&&std::fabs(pair.first->y-pair.second->y)<=1e-6&&
                    std::fabs(pair.first->z-pair.second->z)<=1e-6,"delayed spline holds native first pose");
        }
    }
    std::vector<float> frames;for(int step=0;step<=40;++step)frames.push_back(duration*step/16.f);
    if(observedOrder){
        std::set<float> exact(frames.begin(),frames.end());
        for(int i=0;i<spline.m_nTotalCurveCount;++i){const float time=spline.m_pCurveData[i].fTime;
            for(float sample:{std::nextafter(time,-INFINITY),time,std::nextafter(time,INFINITY)})
                if(std::isfinite(sample)&&sample>=0)exact.insert(sample);
        }
        frames.assign(exact.begin(),exact.end());
    }
    for(int loop=0;loop<2;++loop)for(int rotate=0;rotate<2;++rotate){RwMatrix initial={};int step=0;
        for(float frame:frames){RwMatrix matrix={};
            spline.FrameAnimation(frame,matrix,loop,rotate,1.f);Require(FiniteMatrix(matrix),"finite native spline playback");
            if(step==0)initial=matrix;
            else for(const auto& pair:{std::make_pair(&matrix.right,&initial.right),std::make_pair(&matrix.up,&initial.up),
                std::make_pair(&matrix.at,&initial.at),std::make_pair(&matrix.pos,&initial.pos)})
                if(std::fabs(pair.first->x-pair.second->x)>1e-6||std::fabs(pair.first->y-pair.second->y)>1e-6||std::fabs(pair.first->z-pair.second->z)>1e-6)changed=true;
            ++samples;
            ++step;
        }
    }
    printf("V\t%s\t%d\t%d\t%.9g\t%d\t%d\n",Field(name),spline.m_Info.nControlCount,spline.m_nTotalCurveCount,duration,samples,changed?1:0);
}
int main(int argc,char**argv){
    if(argc==3&&std::string(argv[1])=="--gwp") {
        FILE* file=fopen(argv[2],"rb");Require(file!=NULL,"GWP input");fseek(file,0,SEEK_END);long size=ftell(file);rewind(file);
        char version[10]={};Require(fread(version,10,1,file)==1&&memchr(version,0,10)!=NULL,"GWP version");
        Require(strcmp(version,dNTL_WORLD_VERSION)==0||strcmp(version,dNTL_WORLD_VERSION_OLD)==0,"native world version");rewind(file);
        sNtlWorldParam params={};LoadFileWorldState(&params,file);Require(ftell(file)==size&&!ferror(file),"native GWP complete consumption");
        Require(params.WorldSectorTileNum>0&&params.WorldSectorNum>0&&params.WorldFieldNum>0&&params.WorldSize>0,"positive world dimensions");
        printf("PASS native GWP version=%s field-count=%d field-size=%d world-size=%d bytes=%ld\n",params.WorldVer,params.WorldFieldNum,params.WorldFieldSize,params.WorldSize,size);fclose(file);return 0;
    }
    const bool systemMetadata=argc==4&&std::string(argv[1])=="--system-metadata";
    const bool meshSystems=(argc==3&&std::string(argv[1])=="--mesh-systems")||systemMetadata;
    const bool resourceFields=argc==3&&std::string(argv[1])=="--resource-fields";
    const bool particleTrails=argc==3&&std::string(argv[1])=="--particle-trails";
    const bool literalSplinePlayback=argc==4&&std::string(argv[1])=="--spline-playback-literal";
    const bool splinePlayback=(argc==4&&std::string(argv[1])=="--spline-playback")||literalSplinePlayback;
    const bool observedOrderPlayback=argc==4&&std::string(argv[1])=="--spline-playback-observed-order";
    const bool splineObservation=argc==4&&std::string(argv[1])=="--spline-observations";
    std::set<std::string> requested,seen,metadataRequested,metadataSeen;
    if(systemMetadata){std::stringstream csv(argv[2]);std::string name;
        while(std::getline(csv,name,',')){Require(!name.empty()&&name.size()<=128&&metadataRequested.size()<64,"effect metadata selection bound");
            for(char c:name)Require((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'||c=='-',"ASCII effect metadata selection");
            Require(metadataRequested.insert(name).second,"unique effect metadata selection");}
        Require(!metadataRequested.empty()&&argv[2][std::strlen(argv[2])-1]!=',',"nonempty effect metadata selection");
    }
    if(splinePlayback||splineObservation||observedOrderPlayback){std::stringstream csv(argv[2]);std::string name;bool literalSeen=false;
        while(std::getline(csv,name,',')){Require(!name.empty()&&name.size()<=128&&requested.size()<64,"spline selection bound");
            const bool literal=literalSplinePlayback&&(name=="Hot-tempered_Rush_MGR_A024"||name=="Hot-tempered_Rush_MGR_A024_S");
            literalSeen=literalSeen||literal;
            if(!literal)for(char c:name)Require((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_',"ASCII spline selection");
            Require(requested.insert(name).second,"unique spline selection");}
        Require(!requested.empty()&&argv[2][std::strlen(argv[2])-1]!=',',"nonempty spline selection");
        Require(!literalSplinePlayback||literalSeen,"literal spline playback requires an exact reviewed literal name");
    }
    if(argc!=2&&!meshSystems&&!splinePlayback&&!splineObservation&&!observedOrderPlayback&&!resourceFields&&!systemMetadata&&!particleTrails)return 2;
#ifndef CONTENT_EFFECT_RESOURCE_FIELDS
    Require(!resourceFields,"reader build with pinned resource-fields adapter required");
#endif
    FILE* f=fopen(argv[splinePlayback||splineObservation||observedOrderPlayback||systemMetadata?3:meshSystems||resourceFields||particleTrails?2:1],"rb");Require(f!=NULL,"input");
    fseek(f,0,SEEK_END);long end=ftell(f);rewind(f);char header[7]={};Require(fread(header,6,1,f)==1&&strcmp(header,"effect")==0,"header");
#ifdef CONTENT_EFFECT_RESOURCE_FIELDS
    gEffectStringFileBytes=end;gEffectStringFieldsEnabled=resourceFields;
#endif
    CNtlResourceEffect::m_nVersion=Integer(f);Require(CNtlResourceEffect::m_nVersion==0x68,"pinned installed/source version");
    Require(RwEngineInit(NULL,0,0)!=FALSE,"RW engine init");
    HWND window=CreateWindowExA(0,"STATIC","DBO Assets native resource reader",WS_OVERLAPPEDWINDOW,0,0,320,240,NULL,NULL,GetModuleHandle(NULL),NULL);
    Require(window!=NULL,"hidden probe window");RwEngineOpenParams params={};params.displayID=window;Require(RwEngineOpen(&params)!=FALSE,"RW engine open");
    int mode=-1;for(int i=0;i<RwEngineGetNumVideoModes();++i){RwVideoMode v;RwEngineGetVideoModeInfo(&v,i);if(!(v.flags&rwVIDEOMODEEXCLUSIVE)){mode=i;break;}}
    Require(mode>=0&&RwEngineSetVideoMode(mode)&&RwEngineStart(),"RW engine start");
    printf("H\t%u\n",CNtlResourceEffect::m_nVersion);
    if(meshSystems)printf("B\t1\n");
    if(resourceFields)printf("R\t2\n");
    if(systemMetadata)printf("Y\t1\n");
    if(particleTrails)printf("T\t1\n");
    if(splinePlayback)printf("P\t1\n");
    if(observedOrderPlayback)printf("P\t2\n");
    if(splineObservation||observedOrderPlayback)printf("O\t1\n");
    int splines=Integer(f);Require(splines>=0&&splines<20000,"spline count");
    for(int i=0;i<splines;++i){long start=ftell(f);auto name=Name(f);long infoOffset=ftell(f);CNtlEffectSplinePath::INFO info={};
        Require(fread(&info,sizeof(info),1,f)==1,"spline INFO");
        Require(info.nControlCount>=4&&info.nControlCount<=4096&&info.nCurveSubDivision>0&&info.nCurveSubDivision<=4096,"bounded spline allocation");
        const long long curves=(long long)(info.nControlCount-4)*info.nCurveSubDivision+1;
        const long long size=sizeof(info)+(long long)info.nControlCount*sizeof(SNTL_SPLINE_CONTROL_POINT)+curves*sizeof(SNTL_SPLINE_CURVE_POINT);
        Require(curves>0&&curves<=1000000&&size<=end-infoOffset,"bounded spline stream");Require(fseek(f,infoOffset,SEEK_SET)==0,"rewind spline INFO");
        CNtlEffectSplinePath spline(name.c_str());Require(spline.Load(f)!=FALSE&&!ferror(f)&&ftell(f)==infoOffset+size,"native spline");
        printf("S\t%ld\t%ld\t%s\n",start,ftell(f)-start,name.c_str());
        if(splinePlayback&&requested.count(name)){Require(seen.insert(name).second,"unambiguous spline selection");SplinePlayback(spline,name);}
        if(observedOrderPlayback&&requested.count(name)){Require(seen.insert(name).second,"unambiguous spline selection");SplineObserve(spline,name);SplinePlayback(spline,name,true);}
        if(splineObservation&&requested.count(name)){Require(seen.insert(name).second,"unambiguous spline selection");SplineObserve(spline,name);}
    }
    if(splinePlayback||splineObservation||observedOrderPlayback)Require(seen==requested,"all selected splines present");
    long countOffset=ftell(f);int count=Integer(f);Require(count>=0&&count<100000,"effect count");printf("C\t%ld\t%d\n",countOffset,count);
    for(int i=0;i<count;++i){
#ifdef CONTENT_EFFECT_RESOURCE_FIELDS
        gEffectStringFields.clear();gEffectSplineFields.clear();
#endif
        long start=ftell(f);auto name=Name(f);CNtlResourceEffect effect(name.c_str());Require(effect.Load(f)!=FALSE&&!ferror(f)&&ftell(f)<=end,"native effect");
        printf("E\t%ld\t%ld\t%s\n",start,ftell(f)-start,name.c_str());
        unsigned systemIndex=0;
        bool selectedMetadataEffect=systemMetadata&&metadataRequested.count(name)>0;
        if(selectedMetadataEffect)Require(metadataSeen.insert(name).second,"unambiguous effect metadata selection");
        for(auto system:effect.m_svResourceSystem){
            if(particleTrails)if(auto particle=dynamic_cast<CNtlResourceParticleSystem*>(system)){
                if(particle->IsEmitterDataFlag(rpPRTSTDEMITTERDATAFLAGTRAIL)){
                    const auto& trail=particle->m_EmitterTrail;
                    const char* terminator=(const char*)memchr(trail.strTexture,0,sizeof(trail.strTexture));
                    Require(terminator!=NULL,"bounded embedded particle trail texture");
                    printf("T\t%s\t%u\t%s\t",Field(name),systemIndex,Field(system->m_strName));
                    static const char* digits="0123456789abcdef";
                    for(const char* p=trail.strTexture;p!=terminator;++p){unsigned char c=(unsigned char)*p;putchar(digits[c>>4]);putchar(digits[c&15]);}
                    printf("\t%d\t%d\t%.9g\t%.9g\t%.9g\n",trail.nMaxEdgeCount,trail.nSplinePointCount,
                        (double)trail.fWidth,(double)trail.fEdgeLifeTime,(double)trail.fEdgeGap);
                }
            }
#ifdef CONTENT_EFFECT_RESOURCE_FIELDS
            if(resourceFields){
                if(system->m_nPathEmitterDataFlag)SplineResourceField(name,systemIndex,system->m_EmitterPrtSplinePath);
                if(auto mesh=dynamic_cast<CNtlResourceMeshSystem*>(system)){
                    ResourceField(name,systemIndex,"mesh",-1,mesh->m_strMeshFileName);
                    ResourceField(name,systemIndex,"animation",-1,mesh->m_strAniFileName);
                    ResourceField(name,systemIndex,"uv",-1,mesh->m_strUvFileName);
                }
                if(auto particle=dynamic_cast<CNtlResourceParticleSystem*>(system)){
                    ResourceField(name,systemIndex,"texture",-1,particle->m_strTextureName);
                    ResourceField(name,systemIndex,"subsystem",-1,particle->m_strSubSystem);
                }else if(auto vertex=dynamic_cast<CNtlResourceImVertexSystem*>(system))ResourceField(name,systemIndex,"texture",-1,vertex->m_strTextureName);
                else if(auto post=dynamic_cast<CNtlResourcePostEffectSystem*>(system))ResourceField(name,systemIndex,"texture",-1,post->m_strTextureName);
                for(RwUInt32 frame=0;frame<system->m_EmitterMultiTextures.numTextures;++frame)
                    ResourceField(name,systemIndex,"texture",(int)frame,system->m_EmitterMultiTextures.list[frame].strTexture);
            }
#endif
            if(auto particle=dynamic_cast<CNtlResourceParticleSystem*>(system)){Dependency(name,"texture",particle->m_strTextureName);Dependency(name,"effect",particle->m_strSubSystem);}
            if(auto mesh=dynamic_cast<CNtlResourceMeshSystem*>(system)){
                Dependency(name,"mesh",mesh->m_strMeshFileName);Dependency(name,"animation",mesh->m_strAniFileName);Dependency(name,"uv",mesh->m_strUvFileName);
                if(meshSystems)printf("M\t%s\t%u\t%s\t%s\t%s\t%s\n",Field(name),systemIndex,Field(mesh->m_strMeshFileName),Field(mesh->m_strAniFileName),Field(mesh->m_strUvFileName),system->m_nPathEmitterDataFlag?Field(system->m_EmitterPrtSplinePath.strName):"");
            }
            if(selectedMetadataEffect)SystemMetadata(name,systemIndex,system);
            if(auto vertex=dynamic_cast<CNtlResourceImVertexSystem*>(system))Dependency(name,"texture",vertex->m_strTextureName);
            if(auto post=dynamic_cast<CNtlResourcePostEffectSystem*>(system))Dependency(name,"texture",post->m_strTextureName);
            for(RwUInt32 frame=0;frame<system->m_EmitterMultiTextures.numTextures;++frame)
                Dependency(name,"texture",system->m_EmitterMultiTextures.list[frame].strTexture);
            if(system->m_nPathEmitterDataFlag)Dependency(name,"spline",system->m_EmitterPrtSplinePath.strName);
            ++systemIndex;
        }
        Dependency(name,"sound",effect.m_EffectSound.chSoundName);Dependency(name,"sound",effect.m_EffectSound.chSoundName2);Dependency(name,"sound",effect.m_EffectSound.chSoundName3);Dependency(name,"sound",effect.m_EffectSound.chSoundName4);
    }
    if(systemMetadata)Require(metadataSeen==metadataRequested,"all selected effects present");
    Require(ftell(f)==end,"complete file consumed");fclose(f);RwEngineStop();RwEngineClose();RwEngineTerm();DestroyWindow(window);fprintf(stderr,"PASS native effect reader version=%u splines=%d effects=%d bytes=%ld\n",CNtlResourceEffect::m_nVersion,splines,count,end);return 0;
}
