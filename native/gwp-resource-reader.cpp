// Canonical Taiwan outdoor1.4 serializer; private binding candidate, not Navi.
#include "precomp_ntlpresentation.h"
#include "NtlWorldCommon.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <vector>
static void Need(bool ok,const char* text){if(!ok){fprintf(stderr,"FAIL %s\n",text);exit(1);}}
static std::vector<unsigned char> Bytes(FILE* file){
    Need(fseek(file,0,SEEK_END)==0&&ftell(file)==250,"reviewed outdoor1.4 GWP size");
    rewind(file);std::vector<unsigned char> bytes(250);Need(fread(bytes.data(),1,250,file)==250,"complete GWP bytes");return bytes;
}
static void Load(FILE* file){
    rewind(file);char version[10];Need(fread(version,1,10,file)==10&&memchr(version,0,10)!=NULL&&strcmp(version,dNTL_WORLD_VERSION)==0,"bounded Taiwan1.4 version");
    rewind(file);LoadFileWorldState(dGET_WORLD_PARAM(),file);
    auto* p=dGET_WORLD_PARAM();
    Need(ftell(file)==250&&!ferror(file)&&strcmp(p->WorldVer,dNTL_WORLD_VERSION)==0&&p->WorldType==0&&
        p->WorldFieldNum==16&&p->WorldFieldSectorNum==2&&p->WorldSectorTileSize==4&&p->WorldSectorTileNum==32&&
        p->WorldSectorVertNum==33&&p->WorldSectorSize==128&&p->WorldFieldSize==256&&p->WorldSizeHalf==2048,
        "native complete reviewed outdoor1.4 dimensions");
}
static std::vector<unsigned char> Serialized(){
    FILE* file=tmpfile();Need(file!=NULL,"private serialization buffer");SaveFileWorldState(dGET_WORLD_PARAM(),file);
    Need(!ferror(file),"native GWP serialization");auto bytes=Bytes(file);fclose(file);return bytes;
}
#include "standalone-bsp-world.h"
// Read-only metadata mode. A242-byte current-version project can omit both
// Taiwan identity words: use zeros only in a private stream for inspection,
// explicitly report that fact, and never emit an installable project.
static int InspectProject(int argc,char** argv){
    Need(argc==3,"inspect-project input argument");
    FILE* source=fopen(argv[2],"rb");Need(source!=NULL,"inspection GWP input");
    Need(fseek(source,0,SEEK_END)==0,"inspection input size");long length=ftell(source);
    Need(length==242||length==246||length==250,"inspection bounded242/246/250 byte project");
    rewind(source);std::vector<unsigned char> original((size_t)length);
    Need(fread(original.data(),1,original.size(),source)==original.size()&&!ferror(source),"inspection complete source");fclose(source);
    Need(memchr(original.data(),0,10)!=NULL,"inspection terminated version");
    bool oldVersion=strcmp((char*)original.data(),dNTL_WORLD_VERSION_OLD)==0;
    bool currentVersion=strcmp((char*)original.data(),dNTL_WORLD_VERSION)==0;
    Need(oldVersion||currentVersion,"inspection native old/current version only");
    bool synthetic=currentVersion&&length==242;
    Need(oldVersion||length==250||synthetic,"inspection partial current identity refused");
    std::vector<unsigned char> inputBytes=original;
    if(synthetic)inputBytes.resize(250,0);
    auto* p=dGET_WORLD_PARAM();p->WorldObjectSerialID=0;p->WorldResourceID=0;
    FILE* input=tmpfile();Need(input!=NULL,"inspection private native stream");
    Need(fwrite(inputBytes.data(),1,inputBytes.size(),input)==inputBytes.size(),"inspection stream write");rewind(input);
    LoadFileWorldState(p,input);
    Need(!ferror(input)&&!feof(input)&&ftell(input)==(long)inputBytes.size(),"inspection native complete EOF");fclose(input);
    Need(p->WorldType==0||p->WorldType==1,"inspection known world type");
    Need(!oldVersion||length==(p->WorldType==1?246:242),"inspection native old-version suffix");
    Need(p->WorldFieldNum>0&&p->WorldFieldNum<=256&&p->WorldFieldSectorNum>0&&p->WorldFieldSectorNum<=16&&
        p->WorldSectorTileSize>0&&p->WorldSectorTileSize<=256&&p->WorldSectorTileNum>0&&p->WorldSectorTileNum<=512&&
        p->WorldSectorVertNum>0&&p->WorldSectorVertNum<=513&&p->WorldSectorSize>0&&p->WorldSectorSize<=131072&&
        p->WorldFieldSize>0&&p->WorldFieldSize<=2097152&&p->WorldSize>0&&p->WorldSize<=536870912,
        "inspection bounded native dimensions");
    Need(std::isfinite(p->WorldNearPlane)&&std::isfinite(p->WorldFarPlane)&&std::isfinite(p->WorldCamSpeedPerSec),"inspection finite camera fields");
    FILE* saved=tmpfile();Need(saved!=NULL,"inspection native serialization stream");SaveFileWorldState(p,saved);
    Need(!ferror(saved)&&ftell(saved)==(long)inputBytes.size(),"inspection native serialized length");rewind(saved);
    std::vector<unsigned char> roundtrip(inputBytes.size());
    Need(fread(roundtrip.data(),1,roundtrip.size(),saved)==roundtrip.size()&&roundtrip==inputBytes,"inspection exact native roundtrip");fclose(saved);
    Need(memcmp(roundtrip.data(),original.data(),original.size())==0,"inspection source bytes preserved");
    bool consistent=p->WorldSectorVertNum==p->WorldSectorTileNum+1&&
        (long long)p->WorldSectorSize==(long long)p->WorldSectorTileSize*p->WorldSectorTileNum&&
        (long long)p->WorldFieldSize==(long long)p->WorldSectorSize*p->WorldFieldSectorNum&&
        (long long)p->WorldSize==(long long)p->WorldFieldSize*p->WorldFieldNum&&
        (long long)p->WorldSizeHalf*2==p->WorldSize&&
        (long long)p->WorldSectorNum==(long long)p->WorldFieldNum*p->WorldFieldSectorNum;
    printf("{\"contract\":\"native-gwp-inspection-v1\",\"nativeReaderPass\":true,\"sourceBytes\":%ld,\"nativeInputBytes\":%u,"
        "\"sourcePrefixByteExact\":true,\"sourceNativeRoundtripWithoutSyntheticIdentity\":%s,\"identityFieldsSyntheticForInspectionOnly\":%s,"
        "\"version\":\"%s\",\"worldType\":%d,\"worldFieldNum\":%d,\"worldFieldSectorNum\":%d,"
        "\"worldSectorTileSize\":%d,\"worldSectorTileNum\":%d,\"worldSectorVertNum\":%d,"
        "\"worldSectorSize\":%d,\"worldSectorNum\":%d,\"worldFieldSize\":%d,\"worldSize\":%d,\"worldSizeHalf\":%d,"
        "\"worldObjectSerialId\":%u,\"worldResourceId\":%u,\"worldTriggerSerialId\":%u,"
        "\"sourceHasObjectSerialId\":%s,\"sourceHasResourceId\":%s,"
        "\"worldNearPlane\":%.9g,\"worldFarPlane\":%.9g,\"worldCameraSpeed\":%.9g,"
        "\"dimensionRelationsConsistent\":%s,\"rawNameTerminated\":%s,\"bspNameTerminated\":%s,"
        "\"geometryTested\":false,\"navigationGenerated\":false,\"runtimeInstalled\":false}\n",
        length,(unsigned)inputBytes.size(),synthetic?"false":"true",synthetic?"true":"false",p->WorldVer,p->WorldType,
        p->WorldFieldNum,p->WorldFieldSectorNum,p->WorldSectorTileSize,p->WorldSectorTileNum,p->WorldSectorVertNum,
        p->WorldSectorSize,p->WorldSectorNum,p->WorldFieldSize,p->WorldSize,p->WorldSizeHalf,
        (unsigned)p->WorldObjectSerialID,(unsigned)p->WorldResourceID,(unsigned)p->WorldTriggerSerialID,
        (currentVersion&&length==250)||(oldVersion&&p->WorldType==1)?"true":"false",
        currentVersion&&length==250?"true":"false",
        (double)p->WorldNearPlane,(double)p->WorldFarPlane,(double)p->WorldCamSpeedPerSec,
        consistent?"true":"false",memchr(p->WorldRAW,0,64)?"true":"false",memchr(p->WorldBSP,0,64)?"true":"false");
    return 0;
}
int main(int argc,char** argv){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    if(argc>1&&strcmp(argv[1],"--inspect-project")==0)return InspectProject(argc,argv);
    if(argc>1&&strcmp(argv[1],"--standalone-snake-bsp")==0)return StandaloneSnakeBsp(argc,argv);
    if(argc>1&&strcmp(argv[1],"--standalone-power-bsp")==0)return StandaloneSnakeBsp(argc,argv);
    if(argc>1&&strcmp(argv[1],"--standalone-authored-bsp")==0)return StandaloneSnakeBsp(argc,argv);
    if(argc!=4)return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    char* end=NULL;unsigned long resource=strtoul(argv[2],&end,10);
    Need(end!=argv[2]&&*end==0&&resource==6001,"reviewed Devildom resource6001 only");
    FILE* source=fopen(argv[1],"rb");Need(source!=NULL,"source GWP");auto original=Bytes(source);Load(source);fclose(source);
    unsigned previous=dGET_WORLD_PARAM()->WorldResourceID;Need(previous==0,"source Devildom resource0 only");
    Need(Serialized()==original,"native baseline byte-exact roundtrip");
    dGET_WORLD_PARAM()->WorldResourceID=resource;auto candidate=Serialized();
    Need(memcmp(original.data(),candidate.data(),original.size()-4)==0&&
        memcmp(original.data()+246,candidate.data()+246,4)!=0,"only native resource field changes");
    FILE* output=fopen(argv[3],"wbx");Need(output!=NULL,"new candidate GWP");
    SaveFileWorldState(dGET_WORLD_PARAM(),output);Need(!ferror(output)&&fclose(output)==0,"native candidate write");
    output=fopen(argv[3],"rb");Need(output!=NULL&&Bytes(output)==candidate,"candidate readback bytes");Load(output);fclose(output);
    Need(dGET_WORLD_PARAM()->WorldResourceID==resource&&Serialized()==candidate,"native target load/save roundtrip");
    printf("PASS GWP native roundtrip sourceResource=%u targetResource=%lu bytes=250 preservedPrefix=246; NOT navigation or gameplay acceptance\n",previous,resource);
    return 0;
}
