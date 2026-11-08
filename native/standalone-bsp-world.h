// New Snake Way BSP wrapper, using the native GWP writer and exact block-property
// members consumed by CNtlWorldBlockMananger::LoadProperty/SaveProperty.
#include "NtlPLVisualManager.h"
#include "NtlWorldBlockManager.h"
#include "NtlWorldSectorInfo.h"
#include <string>
class BspFieldContext:public CNtlPLVisualManager {public:EActiveWorldType GetActiveWorldType(void) override{return AW_RWWORLD;}};
static void BspTransfer(FILE* input,FILE* output,void* value,size_t bytes){
    Need(fread(value,1,bytes,input)==bytes,"bounded native block property member");
    Need(fwrite(value,1,bytes,output)==bytes,"native block property member roundtrip");
}
static void BspBlock(FILE* input,FILE* output,sNTL_FIELD_PROP& p,sNTL_BLOCK_PROP& b){
    BspTransfer(input,output,p._Name,64);
    BspTransfer(input,output,&p._FogColor,sizeof(p._FogColor));BspTransfer(input,output,p._FogCamPlane,sizeof(p._FogCamPlane));
    BspTransfer(input,output,&p._BaseSkyMode,sizeof(p._BaseSkyMode));BspTransfer(input,output,&p._NewSkyValue,sizeof(p._NewSkyValue));
    BspTransfer(input,output,p._RGBSkyColor,sizeof(p._RGBSkyColor));BspTransfer(input,output,p._BaseSkyTexName,sizeof(p._BaseSkyTexName));
    BspTransfer(input,output,&p._BaseSkySpeed,sizeof(p._BaseSkySpeed));BspTransfer(input,output,p._BlendedTexName,sizeof(p._BlendedTexName));
    BspTransfer(input,output,p._BlendedTexSpeed,sizeof(p._BlendedTexSpeed));BspTransfer(input,output,&p._ClrLightAmbient,sizeof(p._ClrLightAmbient));
    for(unsigned i=0;i<dNTL_PL_DIRECTIONAL_LIGHT_NUM;i++){BspTransfer(input,output,&p._ClrLightDirectional[i],sizeof(p._ClrLightDirectional[i]));BspTransfer(input,output,&p._AngLightDirectional[i],sizeof(p._AngLightDirectional[i]));}
    BspTransfer(input,output,&p._IdxBGMTbl,sizeof(p._IdxBGMTbl));BspTransfer(input,output,&p._IdxEnvTbl,sizeof(p._IdxEnvTbl));
    BspTransfer(input,output,p._IdxShareTbl,sizeof(p._IdxShareTbl));BspTransfer(input,output,&p._MonoPower,sizeof(p._MonoPower));
    BspTransfer(input,output,p._NtlPlanet,sizeof(p._NtlPlanet));BspTransfer(input,output,&p._NtlSpecular,sizeof(p._NtlSpecular));
    BspTransfer(input,output,&p._FieldColor,sizeof(p._FieldColor));BspTransfer(input,output,p._IdxWeather,sizeof(p._IdxWeather));
    BspTransfer(input,output,&p._NtlHeatHaze,sizeof(p._NtlHeatHaze));BspTransfer(input,output,&b._GameProp,sizeof(b._GameProp));
}
static int StandaloneSnakeBsp(int argc,char** argv){
    // sourceGWP sourceWfp emptyField outputGWP outputWfp (all private snapshots).
    const bool authored=strcmp(argv[1],"--standalone-authored-bsp")==0;
    if(argc!=(authored?10:7))return 2;
    const bool power=strcmp(argv[1],"--standalone-power-bsp")==0;
    const char* folder=authored?argv[7]:power?"dbo_assets_power_arena_001":"dbo_assets_snakeway_001";
    const char* label=authored?argv[9]:power?"600100010":"600090010";const unsigned resource=authored?unsigned(strtoul(argv[8],NULL,10)):power?6010:6009;
    Need(strlen(folder)<=32&&strncmp(folder,"dbo_assets_",12)==0&&strspn(folder,"abcdefghijklmnopqrstuvwxyz0123456789_")==strlen(folder)&&resource&&resource!=UINT_MAX&&strspn(label,"0123456789")==strlen(label)&&strtoul(label,NULL,10)&&strtoul(label,NULL,10)!=UINT_MAX,"bounded authored world identity");
    FILE* source=fopen(argv[2],"rb");Need(source!=NULL,"BSP GWP template");auto original=Bytes(source);rewind(source);
    LoadFileWorldState(dGET_WORLD_PARAM(),source);Need(ftell(source)==250&&!ferror(source),"BSP native GWP EOF");fclose(source);
    auto* p=dGET_WORLD_PARAM();
    fprintf(stderr,"BSP template type=%d fields=%d fieldSectors=%d size=%d half=%d blockSize=%d bsp=%s\n",p->WorldType,p->WorldFieldNum,p->WorldFieldSectorNum,p->WorldSize,p->WorldSizeHalf,p->WorldBlockSize,p->WorldBSP);
    Need(strcmp(p->WorldVer,dNTL_WORLD_VERSION)==0&&p->WorldType==1&&p->WorldFieldNum==8&&p->WorldFieldSectorNum==2&&
        p->WorldSize==2048&&p->WorldSizeHalf==1024&&p->WorldBlockSize==64&&strcmp(p->WorldBSP,"DBO_Battle_001")==0,"reviewed indoor BSP template dimensions/name");
    Need(Serialized()==original,"BSP template native byte-exact GWP");
    memcpy(p->WorldLoadVer,p->WorldVer,10);memcpy(p->WorldSaveVer,p->WorldVer,10);
    fprintf(stderr,"BSP stage: GWP roundtrip pass; constructing field context\n");
    BspFieldContext context;CNtlPLSceneManager* previous=GetSceneManager();CNtlPLSceneManagerFactory::ActiveSceneManager(&context);
    fprintf(stderr,"BSP stage: field context active\n");
    FILE* field=fopen(argv[4],"rb");Need(field!=NULL,"empty BSP field template");fseek(field,0,SEEK_END);Need(ftell(field)==304,"reviewed empty BSP field bytes");rewind(field);
    CNtlWorldSectorInfo sectors;for(unsigned i=0;i<4;i++){
        long start=ftell(field);int count=0;Need(fread(&count,4,1,field)==1&&count==0,"empty new-world object block");fseek(field,start,SEEK_SET);
        sectors.SectorObjectSkipToFile(field,AW_RWWORLD);Need(ftell(field)==start+4,"native empty object boundary");
        fprintf(stderr,"BSP stage: sector%u start%ld indoor skip\n",i,start);
        fseek(field,start,SEEK_SET);sectors.IndoorSectorSkipToFile(field);Need(ftell(field)>start&&ftell(field)<=304&&!ferror(field)&&!feof(field),"native indoor sector boundary");
    }
    Need(ftell(field)==304,"native empty field EOF");fclose(field);
    fprintf(stderr,"BSP stage: empty field pass; reading block properties\n");
    const unsigned blocks=unsigned(p->WorldSize/p->WorldBlockSize)*unsigned(p->WorldSize/p->WorldBlockSize);
    FILE* wfp=fopen(argv[3],"rb"),*round=tmpfile();Need(wfp&&round,"block source/roundtrip stream");
    fseek(wfp,0,SEEK_END);Need(ftell(wfp)==443392,"reviewed template WFP bytes");rewind(wfp);
    std::vector<unsigned char> candidate;candidate.reserve(443392);
    for(unsigned i=0;i<blocks;i++){
        long start=ftell(wfp);sNTL_FIELD_PROP property(FALSE);sNTL_BLOCK_PROP block;BspBlock(wfp,round,property,block);
        Need(ftell(wfp)-start==433&&memchr(property._Name,0,64)!=NULL,"native block stride/name");
    }
    Need(ftell(wfp)==443392&&!ferror(wfp),"complete native WFP template");
    rewind(wfp);rewind(round);std::vector<unsigned char> before(443392),readback(443392);
    Need(fread(before.data(),1,before.size(),wfp)==before.size()&&fread(readback.data(),1,readback.size(),round)==readback.size()&&before==readback,"every template property byte preserved by native member codec");
    fclose(wfp);fclose(round);candidate=before;
    for(unsigned i=0;i<blocks;i++){char name[64]={};strcpy_s(name,label);memcpy(candidate.data()+i*433,name,64);}
    FILE* output=fopen(argv[6],"wbx");Need(output&&fwrite(candidate.data(),1,candidate.size(),output)==candidate.size()&&fclose(output)==0,"new WFP names only");
    strcpy_s(p->WorldBSP,folder);p->WorldObjectSerialID=0;p->WorldResourceID=resource;
    auto next=Serialized();Need(next.size()==250,"native new BSP GWP size");
    output=fopen(argv[5],"wbx");Need(output!=NULL,"new GWP output");SaveFileWorldState(p,output);Need(!ferror(output)&&fclose(output)==0,"native new GWP write");
    output=fopen(argv[5],"rb");Need(output&&Bytes(output)==next,"new GWP readback bytes");rewind(output);LoadFileWorldState(p,output);fclose(output);
    Need(Serialized()==next&&p->WorldType==1&&p->WorldResourceID==resource&&strcmp(p->WorldBSP,folder)==0,"native new BSP GWP roundtrip");
    // The factory rejects NULL, including the initial standalone-reader state.
    // This command immediately returns from main, so no later code uses context.
    if(previous)CNtlPLSceneManagerFactory::ActiveSceneManager(previous);
    printf("PASS standalone BSP world resource%u map%s fields64 blocks1024 nativeGWP250 WFP433 names-only emptyField304; NOT Navi or gameplay\n",resource,label);return 0;
}
