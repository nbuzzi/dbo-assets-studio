// Herramienta de pak Taiwan 2.0 (índice DES con MD5(clave del PAK, tomada de DBO_PAK_KEY),
// elementos {u8 unit; char name[128]; u32 size; u32 offset} (140 B con padding MSVC), payloads crudos en <prefijo><unit>.pak).
//   pak-tool extract <index.pak> <pack-dir> <out-root>          extrae todo (nombres tipo .\data\x.rdf bajo out-root)
//   pak-tool build <prefix> <root> <list.txt> <out-dir> [unitMaxMB]   empaqueta las rutas relativas de list.txt (relativas a root)
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <fstream>
#include <algorithm>
#include <unordered_map>
#include "NtlFileSerializer.h"
#include "md5.h"

// The PAK index key is NOT compiled into this program. It is read at runtime
// from the DBO_PAK_KEY environment variable (set by DBO Assets Studio from its
// local studio.config.json). See README.md.
static char* PakKey(){
    static std::string key;
    if(key.empty()){
        char* value=nullptr; size_t length=0;
        if(_dupenv_s(&value,&length,"DBO_PAK_KEY")!=0||value==nullptr||value[0]==0){
            fprintf(stderr,"PAK_KEY_MISSING: set pakKey in studio.config.json or the DBO_PAK_KEY variable\n");
            exit(10);
        }
        key=value; free(value);
    }
    return const_cast<char*>(key.c_str());
}
#define PAK_KEY PakKey()
struct Entry { unsigned char unit; char name[128]; unsigned int size, offset; };
static_assert(sizeof(Entry)==140, "Taiwan index ABI");
struct PackHeaderEntry { unsigned char unit; char name[128]; unsigned int size, offset; };
static_assert(sizeof(PackHeaderEntry)==140, "Taiwan resource pack header ABI");
static bool SafePath(std::string rel){
 if(rel.rfind(".\\",0)==0)rel=rel.substr(2);
 if(rel.empty() || rel[0]=='\\' || rel[0]=='/' || rel.find(':')!=std::string::npos)return false;
 for(size_t i=0;i<rel.size();){size_t j=rel.find_first_of("\\/",i);if(j==std::string::npos)j=rel.size();std::string part=rel.substr(i,j-i);if(part.empty()||part=="."||part=="..")return false;i=j+1;}
 return true;
}

static bool ReadAll(const std::string& p, std::vector<char>& b){ FILE*f=nullptr; if(fopen_s(&f,p.c_str(),"rb"))return false; _fseeki64(f,0,SEEK_END); long long n=_ftelli64(f); _fseeki64(f,0,SEEK_SET); b.resize((size_t)n); size_t r=n?fread(b.data(),1,b.size(),f):0; fclose(f); return r==b.size(); }
// Reserve physical units too: an unreferenced historical unit must never be
// overwritten when this candidate is later copied to the same pack directory.
static unsigned ReservedUnit(const std::string& index,unsigned highest){
 const size_t slash=index.find_last_of("/\\");
 const std::string folder=slash==std::string::npos?".":index.substr(0,slash);
 std::string prefix=index.substr(slash==std::string::npos?0:slash+1);prefix=prefix.substr(0,prefix.find_last_of('.'));
 WIN32_FIND_DATAA data;HANDLE find=FindFirstFileA((folder+"\\"+prefix+"*.pak").c_str(),&data);
 if(find==INVALID_HANDLE_VALUE)return highest;
 do {std::string name=data.cFileName;if(name.size()<=prefix.size()+4)continue;
  std::string digits=name.substr(prefix.size(),name.size()-prefix.size()-4);
  if(digits.find_first_not_of("0123456789")!=std::string::npos)continue;
  unsigned long value=strtoul(digits.c_str(),nullptr,10);if(value>255){highest=255;break;}
  highest=(std::max)(highest,(unsigned)value);
 }while(FindNextFileA(find,&data));FindClose(find);return highest;
}
static void MkDirs(const std::string& path){ std::string cur; for(size_t i=0;i<path.size();++i){ cur+=path[i]; if((path[i]=='\\'||path[i]=='/')&&cur.size()>3) CreateDirectoryA(cur.c_str(),nullptr); } }

static int Extract(const std::string& index,const std::string& packDir,const std::string& outRoot){
 CNtlFileSerializer s; MD5 md;
 if(!s.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY))){fprintf(stderr,"INDEX_DECRYPT_FAILED\n");return 3;}
 std::string prefix=index.substr(index.find_last_of("/\\")+1); prefix=prefix.substr(0,prefix.find_last_of('.'));
 unsigned entries=s.GetDataSize()/sizeof(Entry), ok=0, bad=0;
 std::vector<FILE*> units(256,nullptr);
 for(unsigned i=0;i<entries;++i){
  Entry e; s.Out(&e,sizeof(e));
  if(!memchr(e.name,0,128)){++bad;continue;}
  if(!units[e.unit]){ std::string up=packDir+"\\"+prefix+std::to_string(e.unit)+".pak"; fopen_s(&units[e.unit],up.c_str(),"rb"); if(!units[e.unit]){fprintf(stderr,"MISSING_UNIT %s\n",up.c_str());++bad;continue;} }
  std::string rel=e.name; if(rel.rfind(".\\",0)==0) rel=rel.substr(2);
  std::string dst=outRoot+"\\"+rel; MkDirs(dst);
  std::vector<char> b(e.size); _fseeki64(units[e.unit],e.offset,SEEK_SET);
  if(e.size && fread(b.data(),1,b.size(),units[e.unit])!=b.size()){fprintf(stderr,"SHORT_READ %s\n",e.name);++bad;continue;}
  FILE*g=nullptr; if(fopen_s(&g,dst.c_str(),"wb")){fprintf(stderr,"WRITE_FAIL %s\n",dst.c_str());++bad;continue;}
  if(e.size) fwrite(b.data(),1,b.size(),g); fclose(g); ++ok;
 }
 for(FILE*f:units) if(f) fclose(f);
 printf("%s extracted=%u errors=%u\n",prefix.c_str(),ok,bad); return bad?1:0;
}

static int Build(const std::string& prefix,const std::string& root,const std::string& list,const std::string& outDir,unsigned long long unitMax){
 std::ifstream in(list); if(!in){fprintf(stderr,"LIST_OPEN_FAILED\n");return 3;}
 CNtlFileSerializer s; std::string line; unsigned unit=0, count=0; unsigned long long offset=0; FILE*fp=nullptr;
 auto openUnit=[&](){ if(fp)fclose(fp); std::string p=outDir+"\\"+prefix+std::to_string(unit)+".pak"; fopen_s(&fp,p.c_str(),"wb"); return fp!=nullptr; };
 if(!openUnit()){fprintf(stderr,"UNIT_CREATE_FAILED\n");return 4;}
 while(std::getline(in,line)){
  while(!line.empty()&&(line.back()=='\r'||line.back()==' '))line.pop_back();
  if(line.empty())continue;
  std::string rel=line; for(char&c:rel){ if(c=='/')c='\\'; c=(char)tolower((unsigned char)c); }
  if(rel.rfind(".\\",0)==0) rel=rel.substr(2);
  std::vector<char> b; if(!ReadAll(root+"\\"+rel,b)){fprintf(stderr,"READ_FAIL %s\n",rel.c_str());return 5;}
  if(offset>0 && offset+b.size()>unitMax){ ++unit; offset=0; if(!openUnit()){fprintf(stderr,"UNIT_CREATE_FAILED\n");return 4;} }
  Entry e; memset(&e,0,sizeof(e)); e.unit=(unsigned char)unit; std::string name=".\\"+rel;
  if(name.size()>=128){fprintf(stderr,"NAME_TOO_LONG %s\n",name.c_str());return 6;}
  memcpy(e.name,name.c_str(),name.size()); e.size=(unsigned)b.size(); e.offset=(unsigned)offset;
  if(!b.empty()) fwrite(b.data(),1,b.size(),fp); offset+=b.size();
  s.In(&e,sizeof(e)); ++count;
 }
 if(fp)fclose(fp);
 MD5 md; std::string idx=outDir+"\\"+prefix+".pak";
 if(!s.SaveFile((char*)idx.c_str(),true,md.digestString(PAK_KEY))){fprintf(stderr,"INDEX_SAVE_FAILED\n");return 7;}
 printf("%s entries=%u units=%u\n",prefix.c_str(),count,unit+1); return 0;
}

// Validate decrypted index, unit boundaries and every payload against the source.
// No extraction or changes to either tree; useful before publishing a rebuilt pack.
static int Verify(const std::string& index,const std::string& packDir,const std::string& root){
 CNtlFileSerializer s; MD5 md;
 if(!s.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY)) || s.GetDataSize()%sizeof(Entry)>8) return 3;
 std::string prefix=index.substr(index.find_last_of("/\\")+1); prefix=prefix.substr(0,prefix.find_last_of('.'));
 unsigned entries=s.GetDataSize()/sizeof(Entry), ok=0;
 std::vector<FILE*> units(256,nullptr); bool failed=false;
 for(unsigned i=0;i<entries;++i){
  Entry e; s.Out(&e,sizeof(e));
  if(!memchr(e.name,0,128)){failed=true;break;}
  std::string rel=e.name; if(rel.rfind(".\\",0)==0) rel=rel.substr(2);
  if(!SafePath(rel)) {failed=true;break;}
  if(!units[e.unit]) {std::string up=packDir+"\\"+prefix+std::to_string(e.unit)+".pak"; fopen_s(&units[e.unit],up.c_str(),"rb");}
  std::vector<char> source, packed(e.size);
  if(!units[e.unit] || !ReadAll(root+"\\"+rel,source) || source.size()!=e.size){fprintf(stderr,"VERIFY_SOURCE %s\n",e.name);failed=true;break;}
  _fseeki64(units[e.unit],e.offset,SEEK_SET);
  if((e.size && fread(packed.data(),1,e.size,units[e.unit])!=e.size) || packed!=source){fprintf(stderr,"VERIFY_PAYLOAD %s\n",e.name);failed=true;break;}
  ++ok;
 }
 for(FILE*f:units) if(f) fclose(f);
 printf("%s verified=%u expected=%u errors=%u\n",prefix.c_str(),ok,entries,failed?1:0); return failed?1:0;
}

// Taiwan client indices are arrays of SFilePackHeaderElement (140 bytes), not the
// 140-byte serialized skill-script entry layout by coincidence: read the named
// header entries and fetch payloads from <prefix><unit>.pak.
static int ExtractPack(const std::string& index,const std::string& packDir,const std::string& outRoot){
 CNtlFileSerializer s; MD5 md;
 if(!s.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY)) || s.GetDataSize()%sizeof(PackHeaderEntry)>8){fprintf(stderr,"PACK_INDEX_INVALID\n");return 3;}
 std::string prefix=index.substr(index.find_last_of("/\\")+1); prefix=prefix.substr(0,prefix.find_last_of('.'));
 unsigned count=s.GetDataSize()/sizeof(PackHeaderEntry),ok=0,bad=0; std::vector<FILE*> units(256,nullptr);
 for(unsigned i=0;i<count;++i){PackHeaderEntry e;s.Out(&e,sizeof(e));if(!memchr(e.name,0,sizeof(e.name))){++bad;continue;}
  if(!units[e.unit]){std::string up=packDir+"\\"+prefix+std::to_string(e.unit)+".pak";fopen_s(&units[e.unit],up.c_str(),"rb");if(!units[e.unit]){fprintf(stderr,"MISSING_UNIT %s\n",up.c_str());++bad;continue;}}
  std::string rel=e.name;if(rel.rfind(".\\",0)==0)rel=rel.substr(2);if(!SafePath(rel)){fprintf(stderr,"UNSAFE_PATH %s\n",e.name);++bad;continue;}
  std::string dst=outRoot+"\\"+rel;MkDirs(dst);std::vector<char>b(e.size);_fseeki64(units[e.unit],e.offset,SEEK_SET);
  if(e.size&&fread(b.data(),1,b.size(),units[e.unit])!=b.size()){fprintf(stderr,"SHORT_READ %s\n",e.name);++bad;continue;}
  FILE*g=nullptr;if(fopen_s(&g,dst.c_str(),"wb")){fprintf(stderr,"WRITE_FAIL %s\n",dst.c_str());++bad;continue;}if(e.size)fwrite(b.data(),1,b.size(),g);fclose(g);++ok;
 }
 for(FILE*f:units)if(f)fclose(f);printf("%s extracted=%u errors=%u\n",prefix.c_str(),ok,bad);return bad?1:0;
}

// Extract matching Taiwan PAK resources without unpacking every texture unit.
// A basename matches case-insensitively; a path matches as a normalized relative path.
static int ExtractMatch(const std::string& index,const std::string& packDir,const std::string& outRoot,const std::string& query){
 if(query.empty()){fprintf(stderr,"MATCH_QUERY_EMPTY\n");return 2;}
 CNtlFileSerializer s; MD5 md;
 if(!s.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY)) || s.GetDataSize()%sizeof(PackHeaderEntry)>8){fprintf(stderr,"PACK_INDEX_INVALID\n");return 3;}
 std::string prefix=index.substr(index.find_last_of("/\\")+1);prefix=prefix.substr(0,prefix.find_last_of('.'));
 std::string q=query;for(char&c:q){if(c=='/')c='\\';c=(char)tolower((unsigned char)c);}
 std::vector<FILE*>units(256,nullptr);unsigned count=s.GetDataSize()/sizeof(PackHeaderEntry),matches=0,ok=0,bad=0;
 for(unsigned i=0;i<count;++i){
  PackHeaderEntry e;s.Out(&e,sizeof(e));
  if(!memchr(e.name,0,sizeof(e.name))){++bad;continue;}
  std::string rel=e.name;if(rel.rfind(".\\",0)==0)rel=rel.substr(2);
  if(!SafePath(rel)){++bad;continue;}
  std::string norm=rel;for(char&c:norm)c=(char)tolower((unsigned char)c);
  const size_t slash=norm.find_last_of('\\');const std::string leaf=slash==std::string::npos?norm:norm.substr(slash+1);
  if(q.find_first_of("\\")!=std::string::npos ? norm!=q : leaf!=q)continue;
  ++matches;
  if(!units[e.unit]){std::string up=packDir+"\\"+prefix+std::to_string(e.unit)+".pak";fopen_s(&units[e.unit],up.c_str(),"rb");if(!units[e.unit]){fprintf(stderr,"MISSING_UNIT %s\n",up.c_str());++bad;continue;}}
  std::string dst=outRoot+"\\"+rel;MkDirs(dst);
  std::vector<char>b(e.size);_fseeki64(units[e.unit],e.offset,SEEK_SET);
  if(e.size&&fread(b.data(),1,b.size(),units[e.unit])!=b.size()){fprintf(stderr,"SHORT_READ %s\n",e.name);++bad;continue;}
  FILE*g=nullptr;if(fopen_s(&g,dst.c_str(),"wb")){fprintf(stderr,"WRITE_FAIL %s\n",dst.c_str());++bad;continue;}
  if(e.size)fwrite(b.data(),1,b.size(),g);fclose(g);++ok;
 }
 for(FILE*f:units)if(f)fclose(f);
 printf("%s matches=%u extracted=%u errors=%u\n",prefix.c_str(),matches,ok,bad);
 return bad?1:(matches?0:10);
}


// Replace one payload by writing a new index and a new, unused unit in staging.
// Existing units may be hardlinks to read-only originals: never open them for write.
static int ReplaceEntry(const std::string& index,const std::string& name,const std::string& replacement,const std::string& outDir){
 CNtlFileSerializer input; MD5 md;
 if(!input.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY)) || input.GetDataSize()%sizeof(Entry)>8)return 3;
 std::vector<Entry> entries; unsigned maxUnit=0, matches=0; size_t found=0;
 const unsigned count=input.GetDataSize()/sizeof(Entry);
 for(unsigned i=0;i<count;++i){
  Entry e;input.Out(&e,sizeof(e));
  if(!memchr(e.name,0,sizeof(e.name)) || !SafePath(e.name))return 4;
  if(!_stricmp(e.name,name.c_str())){++matches;found=i;}
  maxUnit=(std::max)(maxUnit,(unsigned)e.unit);entries.push_back(e);
 }
 if(matches!=1 || maxUnit>=255){fprintf(stderr,"REPLACE_MATCHES %u max_unit=%u\n",matches,maxUnit);return 5;}
 std::vector<char> payload;
 if(!ReadAll(replacement,payload) || payload.size()>0xffffffffULL)return 6;
 std::string prefix=index.substr(index.find_last_of("/\\")+1);prefix=prefix.substr(0,prefix.find_last_of('.'));
 const std::string unit=outDir+"\\"+prefix+std::to_string(maxUnit+1)+".pak", target=outDir+"\\"+prefix+".pak";
 if(GetFileAttributesA(unit.c_str())!=INVALID_FILE_ATTRIBUTES || GetFileAttributesA(target.c_str())!=INVALID_FILE_ATTRIBUTES)return 7;
 FILE* f=nullptr;if(fopen_s(&f,unit.c_str(),"wb"))return 8;
 bool ok=payload.empty() || fwrite(payload.data(),1,payload.size(),f)==payload.size();
 if(fclose(f)!=0 || !ok)return 8;
 entries[found].unit=(unsigned char)(maxUnit+1);entries[found].offset=0;entries[found].size=(unsigned)payload.size();
 CNtlFileSerializer output;for(const Entry& e:entries)output.In(&e,sizeof(e));
 if(!output.SaveFile((char*)target.c_str(),true,md.digestString(PAK_KEY)))return 9;
 CNtlFileSerializer check;if(!check.LoadFile((char*)target.c_str(),true,md.digestString(PAK_KEY)))return 10;
 if(check.GetDataSize()/sizeof(Entry)!=count)return 10;
 for(const Entry& e:entries){Entry actual;check.Out(&actual,sizeof(actual));if(memcmp(&e,&actual,sizeof(e)))return 10;}
 printf("%s replaced=%s bytes=%u new_unit=%u preserved_entries=%u roundtrip=byte-exact\n",prefix.c_str(),name.c_str(),entries[found].size,maxUnit+1,count-1);return 0;
}

// Rebuild one fresh Taiwan texture unit and override a batch of existing or
// new texture paths in the encrypted index. Old units are left untouched.
static int ReplaceBatch(const std::string& index,const std::string& root,const std::string& list,const std::string& outDir){
 CNtlFileSerializer input; MD5 md;
 if(!input.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY)) || input.GetDataSize()%sizeof(Entry)>8){fprintf(stderr,"BATCH_INDEX_INVALID\n");return 3;}
 std::vector<Entry> entries; std::unordered_map<std::string,size_t> byName; unsigned maxUnit=0;
 const unsigned count=input.GetDataSize()/sizeof(Entry);
 for(unsigned i=0;i<count;++i){Entry e;input.Out(&e,sizeof(e));if(!memchr(e.name,0,sizeof(e.name))||!SafePath(e.name))return 4;
  std::string key=e.name;std::transform(key.begin(),key.end(),key.begin(),[](unsigned char c){return (char)tolower(c);});
  byName[key]=entries.size();entries.push_back(e);maxUnit=(std::max)(maxUnit,(unsigned)e.unit);
 }
 maxUnit=ReservedUnit(index,maxUnit);
 if(maxUnit>=255){fprintf(stderr,"BATCH_NO_FREE_UNIT max=%u\n",maxUnit);return 5;}
 std::ifstream in(list);if(!in){fprintf(stderr,"BATCH_LIST_OPEN_FAILED\n");return 6;}
 const unsigned newUnit=maxUnit+1;std::string prefix=index.substr(index.find_last_of("/\\")+1);prefix=prefix.substr(0,prefix.find_last_of('.'));
 const std::string unitPath=outDir+"\\"+prefix+std::to_string(newUnit)+".pak",target=outDir+"\\"+prefix+".pak";
 if(GetFileAttributesA(unitPath.c_str())!=INVALID_FILE_ATTRIBUTES||GetFileAttributesA(target.c_str())!=INVALID_FILE_ATTRIBUTES)return 7;
 FILE* unitFile=nullptr;if(fopen_s(&unitFile,unitPath.c_str(),"wb"))return 8;
 std::string line;unsigned replaced=0,added=0;unsigned long long offset=0;std::unordered_map<std::string,bool> seen;
 while(std::getline(in,line)){
  while(!line.empty()&&(line.back()=='\r'||line.back()==' '))line.pop_back();if(line.empty())continue;
  std::string rel=line;for(char&c:rel){if(c=='/')c='\\';c=(char)tolower((unsigned char)c);}if(rel.rfind(".\\",0)!=0)rel=".\\"+rel;
  if(!SafePath(rel)||rel.size()>=128){fclose(unitFile);fprintf(stderr,"BATCH_UNSAFE_PATH %s\n",rel.c_str());return 9;}
  std::string key=rel;if(!seen.emplace(key,true).second){fclose(unitFile);fprintf(stderr,"BATCH_DUPLICATE_PATH %s\n",rel.c_str());return 10;}
  std::string payloadPath=root+"\\"+rel.substr(2);std::vector<char> payload;
  if(!ReadAll(payloadPath,payload)||payload.size()>0xffffffffULL||offset+payload.size()>0xffffffffULL){fclose(unitFile);fprintf(stderr,"BATCH_READ_FAIL %s\n",payloadPath.c_str());return 11;}
  Entry e={};auto old=byName.find(key);if(old!=byName.end()){e=entries[old->second];++replaced;}else{memcpy(e.name,rel.c_str(),rel.size());++added;}
  e.unit=(unsigned char)newUnit;e.offset=(unsigned)offset;e.size=(unsigned)payload.size();
  if(payload.size()&&fwrite(payload.data(),1,payload.size(),unitFile)!=payload.size()){fclose(unitFile);return 12;}
  offset+=payload.size();
  if(old!=byName.end())entries[old->second]=e;else{byName[key]=entries.size();entries.push_back(e);}
 }
 if(fclose(unitFile)!=0||seen.empty())return 13;
 // Serialize the same contiguous ABI bytes once. The default serializer grows
 // in small increments; per-entry In() repeatedly copies huge terrain indices.
 if(entries.size()>0x7fffffffULL/sizeof(Entry))return 14;
 CNtlFileSerializer output;output.In(entries.data(),(int)(entries.size()*sizeof(Entry)));
 if(!output.SaveFile((char*)target.c_str(),true,md.digestString(PAK_KEY))){fprintf(stderr,"BATCH_INDEX_SAVE_FAILED\n");return 14;}
 CNtlFileSerializer check;if(!check.LoadFile((char*)target.c_str(),true,md.digestString(PAK_KEY)))return 15;
 const unsigned checkCount=check.GetDataSize()/sizeof(Entry);
 std::unordered_map<std::string,Entry> resolved;for(unsigned i=0;i<checkCount;++i){Entry e;check.Out(&e,sizeof(e));std::string k=e.name;std::transform(k.begin(),k.end(),k.begin(),[](unsigned char c){return (char)tolower(c);});resolved[k]=e;}
 FILE* verifyFile=nullptr;if(fopen_s(&verifyFile,unitPath.c_str(),"rb"))return 16;
 std::ifstream verifyList(list);unsigned verified=0;while(std::getline(verifyList,line)){while(!line.empty()&&(line.back()=='\r'||line.back()==' '))line.pop_back();if(line.empty())continue;std::string rel=line;for(char&c:rel){if(c=='/')c='\\';c=(char)tolower((unsigned char)c);}if(rel.rfind(".\\",0)!=0)rel=".\\"+rel;
  auto it=resolved.find(rel);if(it==resolved.end()||it->second.unit!=newUnit){fprintf(stderr,"BATCH_INDEX_VERIFY_FAIL path=%s found=%d unit=%u expected=%u\n",rel.c_str(),it!=resolved.end(),it!=resolved.end()?(unsigned)it->second.unit:999U,newUnit);fclose(verifyFile);return 17;}
  std::vector<char> src,packed(it->second.size);if(!ReadAll(root+"\\"+rel.substr(2),src)||src.size()!=it->second.size){fclose(verifyFile);return 18;}
  _fseeki64(verifyFile,it->second.offset,SEEK_SET);if((it->second.size&&fread(packed.data(),1,packed.size(),verifyFile)!=packed.size())||packed!=src){fclose(verifyFile);fprintf(stderr,"BATCH_VERIFY_FAIL %s\n",rel.c_str());return 19;}++verified;
 }
 fclose(verifyFile);printf("%s batch_replaced=%u batch_added=%u verified=%u unit=%u bytes=%llu\n",prefix.c_str(),replaced,added,verified,newUnit,offset);return 0;
}

// Validate an additive candidate without extracting/hashing the entire runtime.
// Unchanged headers must match the baseline byte for byte; every referenced unit
// and offset is bounded, and ReplaceBatch already roundtrips all changed payloads.
static int AuditOverlay(const std::string& baseline,const std::string& candidate,const std::string& packDir,const std::string& list){
 auto normalize=[](std::string name){for(char&c:name){if(c=='/')c='\\';c=(char)tolower((unsigned char)c);}if(name.rfind(".\\",0)!=0)name=".\\"+name;return name;};
 CNtlFileSerializer original,updated;MD5 md;
 if(!original.LoadFile((char*)baseline.c_str(),true,md.digestString(PAK_KEY)) || !updated.LoadFile((char*)candidate.c_str(),true,md.digestString(PAK_KEY)) || original.GetDataSize()%sizeof(Entry)>8 || updated.GetDataSize()%sizeof(Entry)>8)return 3;
 std::unordered_map<std::string,Entry> before,after;std::unordered_map<std::string,bool> changed;unsigned maxUnit=0;
 unsigned oldCount=original.GetDataSize()/sizeof(Entry),newCount=updated.GetDataSize()/sizeof(Entry);
 for(unsigned i=0;i<oldCount;++i){Entry e;original.Out(&e,sizeof(e));if(!memchr(e.name,0,128)||!SafePath(e.name)||!before.emplace(normalize(e.name),e).second)return 4;maxUnit=(std::max)(maxUnit,(unsigned)e.unit);}
 maxUnit=ReservedUnit(baseline,maxUnit);
 std::ifstream paths(list);if(!paths)return 5;std::string line;
 while(std::getline(paths,line)){if(!line.empty()&&line.back()=='\r')line.pop_back();if(line.empty())continue;if(!SafePath(line)||!changed.emplace(normalize(line),true).second)return 6;}
 std::string prefix=candidate.substr(candidate.find_last_of("/\\")+1);prefix=prefix.substr(0,prefix.find_last_of('.'));
 std::vector<long long> unitSizes(256,-1);unsigned preserved=0,modified=0;
 for(unsigned i=0;i<newCount;++i){
  Entry e;updated.Out(&e,sizeof(e));if(!memchr(e.name,0,128)||!SafePath(e.name))return 7;
  std::string name=normalize(e.name);if(!after.emplace(name,e).second)return 8;
  auto old=before.find(name);bool isChanged=changed.count(name)!=0;
  if(isChanged){if((unsigned)e.unit!=maxUnit+1)return 9;++modified;}
  else {if(old==before.end()||memcmp(&e,&old->second,sizeof(e)))return 10;++preserved;}
  if(unitSizes[e.unit]<0){std::string path=packDir+"\\"+prefix+std::to_string(e.unit)+".pak";FILE*f=nullptr;if(fopen_s(&f,path.c_str(),"rb"))return 11;_fseeki64(f,0,SEEK_END);unitSizes[e.unit]=_ftelli64(f);fclose(f);}
  if((unsigned long long)e.offset+e.size>(unsigned long long)unitSizes[e.unit])return 12;
 }
 for(const auto&row:before)if(!after.count(row.first))return 13;
 for(const auto&row:changed)if(!after.count(row.first))return 14;
 if(modified!=changed.size())return 15;
 printf("%s overlay_verified=%u unchanged_headers=%u changed_payloads=%u all_units_bounded=true\n",prefix.c_str(),newCount,preserved,modified);return 0;
}

// Export decrypted ABI entries for the read-only desktop browser. Payloads are
// read separately, with bounds checks, so large units are never unpacked wholesale.
static int ExportIndex(const char* index,const char* output){
 FILE* source=nullptr;if(fopen_s(&source,index,"rb"))return 3;
 _fseeki64(source,0,SEEK_END);auto size=_ftelli64(source);fclose(source);
 if(size<1 || size>128LL*1024*1024){fprintf(stderr,"INDEX_SIZE_LIMIT\n");return 3;}
 CNtlFileSerializer s;MD5 md;
 if(!s.LoadFile((char*)index,true,md.digestString(PAK_KEY)) || s.GetDataSize()%sizeof(Entry)>8){fprintf(stderr,"TAIWAN_INDEX_INVALID\n");return 4;}
 std::vector<Entry> entries;unsigned count=s.GetDataSize()/sizeof(Entry);
 for(unsigned i=0;i<count;++i){Entry e;s.Out(&e,sizeof(e));if(!memchr(e.name,0,128)||!SafePath(e.name)){fprintf(stderr,"UNSAFE_ENTRY\n");return 5;}entries.push_back(e);}
 FILE* f=nullptr;if(fopen_s(&f,output,"wb"))return 6;
 bool ok=entries.empty() || fwrite(entries.data(),sizeof(Entry),entries.size(),f)==entries.size();
 if(fclose(f)!=0 || !ok)return 7;
 printf("entries=%u ABI=140\n",count);return 0;
}

// Content Studio owns streaming payload writes; this boundary only serializes
// bounded ABI records through the same DES implementation as the Taiwan client.
static int ImportIndex(const char* raw,const char* target){
 WIN32_FILE_ATTRIBUTE_DATA info;
 if(!GetFileAttributesExA(raw,GetFileExInfoStandard,&info) || info.nFileSizeHigh || info.nFileSizeLow>128U*1024*1024)return 3;
 if(GetFileAttributesA(target)!=INVALID_FILE_ATTRIBUTES)return 4;
 std::vector<char> bytes;if(!ReadAll(raw,bytes)||bytes.empty()||bytes.size()%sizeof(Entry))return 5;
 CNtlFileSerializer s;std::unordered_map<std::string,bool> names;
 for(size_t p=0;p<bytes.size();p+=sizeof(Entry)){
  Entry e;memcpy(&e,bytes.data()+p,sizeof(e));
  if(!memchr(e.name,0,128)||!SafePath(e.name))return 6;
  std::string key=e.name;std::transform(key.begin(),key.end(),key.begin(),[](unsigned char c){return c>='A'&&c<='Z'?c+32:c;});
  if(!names.emplace(key,true).second)return 7;
 }
 // Validate the whole bounded index before serializing it once. In() grows its
 // buffer in small increments; per-entry calls make large terrain imports
 // quadratic and can exceed the Content Studio encryption deadline.
 s.In(bytes.data(),(int)bytes.size());
 MD5 md;if(!s.SaveFile((char*)target,true,md.digestString(PAK_KEY)))return 8;
 CNtlFileSerializer check;if(!check.LoadFile((char*)target,true,md.digestString(PAK_KEY))||check.GetDataSize()/sizeof(Entry)!=bytes.size()/sizeof(Entry))return 9;
 for(size_t p=0;p<bytes.size();p+=sizeof(Entry)){Entry e;check.Out(&e,sizeof(e));if(memcmp(&e,bytes.data()+p,sizeof(e)))return 10;}
 printf("entries=%zu ABI=140 roundtrip=byte-exact\n",bytes.size()/sizeof(Entry));return 0;
}

// Read-only listing: name, unit, offset, size of every index entry whose lowercase path contains <filter> (all when empty).
static int ListPack(const std::string& index,const std::string& filter){
 CNtlFileSerializer s; MD5 md;
 if(!s.LoadFile((char*)index.c_str(),true,md.digestString(PAK_KEY)) || s.GetDataSize()%sizeof(PackHeaderEntry)>8){fprintf(stderr,"PACK_INDEX_INVALID\n");return 3;}
 std::string f=filter;for(char&c:f){if(c=='/')c='\\';c=(char)tolower((unsigned char)c);}
 const unsigned count=s.GetDataSize()/sizeof(PackHeaderEntry);unsigned shown=0;
 for(unsigned i=0;i<count;++i){
  PackHeaderEntry e;s.Out(&e,sizeof(e));
  if(!memchr(e.name,0,sizeof(e.name)))continue;
  std::string n=e.name;for(char&c:n)c=(char)tolower((unsigned char)c);
  if(!f.empty()&&n.find(f)==std::string::npos)continue;
  printf("%u\t%u\t%u\t%s\n",(unsigned)e.unit,e.offset,e.size,e.name);++shown;
 }
 fprintf(stderr,"listed=%u of %u\n",shown,count);return 0;
}

int main(int argc,char**argv){
 if((argc==3||argc==4) && !strcmp(argv[1],"list"))return ListPack(argv[2],argc==4?argv[3]:"");
 if(argc==4 && !strcmp(argv[1],"import-index"))return ImportIndex(argv[2],argv[3]);
 if(argc==4 && !strcmp(argv[1],"export-index"))return ExportIndex(argv[2],argv[3]);
 if(argc==6 && !strcmp(argv[1],"audit-overlay"))return AuditOverlay(argv[2],argv[3],argv[4],argv[5]);
 if(argc==5 && !strcmp(argv[1],"extract-pack"))return ExtractPack(argv[2],argv[3],argv[4]);
 if(argc==6 && !strcmp(argv[1],"extract-match"))return ExtractMatch(argv[2],argv[3],argv[4],argv[5]);
 if(argc==6 && !strcmp(argv[1],"replace"))return ReplaceEntry(argv[2],argv[3],argv[4],argv[5]);
 if(argc==6 && !strcmp(argv[1],"replace-batch"))return ReplaceBatch(argv[2],argv[3],argv[4],argv[5]);
 if(argc==5 && !strcmp(argv[1],"verify")) return Verify(argv[2],argv[3],argv[4]);
 if(argc>=5 && !strcmp(argv[1],"extract")) return Extract(argv[2],argv[3],argv[4]);
 if(argc>=6 && !strcmp(argv[1],"build")){ unsigned long long mx=(argc>=7?strtoull(argv[6],nullptr,10):1900ULL)*1024ULL*1024ULL; return Build(argv[2],argv[3],argv[4],argv[5],mx); }
 fprintf(stderr,"Usage:\n  pak-tool extract <index.pak> <pack-dir> <out-root>\n  pak-tool list <index.pak> [path-filter]\n  pak-tool extract-match <index.pak> <pack-dir> <out-root> <name-or-relative-path>\n  pak-tool build <prefix> <root> <list.txt> <out-dir> [unitMaxMB]\n  pak-tool replace-batch <index.pak> <replacement-root> <list.txt> <out-dir>\n"); return 2;
}
