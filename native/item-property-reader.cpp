#include "precomp_ntlpresentation.h"
#include "NtlPLItemProperty.h"
#include "NtlSerializer.h"
#include <fstream>
#include <iterator>
#include <vector>
#include <set>
#include <map>
#include <sstream>
#include <cstdio>
#include <cstdlib>

static void Require(bool ok,const char* why){if(!ok){fprintf(stderr,"FAIL %s\n",why);exit(1);}}
static void Hex(const std::string& value){for(unsigned char c:value)printf("%02x",c);}
struct Row {std::string name;std::vector<char> bytes;};
struct Catalog {unsigned version;std::string path;std::vector<Row> rows;std::vector<char> prefix;};
enum class InputFormat { TaiwanV2, LegacyV0, PaddedV2 };

// One native parser serves Taiwan, Legacy, Padded inspection, and both merge paths.
static std::string Fixed(const char* value,std::size_t bytes){
    const char* end=static_cast<const char*>(memchr(value,0,bytes));
    Require(end!=NULL,"terminated item dependency");return std::string(value,end);
}
static void Dependency(const std::string& key,const char* kind,int index,const std::string& value,
                       const std::string& bone="",bool attach=false){
    if(value.empty())return;
    printf("DEPENDENCY\t");Hex(key);printf("\t%s\t%d\t",kind,index);Hex(value);
    printf("\t");Hex(bone);printf("\t%d\n",attach?1:0);
}
static Catalog Read(const char* path,InputFormat format,bool report,bool dependencies=false){
    std::ifstream file(path,std::ios::binary);Require(bool(file),"property input");
    std::vector<char> raw((std::istreambuf_iterator<char>(file)),std::istreambuf_iterator<char>());
    Require(raw.size()>12&&raw.size()<=128*1024*1024,"property size");
    CNtlSerializer input;input.In(raw.data(),int(raw.size()));Catalog catalog;unsigned count=0;
    input>>catalog.version;input>>catalog.path;input>>count;
    const unsigned expectedVersion=format==InputFormat::LegacyV0?0u:2u;
    Require(catalog.version==expectedVersion&&count>0&&count<=30000,"explicit item property version/count");
    CNtlPLItemProperty::m_strVer=catalog.version;CNtlPLItemProperty::m_strItemDataPath=catalog.path;
    if(report){printf("HEADER\t%u\t%u\t",catalog.version,count);Hex(catalog.path);puts("");}
    catalog.prefix.assign(raw.begin(),raw.begin()+input.GetStartPointer());
    std::set<std::string> names;
    for(unsigned i=0;i<count;++i){
        int start=input.GetStartPointer();CNtlPLItemProperty property;property.LoadSerialize(input);int end=input.GetStartPointer();
        Require(end>start&&end<=int(raw.size()),"native row boundary");
        std::string name=property.GetName();Require(!name.empty()&&name.size()<=256&&names.insert(name).second,"unique nonempty item key");
        CNtlSerializer saved;property.SaveSerialize(saved);
        Require(saved.GetDataSize()==end-start&&!memcmp(saved.GetData(),raw.data()+start,end-start),"native byte-exact row roundtrip");
        catalog.rows.push_back({name,std::vector<char>(saved.GetData(),saved.GetData()+saved.GetDataSize())});
        if(report){printf("PROPERTY\t");Hex(name);printf("\t");Hex(property.GetMeshPath());printf("\t");Hex(property.GetMeshFileName());
            printf("\t%d\t%d\t%u\n",int(property.GetResType()),int(property.GetEquipSlotType()),unsigned(property.GetAnimTable()->GetTypeAnimMap()->size()));}
        if(report&&dependencies){
            for(std::size_t n=0;n<property.m_vLinkEffect.size();++n){
                auto effect=property.m_vLinkEffect[n];Require(effect!=NULL,"item link effect");
                Dependency(name,"link-effect",int(n),Fixed(effect->chEffectName,sizeof(effect->chEffectName)),
                           Fixed(effect->chBoneName,sizeof(effect->chBoneName)),effect->bAttachBone!=FALSE);
            }
            auto upgrade=property.GetUpgradeEffectProperty();
            for(int n=0;n<MAX_UPGRADE_EFFECT_COUNT;++n)
                Dependency(name,"upgrade-effect",n,Fixed(upgrade->szEffect[n],sizeof(upgrade->szEffect[n])));
            Dependency(name,"trace-texture",-1,Fixed(property.m_eventTrace.strTexture,sizeof(property.m_eventTrace.strTexture)));
            for(auto& pair:*property.GetAnimTable()->GetTypeAnimMap()){
                Require(pair.second!=NULL,"item animation");
                Dependency(name,"animation",int(pair.first),pair.second->strAnimName);
            }
        }
    }
    if(format==InputFormat::PaddedV2){
        const int consumed=input.GetStartPointer();
        const std::size_t padding=8u-(static_cast<std::size_t>(consumed)%8u);
        Require(static_cast<std::size_t>(input.GetDataSize())==padding,"Padded trailing DES padding length");
        for(std::size_t i=0;i<padding;++i)
            Require(raw[static_cast<std::size_t>(consumed)+i]==0,"Padded trailing DES padding must be zero");
        fprintf(stderr,"PASS Padded DES padding version=2 rows=%u consumed=%d padding=%u; byte-exact rows\n",
            unsigned(catalog.rows.size()),consumed,unsigned(padding));
    }else{
        Require(input.GetDataSize()==0,"complete item property EOF");
    }
    return catalog;
}

static void Merge(const char* baselinePath,const char* sourcePath,const char* keysPath,
                  const char* targetPath,InputFormat sourceFormat){
    Catalog baseline=Read(baselinePath,InputFormat::TaiwanV2,false);
    Catalog source=Read(sourcePath,sourceFormat,false);
    std::ifstream keys(keysPath);Require(bool(keys),"keys input");
    std::set<std::string> selected;std::string key;
    while(std::getline(keys,key)){
        if(!key.empty()&&key.back()=='\r')key.pop_back();
        Require(!key.empty()&&selected.insert(key).second,"unique keys");
    }
    Require(!selected.empty()&&selected.size()<=1000,"selection limit");
    for(const auto& row:baseline.rows)Require(!selected.count(row.name),"no native replacement");
    std::vector<Row> added;
    for(const auto& row:source.rows)if(selected.count(row.name))added.push_back(row);
    Require(added.size()==selected.size(),"all source keys exist");

    std::ifstream exists(targetPath,std::ios::binary);Require(!exists.good(),"new output required");
    CNtlSerializer output;output<<baseline.version;output<<baseline.path;
    output<<unsigned(baseline.rows.size()+added.size());
    for(const auto& row:baseline.rows)output.In(row.bytes.data(),int(row.bytes.size()));
    for(const auto& row:added)output.In(row.bytes.data(),int(row.bytes.size()));
    std::ofstream target(targetPath,std::ios::binary);Require(bool(target),"output");
    target.write(output.GetData(),output.GetDataSize());target.close();Require(bool(target),"complete output write");

    Catalog check=Read(targetPath,InputFormat::TaiwanV2,false);
    Require(check.rows.size()==baseline.rows.size()+added.size(),"merged count");
    for(std::size_t i=0;i<baseline.rows.size();++i)
        Require(check.rows[i].name==baseline.rows[i].name&&check.rows[i].bytes==baseline.rows[i].bytes,
                "native baseline rows preserved");
    for(std::size_t i=0;i<added.size();++i){
        const std::size_t at=baseline.rows.size()+i;
        Require(check.rows[at].name==added[i].name&&check.rows[at].bytes==added[i].bytes,
                "native selected rows preserved");
    }
    fprintf(stderr,"PASS native Taiwan v2 output rows=%u; baseline rows byte-exact; selected rows byte-exact; no padding\n",
        unsigned(check.rows.size()));
    printf("MERGED\t%u\t%u\n",unsigned(baseline.rows.size()),unsigned(added.size()));
}


struct MeshAlias {std::string key,from,to;};
static bool Literal(const std::string& name,bool filename){
    if(name.empty()||name.size()>120)return false;
    for(unsigned char c:name)if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'||(filename&&c=='.')))return false;
    if(filename)return name.size()>4&&!_stricmp(name.c_str()+name.size()-4,".dff")&&name.find("..") == std::string::npos;
    return true;
}
static void AliasNew(const char* baselinePath,const char* candidatePath,const char* mappingPath,const char* targetPath){
    Catalog baseline=Read(baselinePath,InputFormat::TaiwanV2,false), candidate=Read(candidatePath,InputFormat::TaiwanV2,false);
    Require(baseline.version==candidate.version&&baseline.path==candidate.path&&candidate.rows.size()>baseline.rows.size(),"new-property candidate lineage");
    for(std::size_t i=0;i<baseline.rows.size();++i)
        Require(baseline.rows[i].name==candidate.rows[i].name&&baseline.rows[i].bytes==candidate.rows[i].bytes,"all baseline rows intact before aliases");
    std::ifstream map(mappingPath);Require(bool(map),"alias mapping");std::vector<MeshAlias> aliases;std::set<std::string> keys,toNames;std::string line;
    while(std::getline(map,line)){
        if(!line.empty()&&line.back()=='\r')line.pop_back();
        std::size_t first=line.find('\t'), second=first==std::string::npos?first:line.find('\t',first+1);
        Require(first!=std::string::npos&&second!=std::string::npos&&line.find('\t',second+1)==std::string::npos,"three literal alias columns");
        MeshAlias a={line.substr(0,first),line.substr(first+1,second-first-1),line.substr(second+1)};
        Require(Literal(a.key,false)&&Literal(a.from,true)&&Literal(a.to,true)&&_stricmp(a.from.c_str(),a.to.c_str()),"literal non-noop mesh alias");
        std::string folded=a.to;for(char& c:folded)if(c>='A'&&c<='Z')c+=32;
        Require(keys.insert(a.key).second&&toNames.insert(folded).second&&aliases.size()<64,"unique bounded aliases");aliases.push_back(a);
    }
    Require(!aliases.empty(),"explicit alias selection");
    for(const auto& row:baseline.rows)Require(!keys.count(row.name),"baseline property alias forbidden");
    for(const auto& a:aliases){
        unsigned found=0;
        for(const auto& row:candidate.rows){
            CNtlSerializer input;input.In(row.bytes.data(),int(row.bytes.size()));CNtlPLItemProperty property;property.LoadSerialize(input);
            Require(_stricmp(property.GetMeshFileName().c_str(),a.to.c_str()),"alias target already referenced");
            if(row.name==a.key){++found;Require(property.GetMeshFileName()==a.from,"exact original mesh filename");}
        }
        Require(found==1,"selected added property exists exactly once");
    }
    std::vector<Row> changed=candidate.rows;
    for(std::size_t i=baseline.rows.size();i<changed.size();++i){
        auto& row=changed[i];for(const auto& a:aliases)if(row.name==a.key){
            CNtlSerializer input;input.In(row.bytes.data(),int(row.bytes.size()));CNtlPLItemProperty property;property.LoadSerialize(input);
            property.SetMeshFileName(a.to);CNtlSerializer saved;property.SaveSerialize(saved);
            property.SetMeshFileName(a.from);CNtlSerializer restored;property.SaveSerialize(restored);
            Require(restored.GetDataSize()==int(row.bytes.size())&&!memcmp(restored.GetData(),row.bytes.data(),row.bytes.size()),"only native mesh field changed");
            row.bytes.assign(saved.GetData(),saved.GetData()+saved.GetDataSize());
        }
    }
    std::ifstream exists(targetPath,std::ios::binary);Require(!exists.good(),"new alias output");
    std::ofstream output(targetPath,std::ios::binary);Require(bool(output),"alias output file");
    output.write(candidate.prefix.data(),candidate.prefix.size());for(const auto& row:changed)output.write(row.bytes.data(),row.bytes.size());
    output.close();Require(bool(output),"complete alias output");
    Catalog check=Read(targetPath,InputFormat::TaiwanV2,false);
    Require(check.prefix==candidate.prefix&&check.rows.size()==changed.size(),"header/count preserved");
    for(std::size_t i=0;i<changed.size();++i)Require(check.rows[i].name==changed[i].name&&check.rows[i].bytes==changed[i].bytes,"native alias EOF roundtrip and row identity");
    fprintf(stderr,"PASS native new-property mesh aliases=%u baseline=%u rows=%u; header and every other field preserved\n",unsigned(aliases.size()),unsigned(baseline.rows.size()),unsigned(check.rows.size()));
    printf("ALIASED\t%u\t%u\n",unsigned(baseline.rows.size()),unsigned(aliases.size()));
}

// Scoped weapon upgrade edits. Both the selected whole row and every original
// effect slot must match the native Padded source before changing any field.
struct UpgradeAlias {std::string key,from,to;int slot;};
static void AliasUpgrade(const char* baselinePath,const char* paddedPath,const char* mappingPath,const char* targetPath){
    Catalog baseline=Read(baselinePath,InputFormat::TaiwanV2,false), padded=Read(paddedPath,InputFormat::PaddedV2,false);
    std::ifstream map(mappingPath);Require(bool(map),"upgrade mapping");std::vector<UpgradeAlias> aliases;std::set<std::pair<std::string,int>> slots;std::string line;
    while(std::getline(map,line)){
        if(!line.empty()&&line.back()=='\r')line.pop_back();
        std::size_t a=line.find('\t'),b=a==std::string::npos?a:line.find('\t',a+1),c=b==std::string::npos?b:line.find('\t',b+1);
        Require(a!=std::string::npos&&b!=std::string::npos&&c!=std::string::npos&&line.find('\t',c+1)==std::string::npos,"four upgrade columns");
        std::string index=line.substr(a+1,b-a-1);Require(!index.empty()&&index.size()<4,"bounded slot");
        for(char ch:index)Require(ch>='0'&&ch<='9',"decimal slot");
        UpgradeAlias e={line.substr(0,a),line.substr(b+1,c-b-1),line.substr(c+1),atoi(index.c_str())};
        Require(e.key.find("W_SKIN_")==0&&Literal(e.key,false)&&Literal(e.from,false)&&Literal(e.to,false)&&e.from!=e.to&&
            e.from.size()<32&&e.to.size()<32&&e.slot>=0&&e.slot<MAX_UPGRADE_EFFECT_COUNT,"scoped native weapon upgrade alias");
        Require(aliases.size()<1024&&slots.insert({e.key,e.slot}).second,"unique bounded upgrade slots");aliases.push_back(e);
    }
    Require(!aliases.empty(),"explicit upgrade edits");std::set<std::string> selected;
    for(const auto& e:aliases)selected.insert(e.key);Require(selected.size()<=64,"bounded weapon keys");
    std::vector<Row> changed=baseline.rows;
    for(auto& row:changed)if(selected.count(row.name)){
        auto source=std::find_if(padded.rows.begin(),padded.rows.end(),[&](const Row& r){return r.name==row.name;});
        Require(source!=padded.rows.end()&&source->bytes==row.bytes,"selected whole property matches Padded preimage");
        CNtlSerializer input;input.In(row.bytes.data(),int(row.bytes.size()));CNtlPLItemProperty property;property.LoadSerialize(input);
        auto upgrade=property.GetUpgradeEffectProperty();
        for(const auto& e:aliases)if(e.key==row.name){
            Require(Fixed(upgrade->szEffect[e.slot],sizeof(upgrade->szEffect[e.slot]))==e.from,"exact upgrade slot preimage");
            memset(upgrade->szEffect[e.slot],0,sizeof(upgrade->szEffect[e.slot]));memcpy(upgrade->szEffect[e.slot],e.to.data(),e.to.size());
        }
        CNtlSerializer saved;property.SaveSerialize(saved);
        for(const auto& e:aliases)if(e.key==row.name){
            memset(upgrade->szEffect[e.slot],0,sizeof(upgrade->szEffect[e.slot]));memcpy(upgrade->szEffect[e.slot],e.from.data(),e.from.size());
        }
        CNtlSerializer restored;property.SaveSerialize(restored);
        Require(restored.GetDataSize()==int(row.bytes.size())&&!memcmp(restored.GetData(),row.bytes.data(),row.bytes.size()),"outside upgrade fields preserved");
        Require(saved.GetDataSize()==int(row.bytes.size()),"fixed-width upgrade output");row.bytes.assign(saved.GetData(),saved.GetData()+saved.GetDataSize());selected.erase(row.name);
    }
    Require(selected.empty(),"every selected native weapon exists");
    std::ifstream exists(targetPath,std::ios::binary);Require(!exists.good(),"new upgrade output");
    std::ofstream output(targetPath,std::ios::binary);Require(bool(output),"upgrade output");output.write(baseline.prefix.data(),baseline.prefix.size());
    for(const auto& r:changed)output.write(r.bytes.data(),r.bytes.size());output.close();Require(bool(output),"complete upgrade output");
    Catalog check=Read(targetPath,InputFormat::TaiwanV2,false);Require(check.prefix==baseline.prefix&&check.rows.size()==changed.size(),"upgrade header/count");
    for(std::size_t i=0;i<changed.size();++i)Require(check.rows[i].name==changed[i].name&&check.rows[i].bytes==changed[i].bytes,"native upgrade output row identity");
    printf("UPGRADE_ALIASED\t%u\t%u\n",unsigned(baseline.rows.size()),unsigned(aliases.size()));
}

// Only rows absent from the pinned baseline may receive these effect aliases.
// The managed service proves their additive alias lineage and EFF definitions.
static void AliasOwnedEffects(const char* candidatePath,const char* baselinePath,const char* mapPath,const char* targetPath){
    Catalog candidate=Read(candidatePath,InputFormat::TaiwanV2,false),baseline=Read(baselinePath,InputFormat::TaiwanV2,false);
    Require(candidate.rows.size()>baseline.rows.size(),"owned effect aliases require additive rows");
    std::set<std::string> protectedKeys;for(std::size_t i=0;i<baseline.rows.size();++i){
        Require(candidate.rows[i].name==baseline.rows[i].name&&candidate.rows[i].bytes==baseline.rows[i].bytes,"every baseline row preserved before owned effects");
        protectedKeys.insert(baseline.rows[i].name);
    }
    struct Edit {std::string key,kind,from,to;int slot;std::vector<char> original;};
    std::vector<Edit> edits;std::set<std::string> identities,selected;std::ifstream mapping(mapPath);Require(bool(mapping),"owned effect map");std::string line;
    while(std::getline(mapping,line)){
        if(!line.empty()&&line.back()=='\r')line.pop_back();std::stringstream columns(line);std::string key,kind,slot,from,to,extra;
        Require(bool(std::getline(columns,key,'\t'))&&bool(std::getline(columns,kind,'\t'))&&bool(std::getline(columns,slot,'\t'))&&
            bool(std::getline(columns,from,'\t'))&&bool(std::getline(columns,to,'\t'))&&!std::getline(columns,extra,'\t'),"five owned effect columns");
        Require(Literal(key,false)&&!protectedKeys.count(key)&&(key.find("W_HF_")==0||key.find("HLS_HF_")==0)&&
            (kind=="upgrade-effect"||kind=="link-effect")&&!slot.empty()&&slot.size()<4&&Literal(from,false)&&Literal(to,false)&&from!=to&&to.find("HF_E_")==0,"owned effect selection");
        for(char c:slot)Require(c>='0'&&c<='9',"decimal owned slot");
        Require(edits.size()<1024&&identities.insert(key+"\t"+kind+"\t"+slot).second,"unique bounded owned slots");
        edits.push_back({key,kind,from,to,atoi(slot.c_str()),{}});selected.insert(key);
    }
    Require(!edits.empty()&&selected.size()<=64,"bounded owned effect edits");std::vector<Row> changed=candidate.rows;
    for(auto& row:changed)if(selected.count(row.name)){
        CNtlSerializer input;input.In(row.bytes.data(),int(row.bytes.size()));CNtlPLItemProperty property;property.LoadSerialize(input);Require(!input.GetDataSize(),"owned row EOF");
        auto field=[&](Edit& e)->std::pair<char*,std::size_t>{
            if(e.kind=="upgrade-effect"){
                auto upgrade=property.GetUpgradeEffectProperty();Require(e.slot>=0&&e.slot<MAX_UPGRADE_EFFECT_COUNT,"owned upgrade slot");
                return {upgrade->szEffect[e.slot],sizeof(upgrade->szEffect[e.slot])};
            }
            Require(e.slot>=0&&std::size_t(e.slot)<property.m_vLinkEffect.size()&&property.m_vLinkEffect[e.slot]!=NULL,"owned link slot");
            auto link=property.m_vLinkEffect[e.slot];return {link->chEffectName,sizeof(link->chEffectName)};
        };
        for(auto& e:edits)if(e.key==row.name){auto value=field(e);Require(Fixed(value.first,value.second)==e.from&&e.to.size()<value.second,"owned effect exact preimage and width");
            e.original.assign(value.first,value.first+value.second);memset(value.first,0,value.second);memcpy(value.first,e.to.data(),e.to.size());}
        CNtlSerializer saved;property.SaveSerialize(saved);
        for(auto& e:edits)if(e.key==row.name){auto value=field(e);Require(e.original.size()==value.second,"owned field restore size");memcpy(value.first,e.original.data(),value.second);}
        CNtlSerializer restored;property.SaveSerialize(restored);Require(restored.GetDataSize()==int(row.bytes.size())&&!memcmp(restored.GetData(),row.bytes.data(),row.bytes.size()),"only selected owned effect fields changed");
        Require(saved.GetDataSize()==int(row.bytes.size()),"owned effects fixed field width");row.bytes.assign(saved.GetData(),saved.GetData()+saved.GetDataSize());selected.erase(row.name);
    }
    Require(selected.empty(),"all owned effect rows present");std::ifstream exists(targetPath,std::ios::binary);Require(!exists.good(),"new owned effect output");
    std::ofstream target(targetPath,std::ios::binary);Require(bool(target),"owned effect output");target.write(candidate.prefix.data(),candidate.prefix.size());
    for(const auto& row:changed)target.write(row.bytes.data(),row.bytes.size());target.close();Require(bool(target),"complete owned effect write");
    Catalog check=Read(targetPath,InputFormat::TaiwanV2,false);Require(check.prefix==candidate.prefix&&check.rows.size()==changed.size(),"owned effect header/count");
    for(std::size_t i=0;i<changed.size();++i)Require(check.rows[i].name==changed[i].name&&check.rows[i].bytes==changed[i].bytes,"owned effect native readback");
    printf("OWNED_EFFECTS_ALIASED\t%u\t%u\n",unsigned(check.rows.size()),unsigned(edits.size()));
}

// Add aliases to already native-compatible version2 rows. Renaming uses the
// same native loader/serializer; reset the explicitly selected strings and compare
// to the source row to prove that flags/events/slots/offsets are unchanged.
static void AddAliases(const char* baselinePath,const char* sourcePath,const char* mapPath,const char* targetPath,bool resources=false){
    Catalog baseline=Read(baselinePath,InputFormat::TaiwanV2,false),source=Read(sourcePath,InputFormat::TaiwanV2,false);
    struct Alias {std::string name,path,mesh,animation;};std::map<std::string,Alias> aliases;std::set<std::string> own;
    std::ifstream mappings(mapPath);Require(bool(mappings),"alias map input");std::string line;
    while(std::getline(mappings,line)){
        if(!line.empty()&&line.back()=='\r')line.pop_back();std::stringstream fields(line);std::string key,name,path,mesh,animation,extra;
        Require(bool(std::getline(fields,key,'\t'))&&bool(std::getline(fields,name,'\t'))&&bool(std::getline(fields,path,'\t'))&&bool(std::getline(fields,mesh,'\t'))&&
            (!resources||bool(std::getline(fields,animation,'\t')))&&!std::getline(fields,extra,'\t'),"exact explicit alias fields");
        auto ascii=[](const std::string& value){if(value.empty()||value.size()>256)return false;for(unsigned char c:value)if(c<33||c>126)return false;return true;};
        Require(ascii(key)&&ascii(name)&&ascii(path)&&ascii(mesh)&&key!=name&&own.insert(name).second&&aliases.emplace(key,Alias{name,path,mesh,animation}).second,"unique bounded ASCII source/own aliases");
    }
    for(const auto& a:aliases)if(resources&&a.second.animation!="-"){
        const auto& folder=a.second.animation;
        Require(!folder.empty()&&folder.size()<=256&&folder.find(':')==std::string::npos&&folder[0]!='/'&&folder[0]!='\\'&&
            folder.find("..") == std::string::npos&&(folder.back()=='/'||folder.back()=='\\'),"safe relative animation folder");
        for(unsigned char c:folder)Require(c>=33&&c<=126,"ASCII animation folder");
    }
    Require(!aliases.empty()&&aliases.size()<=1000,"alias count");for(const auto& row:baseline.rows)Require(!own.count(row.name),"own key absent in baseline");
    std::vector<Row> added;
    for(const auto& row:source.rows){const auto it=aliases.find(row.name);if(it==aliases.end())continue;
        CNtlSerializer input;input.In(row.bytes.data(),int(row.bytes.size()));CNtlPLItemProperty property;property.LoadSerialize(input);Require(!input.GetDataSize(),"source alias row EOF");
        const std::string originalPath=property.GetMeshPath(),originalMesh=property.GetMeshFileName(),originalAnimation=property.GetAnimTable()->GetAnimPath();
        const bool animationAlias=resources&&it->second.animation!="-";
        if(animationAlias){Require(!property.GetAnimTable()->GetTypeAnimMap()->empty()&&!originalAnimation.empty(),"existing native animations for folder alias");
            std::string folder=it->second.animation;property.GetAnimTable()->SetAnimPath(folder);}
        property.SetName(it->second.name.c_str());property.SetMeshPath(it->second.path);property.SetMeshFileName(it->second.mesh);
        CNtlSerializer changed;property.SaveSerialize(changed);std::vector<char> bytes(changed.GetData(),changed.GetData()+changed.GetDataSize());
        CNtlSerializer verify;verify.In(bytes.data(),int(bytes.size()));CNtlPLItemProperty check;check.LoadSerialize(verify);
        Require(!verify.GetDataSize()&&check.GetName()==it->second.name&&check.GetMeshPath()==it->second.path&&check.GetMeshFileName()==it->second.mesh,"native alias readback");
        Require(check.GetAnimTable()->GetAnimPath()==(animationAlias?it->second.animation:originalAnimation),"native animation folder readback");
        check.SetName(row.name.c_str());check.SetMeshPath(originalPath);check.SetMeshFileName(originalMesh);
        std::string restoredAnimation=originalAnimation;check.GetAnimTable()->SetAnimPath(restoredAnimation);
        CNtlSerializer restored;check.SaveSerialize(restored);
        Require(restored.GetDataSize()==int(row.bytes.size())&&!memcmp(restored.GetData(),row.bytes.data(),row.bytes.size()),"all source bytes outside explicit strings preserved");
        added.push_back({it->second.name,std::move(bytes)});
    }
    Require(added.size()==aliases.size(),"all source alias keys found");std::ifstream exists(targetPath,std::ios::binary);Require(!exists.good(),"new alias output required");
    CNtlSerializer output;output<<baseline.version;output<<baseline.path;output<<unsigned(baseline.rows.size()+added.size());
    for(const auto& row:baseline.rows)output.In(row.bytes.data(),int(row.bytes.size()));for(const auto& row:added)output.In(row.bytes.data(),int(row.bytes.size()));
    std::ofstream target(targetPath,std::ios::binary);Require(bool(target),"alias output");target.write(output.GetData(),output.GetDataSize());target.close();Require(bool(target),"alias complete write");
    Catalog round=Read(targetPath,InputFormat::TaiwanV2,false);Require(round.rows.size()==baseline.rows.size()+added.size(),"alias output count");
    for(std::size_t i=0;i<baseline.rows.size();++i)Require(round.rows[i].name==baseline.rows[i].name&&round.rows[i].bytes==baseline.rows[i].bytes,"baseline rows byte-exact");
    for(std::size_t i=0;i<added.size();++i)Require(round.rows[baseline.rows.size()+i].name==added[i].name&&round.rows[baseline.rows.size()+i].bytes==added[i].bytes,"added alias rows byte-exact");
    fprintf(stderr,"PASS native item alias output baseline=%u added=%u; baseline and all non-string source bytes preserved; no rendering\n",unsigned(baseline.rows.size()),unsigned(added.size()));
    printf("ALIASED\t%u\t%u\n",unsigned(baseline.rows.size()),unsigned(added.size()));
}

static void Resources(const char* path,const char* keysPath){
    Catalog catalog=Read(path,InputFormat::TaiwanV2,false);
    std::ifstream keys(keysPath);Require(bool(keys),"resource selection input");
    std::set<std::string> selected;std::string key;
    while(std::getline(keys,key)){if(!key.empty()&&key.back()=='\r')key.pop_back();Require(!key.empty()&&selected.insert(key).second,"unique selected resource key");}
    Require(!selected.empty()&&selected.size()<=1000,"bounded resource selection");unsigned found=0;
    printf("RESOURCES\t%u\t%u\n",unsigned(catalog.rows.size()),unsigned(selected.size()));
    for(const auto& row:catalog.rows){if(!selected.count(row.name))continue;++found;
        CNtlSerializer input;input.In(row.bytes.data(),int(row.bytes.size()));CNtlPLItemProperty property;property.LoadSerialize(input);Require(!input.GetDataSize(),"native resource row EOF");
        auto emit=[&](const char* kind,const std::string& name,const std::string& folder,unsigned animation=0){
            if(name.empty())return;printf("RESOURCE\t");Hex(row.name);printf("\t%s\t%u\t",kind,animation);Hex(folder);printf("\t");Hex(name);puts("");};
        emit("mesh",property.GetMeshFileName(),property.GetMeshPath());
        emit("trace-texture",Fixed(property.m_eventTrace.strTexture,sizeof(property.m_eventTrace.strTexture)),"");
        for(auto* link:property.m_vLinkEffect){Require(link!=NULL,"non-null native link effect");emit("link-effect",Fixed(link->chEffectName,sizeof(link->chEffectName)),"");}
        auto* upgrade=property.GetUpgradeEffectProperty();
        for(unsigned i=0;i<MAX_UPGRADE_EFFECT_COUNT;++i)emit("upgrade-effect",Fixed(upgrade->szEffect[i],sizeof(upgrade->szEffect[i])),"",i);
        for(const auto& animation:*property.GetAnimTable()->GetTypeAnimMap()){
            auto* data=animation.second;Require(data!=NULL,"non-null native animation");
            emit("animation",data->strAnimName,property.GetAnimTable()->GetAnimPath(),animation.first);
            for(auto* event:data->vecAnimEvent){Require(event!=NULL,"non-null native event");printf("EVENT\t");Hex(row.name);printf("\t%u\t%u\n",animation.first,unsigned(event->eEventID));}
        }
    }
    Require(found==selected.size(),"all selected properties exist");
    fprintf(stderr,"PASS native resource getters selected=%u fullRows=%u; events enumerated, not interpreted; no mutation/rendering\n",found,unsigned(catalog.rows.size()));
}

// Compare selected Taiwan v2 records without reducing them to visible getters.
// Each selected row is emitted in full hex so the caller can independently hash
// and retain the exact serialized bytes alongside the native reader result.
static void SelectedCompareV2(const char* sourcePath,const char* currentPath,const char* keysPath){
    Catalog source=Read(sourcePath,InputFormat::TaiwanV2,false),current=Read(currentPath,InputFormat::TaiwanV2,false);
    std::ifstream keys(keysPath);Require(bool(keys),"selected comparison keys");
    std::vector<std::string> selected;std::set<std::string> unique;std::string key;
    while(std::getline(keys,key)){
        if(!key.empty()&&key.back()=='\r')key.pop_back();
        Require(!key.empty()&&key.size()<=256&&unique.insert(key).second,"unique bounded selected comparison key");
        selected.push_back(key);
    }
    Require(!selected.empty()&&selected.size()<=64,"selected comparison count");
    std::map<std::string,const Row*> sourceRows,currentRows;
    for(const auto& row:source.rows)sourceRows.emplace(row.name,&row);
    for(const auto& row:current.rows)currentRows.emplace(row.name,&row);
    printf("SELECTED_COMPARE_V2\t%u\t%u\t%u\t%u\t",unsigned(source.version),unsigned(source.rows.size()),unsigned(current.version),unsigned(current.rows.size()));
    Hex(source.path);printf("\t");Hex(current.path);puts("");
    unsigned identical=0;
    for(const auto& name:selected){
        const auto s=sourceRows.find(name),c=currentRows.find(name);
        Require(s!=sourceRows.end()&&c!=currentRows.end(),"selected key exists in both native catalogs");
        const bool equal=s->second->bytes==c->second->bytes;if(equal)++identical;
        printf("ROW\t");Hex(name);printf("\t%u\t",unsigned(s->second->bytes.size()));Hex(std::string(s->second->bytes.begin(),s->second->bytes.end()));
        printf("\t%u\t",unsigned(c->second->bytes.size()));Hex(std::string(c->second->bytes.begin(),c->second->bytes.end()));
        printf("\t%d\n",equal?1:0);
    }
    fprintf(stderr,"PASS selected ItemProperty compare v2 selected=%u fullSource=%u fullCurrent=%u native row serialization and complete EOF; byteEqual=%u; no rendering\n",
        unsigned(selected.size()),unsigned(source.rows.size()),unsigned(current.rows.size()),identical);
}

int main(int argc,char** argv){
    Require(argc==2||argc==3||argc==4||argc==5||argc==6,"input [--legacy-v0|--padded-v2] [--dependencies] | --selected-compare-v2 source current keys | --merge baseline source keys output | --merge-padded baseline source keys output");
    if(argc==4&&!strcmp(argv[1],"--resources")){Resources(argv[2],argv[3]);return 0;}
    if(argc==5&&!strcmp(argv[1],"--selected-compare-v2")){SelectedCompareV2(argv[2],argv[3],argv[4]);return 0;}
    if(argc<6){
        bool legacy=false,padded=false,dependencies=false;
        for(int i=2;i<argc;++i){
            if(!strcmp(argv[i],"--legacy-v0")){Require(!legacy&&!padded,"single item format");legacy=true;}
            else if(!strcmp(argv[i],"--padded-v2")){Require(!legacy&&!padded,"single item format");padded=true;}
            else if(!strcmp(argv[i],"--dependencies")){Require(!dependencies,"single dependency flag");dependencies=true;}
            else Require(false,"explicit inspection mode flag");
        }
        const InputFormat format=legacy?InputFormat::LegacyV0:(padded?InputFormat::PaddedV2:InputFormat::TaiwanV2);
        Catalog catalog=Read(argv[1],format,true,dependencies);
        fprintf(stderr,"PASS native item properties=%u; byte-exact rows; no models rendered\n",unsigned(catalog.rows.size()));
        return 0;
    }
    if(!strcmp(argv[1],"--add-aliases"))AddAliases(argv[2],argv[3],argv[4],argv[5]);
    else if(!strcmp(argv[1],"--add-resource-aliases"))AddAliases(argv[2],argv[3],argv[4],argv[5],true);
    else if(!strcmp(argv[1],"--alias-upgrade"))AliasUpgrade(argv[2],argv[3],argv[4],argv[5]);
    else if(!strcmp(argv[1],"--alias-owned-effects"))AliasOwnedEffects(argv[2],argv[3],argv[4],argv[5]);
    else if(!strcmp(argv[1],"--alias-new"))AliasNew(argv[2],argv[3],argv[4],argv[5]);
    else if(!strcmp(argv[1],"--merge"))Merge(argv[2],argv[3],argv[4],argv[5],InputFormat::LegacyV0);
    else if(!strcmp(argv[1],"--merge-padded"))Merge(argv[2],argv[3],argv[4],argv[5],InputFormat::PaddedV2);
    else Require(false,"explicit merge mode");
    return 0;
}
