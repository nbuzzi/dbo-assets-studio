// Taiwan FILE reader + CNtlPLObject::SetTransform's actual RenderWare matrices.
// TOS1 is a placed-object intermediate, not a collision or server Navi format.
#include "precomp_ntlpresentation.h"
#include "NtlWorldCommon.h"
#include "NtlWorldSectorInfo.h"
#include "NtlPLObjectType.h"
#include "NtlPLDef.h"
#include <fstream>
#include <string>
#include <vector>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <algorithm>
#include <map>
#include <set>

static void Require(bool ok,const char* what){if(!ok){fprintf(stderr,"FAIL %s\n",what);exit(1);}}
static void Read(FILE* f,void* dst,size_t bytes,long limit){
    long pos=ftell(f);Require(pos>=0&&pos<=limit&&bytes<=size_t(limit-pos),"bounded object prefix");
    Require(fread(dst,1,bytes,f)==bytes,"complete object prefix");
}
static void Write(FILE* f,const void* data,size_t n){Require(fwrite(data,1,n,f)==n,"write snapshot");}
static void UInt(FILE* f,unsigned v){Write(f,&v,4);}
static void Vec(FILE* f,const RwV3d& v){Require(std::isfinite(v.x)&&std::isfinite(v.y)&&std::isfinite(v.z),"finite vector");Write(f,&v,12);}
struct FieldName { long start,end; std::string key; };
static std::vector<FieldName> Names(const char* path){
    FILE* f=fopen(path,"rb");Require(f!=NULL,"field binding input");fseek(f,0,SEEK_END);long size=ftell(f);rewind(f);
    Require(size>0&&size<=128*1024*1024,"bounded native field");CNtlWorldSectorInfo reader;std::vector<FieldName> names;
    for(unsigned sector=0;sector<4;++sector){
        long start=ftell(f);reader.SectorMeshSkipToFile(f);Require(ftell(f)>start&&ftell(f)<size&&!ferror(f)&&!feof(f),"native sector mesh boundary");
        long block=ftell(f);int count=0;Read(f,&count,4,size);Require(count>=0&&count<=100000,"native bounded placed count");
        for(int index=0;index<count;++index){
            long begin=ftell(f);reader.ObjectSkipToFile(f,AW_HEGITHFIELD);long end=ftell(f);
            Require(end>begin&&end<=size&&!ferror(f)&&!feof(f),"native placed-object boundary");fseek(f,begin,SEEK_SET);
            unsigned length=0;Read(f,&length,4,end);Require(length>0&&length<128,"native model name capacity");
            char name[128]={};Read(f,name,length,end);
            for(unsigned k=0;k<length;++k)Require((name[k]>='a'&&name[k]<='z')||(name[k]>='A'&&name[k]<='Z')||(name[k]>='0'&&name[k]<='9')||name[k]=='_',"literal model key");
            names.push_back({begin,ftell(f),std::string(name,length)});fseek(f,end,SEEK_SET);
        }
        long end=ftell(f);fseek(f,block,SEEK_SET);reader.SectorObjectSkipToFile(f,AW_HEGITHFIELD);Require(ftell(f)==end&&!ferror(f)&&!feof(f),"native object block agreement");
        fseek(f,start,SEEK_SET);reader.OutdoorSectorSkipToFile(f);Require(ftell(f)>=end&&ftell(f)<=size&&!ferror(f)&&!feof(f),"complete native sector");
    }
    Require(ftell(f)==size,"complete native field EOF");fclose(f);return names;
}
static std::vector<char> FieldBytes(const char* path){
    std::ifstream f(path,std::ios::binary);Require(bool(f),"field bytes input");return std::vector<char>((std::istreambuf_iterator<char>(f)),std::istreambuf_iterator<char>());
}
int main(int argc,char** argv){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    if(argc!=4)return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    // RenderWare 3.6 math module globals are initialized at EngineOpen/Start,
    // not Init alone. Same hidden D3D9 device lifecycle as media-helper.
    HWND window=CreateWindowA("STATIC","DBO AssetsSceneNative",0,0,0,64,64,NULL,NULL,GetModuleHandle(NULL),NULL);
    Require(window!=NULL&&RwEngineInit(NULL,0,1024*1024)!=FALSE,"RenderWare initialization");
    RwEngineOpenParams open={};open.displayID=window;
    Require(RwEngineOpen(&open)!=FALSE,"RenderWare engine open");
    int mode=-1;for(int i=0;i<RwEngineGetNumVideoModes();++i){RwVideoMode info;if(RwEngineGetVideoModeInfo(&info,i)&&!(info.flags&rwVIDEOMODEEXCLUSIVE)){mode=i;break;}}
    Require(mode>=0&&RwEngineSetVideoMode(mode)&&RwEngineStart(),"RenderWare hidden device start");
    FILE* gwp=fopen(argv[1],"rb");Require(gwp!=NULL,"GWP");fseek(gwp,0,SEEK_END);long size=ftell(gwp);rewind(gwp);
    LoadFileWorldState(dGET_WORLD_PARAM(),gwp);Require(ftell(gwp)==size&&!ferror(gwp),"native complete GWP");fclose(gwp);
    auto* p=dGET_WORLD_PARAM();Require(strcmp(p->WorldVer,dNTL_WORLD_VERSION)==0&&p->WorldFieldSectorNum==2&&p->WorldFieldNum>0&&p->WorldFieldNum<=64&&
        p->WorldSectorVertNum>1&&p->WorldSectorVertNum<=129&&p->WorldSectorTileSize>0&&p->WorldSectorVertNum==p->WorldSectorTileNum+1&&
        p->WorldSectorSize==p->WorldSectorTileNum*p->WorldSectorTileSize&&p->WorldFieldSize==2*p->WorldSectorSize&&
        p->WorldFieldNum*p->WorldFieldSize==2*p->WorldSizeHalf,"Taiwan outdoor 1.4 grid");
    memcpy(p->WorldLoadVer,p->WorldVer,10);
    std::ifstream list(argv[2]);Require(bool(list),"field list");std::vector<std::string> paths;std::string line;
    while(std::getline(list,line)){if(!line.empty()&&line.back()=='\r')line.pop_back();Require(!line.empty(),"nonempty literal field");paths.push_back(line);}
    Require(paths.size()==unsigned(p->WorldFieldNum*p->WorldFieldNum),"complete nominal grid");
    FILE* out=fopen(argv[3],"wbx");Require(out!=NULL,"new TOS1 output");Write(out,"TOS1",4);UInt(out,unsigned(paths.size()));UInt(out,0);
    CNtlWorldSectorInfo reader;unsigned total=0;std::vector<RwV3d> heights(p->WorldSectorVertNum*p->WorldSectorVertNum);
    const RwV3d xAxis={1,0,0},yAxis={0,1,0},zAxis={0,0,1};
    for(unsigned field=0;field<paths.size();++field){
        FILE* f=fopen(paths[field].c_str(),"rb");Require(f!=NULL,"field input");fseek(f,0,SEEK_END);size=ftell(f);rewind(f);
        for(unsigned sector=0;sector<4;++sector){
            long sectorStart=ftell(f);reader.SectorMaterialSkipToFile(f);
            Require(ftell(f)>sectorStart&&ftell(f)<size&&!ferror(f)&&!feof(f),"native material boundary");
            const long heightStart=ftell(f),heightBytes=long(sizeof(RwV3d)+heights.size()*sizeof(RwReal));
            Require(heightStart>=0&&heightStart<=size&&heightBytes<=size-heightStart,"complete compressed native height payload");
            Require(reader.SectorHeightfieldLoadFromFile(f,heights.data())!=FALSE&&ftell(f)==heightStart+heightBytes&&!ferror(f)&&!feof(f),"native heights");
            const RwReal expectedX=RwReal(-p->WorldSizeHalf+(int(field)%p->WorldFieldNum)*p->WorldFieldSize+(int(sector)%2+1)*p->WorldSectorSize);
            const RwReal expectedZ=RwReal(-p->WorldSizeHalf+(int(field)/p->WorldFieldNum)*p->WorldFieldSize+(int(sector)/2)*p->WorldSectorSize);
            if(heights[0].x!=expectedX||heights[0].z!=expectedZ)
                fprintf(stderr,"field=%u sector=%u nativeFirstVertex=(%.9g,%.9g,%.9g) expectedXZ=(%.9g,%.9g) materialEnd=%ld\n",
                    field,sector,heights[0].x,heights[0].y,heights[0].z,expectedX,expectedZ,heightStart);
            Require(heights[0].x==expectedX&&heights[0].z==expectedZ,"sector belongs to declared field");
            fseek(f,sectorStart,SEEK_SET);reader.SectorMeshSkipToFile(f);Require(ftell(f)>sectorStart&&ftell(f)<size&&!ferror(f)&&!feof(f),"native mesh boundary");
            long block=ftell(f);RwInt32 count=0;Read(f,&count,4,size);Require(count>=0&&count<=100000,"bounded object count");
            for(RwInt32 j=0;j<count;++j){
                long start=ftell(f);reader.ObjectSkipToFile(f,AW_HEGITHFIELD);long end=ftell(f);
                Require(end>start&&end<=size&&!ferror(f)&&!feof(f),"native object boundary");fseek(f,start,SEEK_SET);
                RwUInt32 length=0;Read(f,&length,4,end);Require(length>0&&length<128,"native name capacity");
                char name[128]={0};Read(f,name,length,end);
                for(unsigned k=0;k<length;++k)Require((name[k]>='a'&&name[k]<='z')||(name[k]>='A'&&name[k]<='Z')||(name[k]>='0'&&name[k]<='9')||name[k]=='_',"literal model key");
                RwV3d srt[3];Read(f,srt,sizeof(srt),end);
                for(unsigned k=0;k<3;++k)Require(std::isfinite(srt[k].x)&&std::isfinite(srt[k].y)&&std::isfinite(srt[k].z),"finite transform");
                Require(srt[0].x>0&&srt[0].y>0&&srt[0].z>0,"positive scale only; reflected winding requires another profile");
                RwUInt32 type=0;Read(f,&type,4,end);Require(type<=EPL_OBJECT_TYPE_MINI_INDOOR_OBJECT,"object type");
                if(type==EPL_OBJECT_TYPE_MINI_INDOOR_CLOSE||type==EPL_OBJECT_TYPE_MINI_INDOOR_OPEN){CNtlPLObjectTypeHasProperty::SkipToFile(f);Require(ftell(f)<=end&&!ferror(f)&&!feof(f),"indoor property boundary");}
                RwUInt32 trigger=0,object=0,nameIndex=0,flags=0;RwReal visibility=0;
                Read(f,&trigger,4,end);Read(f,&object,4,end);Read(f,&nameIndex,4,end);Read(f,&visibility,4,end);Read(f,&flags,4,end);Require(std::isfinite(visibility),"finite visibility");
                // Exact production transform order, including rotated pitch/roll axes.
                RwMatrix matrix,rotation;RwV3d left,front,up;
                RwMatrixRotate(&rotation,&yAxis,srt[1].y,rwCOMBINEREPLACE);
                RwV3dTransformVector(&front,&zAxis,&rotation);RwV3dTransformVector(&left,&xAxis,&rotation);
                RwMatrixRotate(&rotation,&left,srt[1].x,rwCOMBINEREPLACE);
                RwV3dTransformVector(&front,&front,&rotation);RwV3dTransformVector(&up,&yAxis,&rotation);
                RwMatrixScale(&matrix,&srt[0],rwCOMBINEREPLACE);
                RwMatrixRotate(&matrix,&yAxis,srt[1].y,rwCOMBINEPOSTCONCAT);
                RwMatrixRotate(&matrix,&left,srt[1].x,rwCOMBINEPOSTCONCAT);
                RwMatrixRotate(&matrix,&front,srt[1].z,rwCOMBINEPOSTCONCAT);
                RwMatrixTranslate(&matrix,&srt[2],rwCOMBINEPOSTCONCAT);
                UInt(out,field);UInt(out,sector);UInt(out,unsigned(j));UInt(out,length);Write(out,name,length);
                UInt(out,type);UInt(out,flags);UInt(out,trigger);UInt(out,object);UInt(out,nameIndex);
                for(unsigned k=0;k<3;++k)Vec(out,srt[k]);
                Vec(out,matrix.right);Vec(out,matrix.up);Vec(out,matrix.at);Vec(out,matrix.pos);
                Require(++total<=100000,"scene object bound");fseek(f,end,SEEK_SET);
            }
            long end=ftell(f);fseek(f,block,SEEK_SET);reader.SectorObjectSkipToFile(f,AW_HEGITHFIELD);Require(ftell(f)==end&&!ferror(f)&&!feof(f),"native object block agreement");
            fseek(f,sectorStart,SEEK_SET);reader.OutdoorSectorSkipToFile(f);Require(ftell(f)>=end&&ftell(f)<=size&&!ferror(f)&&!feof(f),"complete sector boundary");
        }
        Require(ftell(f)==size,"complete field EOF");fclose(f);
    }
    Require(fseek(out,8,SEEK_SET)==0,"object count seek");UInt(out,total);Require(fclose(out)==0,"flush snapshot");
    RwEngineStop();RwEngineClose();RwEngineTerm();DestroyWindow(window);
    printf("PASS fields=%u placedObjects=%u nativeRenderWareMatrices=true; NOT collision or Navi\n",unsigned(paths.size()),total);return 0;
}
