// Read-only presentation metadata catalog through production Taiwan serializers.
#include "precomp_ntlpresentation.h"
#include "NtlPLObjectProperty.h"
#include "NtlSerializer.h"
#include <fstream>
#include <iterator>
#include <vector>
#include <set>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <map>
#include <cctype>

static void Require(bool ok,const char* message){if(!ok){fprintf(stderr,"FAIL %s\n",message);exit(1);}}
static void Hex(const char* text){for(auto p=(const unsigned char*)text;*p;++p)printf("%02x",*p);}
struct Catalog { unsigned version;std::string path;std::vector<std::pair<std::string,std::vector<char>>> rows; };
static void Dependency(const char* key,const char* kind,const std::string& path){
    if(path.empty())return;printf("DEPENDENCY\t");Hex(key);printf("\t%s\t",kind);Hex(path.c_str());printf("\n");
}
static std::string Fixed(const char* text,size_t length){Require(memchr(text,0,length)!=NULL,"terminated dependency name");return text;}
static void Sound(const char* key,SEventSound* sound){
    Dependency(key,"sound",Fixed(sound->chSoundName,sizeof(sound->chSoundName)));
    Dependency(key,"sound",Fixed(sound->chSoundName2,sizeof(sound->chSoundName2)));
    Dependency(key,"sound",Fixed(sound->chSoundName3,sizeof(sound->chSoundName3)));
    Dependency(key,"sound",Fixed(sound->chSoundName4,sizeof(sound->chSoundName4)));
}
static Catalog Read(const char* path,bool legacy,bool report,bool dependencies,bool padded=false){
    std::ifstream file(path,std::ios::binary);Require(bool(file),"property input");
    std::vector<char> raw((std::istreambuf_iterator<char>(file)),std::istreambuf_iterator<char>());
    Require(raw.size()>12&&raw.size()<=128*1024*1024,"property size");
    CNtlSerializer input;input.In(raw.data(),int(raw.size()));
    unsigned version=0,count=0;std::string meshPath;input>>version;input>>meshPath;input>>count;
    Require(version==(legacy?0u:1u)&&count>0&&count<=100000,"explicit object property version/count");
    CNtlPLObjectProperty::m_strVer=version;CNtlPLObjectProperty::m_strMeshPath=meshPath;
    Catalog catalog;catalog.version=version;catalog.path=meshPath;
    if(report){printf("HEADER\t%u\t%u\t",version,count);Hex(meshPath.c_str());printf("\n");}
    std::set<std::string> names;
    for(unsigned i=0;i<count;++i){
        int start=input.GetStartPointer();CNtlPLObjectProperty property;property.LoadSerialize(input);
        int end=input.GetStartPointer();Require(end>start&&end<=int(raw.size()),"native row boundary");
        Require(property.GetName()!=NULL,"native property key");names.insert(property.GetName());
        CNtlSerializer saved;property.SaveSerialize(saved);
        Require(saved.GetDataSize()==end-start&&memcmp(saved.GetData(),raw.data()+start,end-start)==0,"native byte-exact row roundtrip");
        RwBBox* box=property.GetBBox();
        for(RwV3d v:{box->inf,box->sup})Require(std::isfinite(v.x)&&std::isfinite(v.y)&&std::isfinite(v.z),"finite property bbox");
        catalog.rows.push_back({property.GetName(),std::vector<char>(saved.GetData(),saved.GetData()+saved.GetDataSize())});
        if(report){printf("PROPERTY\t");Hex(property.GetName());printf("\t");Hex(property.GetDffName());printf("\t");Hex(property.m_strCollisionMeshName.c_str());
        printf("\t%u\t%d\t%d\t%d\t%.9g\t%.9g\t%.9g\t%.9g\t%.9g\t%.9g\n",property.GetFlag(),property.GetCollision(),property.IsNaviPress()!=0,
               int(property.GetAnimTable()->GetTypeAnimMap()->size()),box->inf.x,box->inf.y,box->inf.z,box->sup.x,box->sup.y,box->sup.z);}
        if(dependencies){
            const char* key=property.GetName();
            Dependency(key,"legacy-animation",property.m_strAnimFileName);
            Dependency(key,"uv-animation",property.m_strUVAnimFileName);
            Sound(key,property.GetSoundProp());
            for(auto effect:property.m_vLinkEffect){Require(effect!=NULL,"link effect");Dependency(key,"effect",Fixed(effect->chEffectName,sizeof(effect->chEffectName)));}
            for(auto pair:*property.GetAnimTable()->GetTypeAnimMap()){
                STypeAnimData* anim=pair.second;Require(anim!=NULL,"animation data");
                Dependency(key,"animation",property.GetAnimTable()->GetAnimPath()+anim->strAnimName);
                for(auto event:anim->vecAnimEvent){Require(event!=NULL,"animation event");
                    if(event->eEventID==EVENT_ANIM_VISUAL_SOUND)Sound(key,(SEventSound*)event);
                    else if(event->eEventID==EVENT_ANIM_VISUAL_EFFECT)Dependency(key,"effect",Fixed(((SEventVisualEffect*)event)->chEffectName,MAX_DEFAULT_NAME));
                    else if(event->eEventID==EVENT_ANIM_LINK_EFFECT)Dependency(key,"effect",Fixed(((SEventLinkEffect*)event)->chEffectName,MAX_DEFAULT_NAME));
                    else Dependency(key,"unresolved-event",std::to_string(int(event->eEventID)));
                }
            }
        }
    }
    if(padded){
        // Padded property DES has no size envelope. Native row consumption,
        // not a scan backwards over legitimate zero fields, defines its EOF.
        int consumed=input.GetStartPointer(),padding=input.GetDataSize();
        Require(!legacy && raw.size()%8==0 && padding==8-consumed%8,"Padded DES padding length");
        for(int i=0;i<padding;++i)Require(raw[size_t(consumed+i)]==0,"Padded DES zero padding");
        fprintf(stderr,"PASS Padded native EOF=%d DES padding=%d; no container conversion\n",consumed,padding);
    } else Require(input.GetDataSize()==0,"complete property metadata EOF");
    fprintf(stderr,"PASS native object properties=%u; byte-exact roundtrip; no models rendered\n",count);return catalog;
}
static std::vector<char> OwnedRow(const std::vector<char>& raw){
    CNtlSerializer input;input.In(raw.data(),int(raw.size()));CNtlPLObjectProperty property;property.LoadSerialize(input);
    Require(input.GetDataSize()==0&&property.m_strCollisionMeshName.empty(),"complete Owned row with no unported collision mesh");
    std::string name=property.GetName(),dff=property.GetDffName(),anim=property.GetAnimTable()->GetAnimPath();
    std::string legacy=property.m_strAnimFileName,uv=property.m_strUVAnimFileName;
    Require(!name.empty()&&name.size()<100&&dff.size()<128,"bounded Owned property binding");
    for(char c:name)Require(isalnum((unsigned char)c)||c=='_',"literal Owned property key");
    Require(dff.find_first_of("/\\:")==std::string::npos,"source DFF basename only");
    std::string alias="HF_"+name,mesh="OWNED\\"+alias+".DFF",animationPath=".\\OBJECT\\OWNED\\ANIMATION\\";
    auto animationFile=[&](const std::string& value){
        if(value.empty())return value;size_t at=value.find_last_of("/\\");std::string file=at==std::string::npos?value:value.substr(at+1);
        Require(!file.empty()&&file.find_first_of(":\r\n") == std::string::npos,"literal legacy animation basename");return animationPath+file;
    };
    property.SetName(alias.c_str());property.SetDffName(mesh.c_str());property.GetAnimTable()->SetAnimPath(animationPath);
    property.m_strAnimFileName=animationFile(legacy);property.m_strUVAnimFileName=animationFile(uv);
    CNtlSerializer saved;property.SaveSerialize(saved);std::vector<char> result(saved.GetData(),saved.GetData()+saved.GetDataSize());
    // Reverting only the explicit bindings must reproduce every source byte.
    property.SetName(name.c_str());property.SetDffName(dff.c_str());property.GetAnimTable()->SetAnimPath(anim);
    property.m_strAnimFileName=legacy;property.m_strUVAnimFileName=uv;CNtlSerializer restored;property.SaveSerialize(restored);
    Require(restored.GetDataSize()==int(raw.size())&&memcmp(restored.GetData(),raw.data(),raw.size())==0,"all non-binding Owned source bytes preserved");
    return result;
}
int main(int argc,char** argv){
    if(argc==6&&(strcmp(argv[1],"--merge-legacy")==0||strcmp(argv[1],"--merge-padded")==0||strcmp(argv[1],"--merge-ext-owned")==0)){
        bool padded=strcmp(argv[1],"--merge-padded")==0;
        bool ext=strcmp(argv[1],"--merge-ext-owned")==0;
        Catalog base=Read(argv[2],false,false,false),source=Read(argv[3],!padded&&!ext,false,false,padded);
        for(auto catalog:{&base,&source}){std::set<std::string> unique;for(auto& row:catalog->rows)Require(unique.insert(row.first).second,"duplicate merge property key");}
        Require(base.path==source.path,"matching mesh paths");
        std::ifstream keys(argv[4]);Require(bool(keys),"selection input");std::set<std::string> selected;std::string hex;
        while(std::getline(keys,hex)){
            Require(!hex.empty()&&hex.size()<=512&&hex.size()%2==0,"selection hex length");std::string key;
            for(size_t i=0;i<hex.size();i+=2){Require(isxdigit((unsigned char)hex[i])&&isxdigit((unsigned char)hex[i+1]),"selection hex");char pair[]={hex[i],hex[i+1],0};key.push_back(char(strtoul(pair,NULL,16)));}
            Require(key.find('\0')==std::string::npos&&selected.insert(key).second,"unique nonempty selection");
        }
        Require(!selected.empty()&&selected.size()<=1000,"selection count");
        Require(!ext||(selected.size()==60&&source.rows.size()==4547),"exact Owned selected property count");
        std::set<std::string> targetKeys;for(auto& key:selected)targetKeys.insert(ext?"HF_"+key:key);
        for(auto& row:base.rows)Require(targetKeys.count(row.first)==0,"existing baseline key");
        CNtlSerializer output;unsigned version=1,count=unsigned(base.rows.size()+selected.size());output<<version;output<<base.path;output<<count;
        for(auto& row:base.rows)output.In(row.second.data(),int(row.second.size()));
        size_t added=0;for(auto& row:source.rows)if(selected.count(row.first)){
            auto bytes=ext?OwnedRow(row.second):row.second;output.In(bytes.data(),int(bytes.size()));++added;
        }
        Require(added==selected.size(),"all selected source keys present");Require(output.GetDataSize()<=128*1024*1024,"merged size");
        std::ifstream existing(argv[5]);Require(!existing,"new merge output required");
        std::ofstream target(argv[5],std::ios::binary);Require(bool(target),"merge output");target.write(output.GetData(),output.GetDataSize());target.close();Require(bool(target),"complete merge write");
        Catalog check=Read(argv[5],false,false,false);Require(check.rows.size()==count,"merged count");
        for(size_t i=0;i<base.rows.size();++i)Require(check.rows[i]==base.rows[i],"every baseline row preserved");
        fprintf(stderr,"PASS merge baseline=%u added=%u; native header version 1; baseline bytes preserved\n",unsigned(base.rows.size()),unsigned(added));return 0;
    }
    bool legacy=false,dependencies=false,padded=false;if(argc<2||argc>4)return 2;
    for(int i=2;i<argc;++i){if(strcmp(argv[i],"--legacy-v0")==0)legacy=true;else if(strcmp(argv[i],"--dependencies")==0)dependencies=true;else if(strcmp(argv[i],"--padded-v1")==0)padded=true;else return 2;}
    if(legacy&&padded)return 2;
    Read(argv[1],legacy,true,dependencies,padded);return 0;
}
