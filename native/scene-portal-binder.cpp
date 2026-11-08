// Native Taiwan outdoor1.4 field boundaries; bind an existing PORTAL_COLLISION.
// Preserve the entire field except that object's name index and selection flags.
#include "precomp_ntlpresentation.h"
#include "NtlWorldCommon.h"
#include "NtlWorldSectorInfo.h"
#include "NtlPLDef.h"
#include "NtlPLObjectType.h"
#include "NtlWorldSector.h"
#include "ntlworldinterface.h"
#include "NtlWorldFieldInfo.h"
#include "NtlPLVisualManager.h"
#include <vector>
#include <string>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

static void Require(bool ok,const char* message){if(!ok){fprintf(stderr,"FAIL %s\n",message);exit(1);}}
static void Read(FILE* f,void* target,size_t n,long limit){long pos=ftell(f);Require(pos>=0&&pos<=limit&&n<=size_t(limit-pos),"bounded prefix");Require(fread(target,1,n,f)==n,"complete prefix");}
static unsigned Number(const char* value){char* end=NULL;unsigned long n=strtoul(value,&end,10);Require(value[0]>='0'&&value[0]<='9'&&*end==0&&n!=0xffffffff,"literal non-invalid ID");return unsigned(n);}
class FieldScene: public CNtlPLVisualManager{public:EActiveWorldType GetActiveWorldType(void) override{return AW_HEGITHFIELD;}};
struct Binding{long nameOffset,flagsOffset,objectStart,objectEnd,countOffset,blockEnd,positionOffset,triggerOffset;unsigned objectCount;RwV3d position;std::vector<RwV3d> terrain;};
static Binding Inspect(const char* fieldPath,unsigned field,unsigned wantedSector,unsigned sid,unsigned expectedName,unsigned expectedFlags,bool checkExpected,unsigned forbidden=0){
    auto* p=dGET_WORLD_PARAM();FILE* f=fopen(fieldPath,"rb");Require(f!=NULL,"field source");fseek(f,0,SEEK_END);long bytes=ftell(f);rewind(f);
    Require(bytes>0&&bytes<=128*1024*1024,"bounded field");
    CNtlWorldSectorInfo reader;Binding result={};result.nameOffset=result.flagsOffset=-1;unsigned matches=0;
    std::vector<RwV3d> heights(p->WorldSectorVertNum*p->WorldSectorVertNum);
    for(unsigned sector=0;sector<4;++sector){
        long start=ftell(f);reader.SectorMaterialSkipToFile(f);Require(ftell(f)>start&&ftell(f)<bytes&&!ferror(f)&&!feof(f),"material boundary");
        Require(reader.SectorHeightfieldLoadFromFile(f,heights.data())!=FALSE&&ftell(f)<bytes&&!ferror(f)&&!feof(f),"heightfield boundary");
        Require(heights[0].x==-p->WorldSizeHalf+(int(field)%p->WorldFieldNum)*p->WorldFieldSize+(int(sector)%2+1)*p->WorldSectorSize&&
            heights[0].z==-p->WorldSizeHalf+(int(field)/p->WorldFieldNum)*p->WorldFieldSize+(int(sector)/2)*p->WorldSectorSize,"literal field/sector location");
        fseek(f,start,SEEK_SET);reader.SectorMeshSkipToFile(f);Require(ftell(f)>start&&ftell(f)<bytes&&!ferror(f)&&!feof(f),"mesh boundary");
        long block=ftell(f);int count=0;Read(f,&count,4,bytes);Require(count>=0&&count<=100000,"object count");result.objectCount+=unsigned(count);
        for(int index=0;index<count;++index){
            long objectStart=ftell(f);reader.ObjectSkipToFile(f,AW_HEGITHFIELD);long objectEnd=ftell(f);
            Require(objectEnd>objectStart&&objectEnd<=bytes&&!ferror(f)&&!feof(f),"native object boundary");fseek(f,objectStart,SEEK_SET);
            unsigned length=0;Read(f,&length,4,objectEnd);Require(length>0&&length<128,"object model capacity");char model[128]={};Read(f,model,length,objectEnd);
            long srtOffset=ftell(f);RwV3d srt[3];Read(f,srt,sizeof(srt),objectEnd);unsigned type=0;Read(f,&type,4,objectEnd);
            Require(type<=EPL_OBJECT_TYPE_MINI_INDOOR_OBJECT,"known object type");
            if(type==EPL_OBJECT_TYPE_MINI_INDOOR_CLOSE||type==EPL_OBJECT_TYPE_MINI_INDOOR_OPEN){CNtlPLObjectTypeHasProperty::SkipToFile(f);Require(ftell(f)<=objectEnd&&!ferror(f)&&!feof(f),"native object property boundary");}
            // This binder intentionally only understands plain portal records.
            // Other object types are skipped by production code, never guessed.
            {
                unsigned trigger=0,storage=0,name=0,flags=0;float visibility=0;
                long triggerOffset=ftell(f);Read(f,&trigger,4,objectEnd);Read(f,&storage,4,objectEnd);long nameOffset=ftell(f);Read(f,&name,4,objectEnd);
                Read(f,&visibility,4,objectEnd);long flagsOffset=ftell(f);Read(f,&flags,4,objectEnd);
                Require(!forbidden||(trigger!=forbidden&&storage!=forbidden),"reserved clone identity must be free in field");
                if(trigger==sid){
                    Require(type==0&&sector==wantedSector&&strcmp(model,"PORTAL_COLLISION")==0,"exact portal SID/sector/model");Require(++matches==1,"unique portal SID");
                    Require(std::isfinite(visibility)&&visibility>0,"finite portal visibility");
                    for(unsigned k=0;k<3;++k)Require(std::isfinite(srt[k].x)&&std::isfinite(srt[k].y)&&std::isfinite(srt[k].z),"finite portal SRT");
                    Require(srt[0].x>0&&srt[0].y>0&&srt[0].z>0,"positive portal scale");
                    Require((flags&NTL_PLEFLAG_TRIGGER)!=0,"existing trigger flag required");
                    if(checkExpected)Require(name==expectedName&&flags==expectedFlags,"native output binding matches");
                    result.nameOffset=nameOffset;result.flagsOffset=flagsOffset;
                    result.objectStart=objectStart;result.objectEnd=objectEnd;result.countOffset=block;result.positionOffset=srtOffset+24;result.triggerOffset=triggerOffset;result.position=srt[2];
                    result.terrain=heights;
                }
            }
            fseek(f,objectEnd,SEEK_SET);
        }
        long blockEnd=ftell(f);fseek(f,block,SEEK_SET);reader.SectorObjectSkipToFile(f,AW_HEGITHFIELD);Require(ftell(f)==blockEnd&&!ferror(f)&&!feof(f),"native object block agreement");
        if(sector==wantedSector)result.blockEnd=blockEnd;
        fseek(f,start,SEEK_SET);reader.OutdoorSectorSkipToFile(f);Require(ftell(f)>=blockEnd&&ftell(f)<=bytes&&!ferror(f)&&!feof(f),"complete sector");
    }
    Require(ftell(f)==bytes&&matches==1,"complete field EOF and unique portal");fclose(f);return result;
}
static void Project(const char* path){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    FILE* gwp=fopen(path,"rb");Require(gwp!=NULL,"GWP");fseek(gwp,0,SEEK_END);long size=ftell(gwp);rewind(gwp);
    LoadFileWorldState(dGET_WORLD_PARAM(),gwp);Require(ftell(gwp)==size&&!ferror(gwp),"native complete GWP");fclose(gwp);
    auto* p=dGET_WORLD_PARAM();Require(strcmp(p->WorldVer,dNTL_WORLD_VERSION)==0&&p->WorldFieldSectorNum==2&&p->WorldFieldNum>0&&p->WorldFieldNum<=64&&
        p->WorldSectorVertNum>1&&p->WorldSectorVertNum<=129&&p->WorldSectorTileSize>0&&p->WorldSectorVertNum==p->WorldSectorTileNum+1&&
        p->WorldSectorSize==p->WorldSectorTileNum*p->WorldSectorTileSize&&p->WorldFieldSize==2*p->WorldSectorSize&&p->WorldFieldNum*p->WorldFieldSize==2*p->WorldSizeHalf,"outdoor1.4 grid");
    memcpy(p->WorldLoadVer,p->WorldVer,10);
}
int main(int argc,char** argv){
    if(argc==4&&strcmp(argv[1],"--field-map")==0){
        Project(argv[2]);FILE* f=fopen(argv[3],"rb");Require(f!=NULL,"field properties");FieldScene scene;CNtlPLSceneManagerFactory::ActiveSceneManager(&scene);sNTL_FIELD_PROP prop;CNtlWorldFieldInfo reader;
        reader.FieldNameLoadFromFile(f,&prop);Require(ftell(f)==64&&!ferror(f)&&!feof(f)&&memchr(prop._Name,0,64)!=NULL,"native field name prefix only");fclose(f);
        char* end=NULL;unsigned long code=strtoul(prop._Name,&end,10);Require(end!=prop._Name&&*end==0,"numeric map code");
        printf("{\"mapCode\":%lu,\"zoneId\":%lu,\"completePropertiesValidated\":false}\n",code,code/1000);return 0;
    }
    if(argc==3&&strcmp(argv[1],"--project-info")==0){Project(argv[2]);auto* p=dGET_WORLD_PARAM();printf("{\"fieldsCross\":%d,\"fieldSize\":%d,\"worldHalf\":%d,\"sectorSize\":%d,\"resourceId\":%u}\n",p->WorldFieldNum,p->WorldFieldSize,p->WorldSizeHalf,p->WorldSectorSize,p->WorldResourceID);return 0;}
    if(argc!=8&&argc!=13)return 2;Project(argv[1]);auto* p=dGET_WORLD_PARAM();
    unsigned field=Number(argv[3]),sector=Number(argv[4]),sid=Number(argv[5]),name=Number(argv[6]);
    Require(field<unsigned(p->WorldFieldNum*p->WorldFieldNum)&&sector<4&&sid>0&&name>0,"binding identity");
    unsigned clone=argc==13?Number(argv[8]):0;Require(argc==8||(clone>=60000000&&clone<=60009999&&clone!=sid&&Number(argv[9])==clone),"reserved separate clone SID/storage ID");
    Binding before=Inspect(argv[2],field,sector,sid,0,0,false,clone);
    FILE* source=fopen(argv[2],"rb");Require(source!=NULL,"source reopen");fseek(source,0,SEEK_END);long size=ftell(source);rewind(source);std::vector<unsigned char> raw(size);Read(source,raw.data(),raw.size(),size);fclose(source);
    unsigned flags=0;memcpy(&flags,raw.data()+before.flagsOffset,4);flags|=NTL_PLEFLAG_SELECTION|NTL_PLEFLAG_PORTAL;
    std::vector<unsigned char> candidate=raw;RwV3d clonedPos=before.position;float terrainHeight=0;
    if(clone){
        char* xEnd=NULL;char* yEnd=NULL;char* zEnd=NULL;float dx=strtof(argv[10],&xEnd),dy=strtof(argv[11],&yEnd),dz=strtof(argv[12],&zEnd);
        Require(xEnd!=argv[10]&&yEnd!=argv[11]&&zEnd!=argv[12]&&*xEnd==0&&*yEnd==0&&*zEnd==0&&std::isfinite(dx)&&std::isfinite(dy)&&std::isfinite(dz)&&std::abs(dx)<=32&&std::abs(dy)<=8&&std::abs(dz)<=32&&(std::abs(dx)+std::abs(dz))>=4,"bounded explicit clone displacement");
        clonedPos.x+=dx;clonedPos.y+=dy;clonedPos.z+=dz;
        float minX=float(-p->WorldSizeHalf+(int(field)%p->WorldFieldNum)*p->WorldFieldSize+(int(sector)%2)*p->WorldSectorSize),minZ=float(-p->WorldSizeHalf+(int(field)/p->WorldFieldNum)*p->WorldFieldSize+(int(sector)/2)*p->WorldSectorSize);
        Require(clonedPos.x>minX+1&&clonedPos.x<minX+p->WorldSectorSize-1&&clonedPos.z>minZ+1&&clonedPos.z<minZ+p->WorldSectorSize-1,"clone stays in declared sector");
        CNtlWorldSector nativeSector;nativeSector.DatumPoint.x=minX+p->WorldSectorSize/2;nativeSector.DatumPoint.y=0;nativeSector.DatumPoint.z=minZ+p->WorldSectorSize/2;
        terrainHeight=RpNtlWorldSectorGetHeight(&nativeSector,&clonedPos,before.terrain.data());
        fprintf(stderr,"clone terrain diagnostic x=%.9g z=%.9g portalY=%.9g terrainY=%.9g\n",clonedPos.x,clonedPos.z,clonedPos.y,terrainHeight);
        Require(std::isfinite(terrainHeight)&&std::abs(terrainHeight-clonedPos.y)<=1,"clone terrain height within one metre; no object collision proof");
        std::vector<unsigned char> record(raw.begin()+before.objectStart,raw.begin()+before.objectEnd);
        memcpy(record.data()+before.triggerOffset-before.objectStart,&clone,4);memcpy(record.data()+before.triggerOffset-before.objectStart+4,&clone,4);
        memcpy(record.data()+before.nameOffset-before.objectStart,&name,4);memcpy(record.data()+before.flagsOffset-before.objectStart,&flags,4);
        memcpy(record.data()+before.positionOffset-before.objectStart,&clonedPos,12);
        candidate.insert(candidate.begin()+before.blockEnd,record.begin(),record.end());int count=0;memcpy(&count,candidate.data()+before.countOffset,4);Require(count<100000,"bounded cloned sector count");++count;memcpy(candidate.data()+before.countOffset,&count,4);
        std::vector<unsigned char> restored=candidate;restored.erase(restored.begin()+before.blockEnd,restored.begin()+before.blockEnd+record.size());--count;memcpy(restored.data()+before.countOffset,&count,4);Require(restored==raw,"all original sector bytes preserved after removing clone");
    }else{
        memcpy(candidate.data()+before.nameOffset,&name,4);memcpy(candidate.data()+before.flagsOffset,&flags,4);
        for(long i=0;i<size;++i)Require(raw[i]==candidate[i]||(i>=before.nameOffset&&i<before.nameOffset+4)||(i>=before.flagsOffset&&i<before.flagsOffset+4),"only binding bytes changed");
    }
    FILE* out=fopen(argv[7],"wbx");Require(out!=NULL,"new output only");Require(fwrite(candidate.data(),1,candidate.size(),out)==candidate.size()&&fclose(out)==0,"flush candidate");
    Binding after=Inspect(argv[7],field,sector,clone?clone:sid,name,flags,true);
    if(clone){Binding old=Inspect(argv[7],field,sector,sid,0,0,false);Require(old.objectCount==before.objectCount+1&&memcmp(&after.position,&clonedPos,12)==0,"clone position/count and original retained");}
    else Require(before.nameOffset==after.nameOffset&&before.flagsOffset==after.flagsOffset&&before.objectCount==after.objectCount,"native boundaries/objects preserved");
    printf("PASS native portal binding field=%u sector=%u sid=%u name=%u flags=%u objects=%u bytes=%zu nameOffset=%ld flagsOffset=%ld preservedOtherBytes=true cloned=%u x=%.9g y=%.9g z=%.9g objectStart=%ld objectEnd=%ld countOffset=%ld insertOffset=%ld positionOffset=%ld triggerOffset=%ld terrainY=%.9g\n",field,sector,clone?clone:sid,name,flags,after.objectCount,candidate.size(),after.nameOffset,after.flagsOffset,clone?1:0,after.position.x,after.position.y,after.position.z,after.objectStart,after.objectEnd,after.countOffset,before.blockEnd,after.positionOffset,after.triggerOffset,terrainHeight);return 0;
}
