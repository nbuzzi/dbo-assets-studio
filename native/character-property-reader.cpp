// Read-only production Taiwan character serializer probe. No asset writes.
#include "precomp_ntlpresentation.h"
#include "NtlPLCharacterProperty.h"
#include "NtlSerializer.h"
#include <fstream>
#include <iterator>
#include <vector>
#include <set>
#include <cstdio>
#include <cstdlib>
static void Require(bool ok,const char* why){if(!ok){fprintf(stderr,"FAIL %s\n",why);exit(1);}}
int main(int argc,char** argv){
    Require(argc==2||argc==3,"characterproperty RDF [required-model]");std::ifstream file(argv[1],std::ios::binary);Require(bool(file),"input");
    std::vector<char> raw((std::istreambuf_iterator<char>(file)),std::istreambuf_iterator<char>());
    Require(raw.size()>12&&raw.size()<=128*1024*1024,"size");
    CNtlSerializer input;input.In(raw.data(),int(raw.size()));unsigned version,count;std::string path;
    input>>version;input>>path;input>>count;Require(version==1&&count>0&&count<=10000,"Taiwan1 header");
    CNtlPLCharacterProperty::m_strVer=version;CNtlPLCharacterProperty::m_strCharacterDataPath=path;
    std::set<std::string> names;unsigned animationCount=0,nonExactRows=0;bool requiredFound=argc==2;
    for(unsigned i=0;i<count;++i){
        int start=input.GetStartPointer();CNtlPLCharacterProperty property;property.LoadSerialize(input);int end=input.GetStartPointer();
        Require(end>start&&end<=int(raw.size()),"native row boundary");Require(names.insert(property.GetName()).second,"unique model");
        CNtlSerializer output;property.SaveSerialize(output);
        // The production serializer normalizes some event bytes and reverses
        // walk/run. Report that behavior; never claim a byte-exact roundtrip.
        Require(output.GetDataSize()==end-start&&end-start>=8,"native size");
        if(memcmp(output.GetData(),raw.data()+start,end-start-8))++nonExactRows;
        Require(!memcmp(output.GetData()+end-start-8,raw.data()+end-4,4)&&!memcmp(output.GetData()+end-start-4,raw.data()+end-8,4),"native walk/run swap");
        animationCount+=unsigned(property.GetAnimTable()->GetTypeAnimMap()->size());
        if(argc==3&&property.GetName()==std::string(argv[2])){requiredFound=true;
            printf("SELECTED model=%s animations=%zu nativeBytes=%d\n",argv[2],property.GetAnimTable()->GetTypeAnimMap()->size(),end-start);}
    }
    Require(input.GetDataSize()==0,"complete native EOF");
    Require(requiredFound,"required model present in native reader");
    printf("PASS native Taiwan character models=%u animations=%u nonExactSavedRows=%u; native EOF; no byte-exact serialization/render claim\n",count,animationCount,nonExactRows);return 0;
}
