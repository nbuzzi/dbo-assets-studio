// Included only by the explicitly hash-pinned reader build adapter. The core
// source is never edited: its component implementation is copied into the
// private build with only the string Load overload instrumented/guarded.
#include <map>
#include <string>

struct NativeEffectStringField { long offset; int bytes; std::string value; };
static std::map<const std::string*,NativeEffectStringField> gEffectStringFields;
static std::map<const char*,NativeEffectStringField> gEffectSplineFields;
static long gEffectStringFileBytes=0;
static bool gEffectStringFieldsEnabled=false;
static void TraceEffectSpline(const SNtlPrtEmitterSplinePath& spline,long offset){
    static_assert(offsetof(SNtlPrtEmitterSplinePath,strName)==0,"native spline name offset");
    static_assert(sizeof(spline.strName)==260,"native spline name ABI");
    Require(offset>=0&&offset<=gEffectStringFileBytes-(long)sizeof(spline)&&
        memchr(spline.strName,0,sizeof(spline.strName))!=NULL,"bounded native spline name");
    if(gEffectStringFieldsEnabled)gEffectSplineFields[spline.strName]={offset,sizeof(spline.strName),spline.strName};
}
static void SplineResourceField(const std::string& effect,unsigned system,const SNtlPrtEmitterSplinePath& spline){
    auto found=gEffectSplineFields.find(spline.strName);Require(found!=gEffectSplineFields.end(),"actual spline object traced");
    const auto& field=found->second;Require(field.value==spline.strName,"native spline name preserved");
    printf("F\t%s\t%u\tspline\t-1\t%ld\t%d\t",Field(effect),system,field.offset,field.bytes);
    static const char* digits="0123456789abcdef";
    for(unsigned char byte:field.value){putchar(digits[byte>>4]);putchar(digits[byte&15]);}putchar('\n');
}
static void TraceEffectString(const std::string& value,long offset,int bytes){
    Require(offset>=0&&bytes>=0&&bytes<=NTL_MAX_DIR_PATH&&offset<=gEffectStringFileBytes-4&&
        bytes<=gEffectStringFileBytes-offset-4,"native string span");
    if(gEffectStringFieldsEnabled){
        Require(gEffectStringFields.size()<100000,"native string trace count");
        gEffectStringFields[&value]={offset,bytes,value};
    }
}
static void ResourceField(const std::string& effect,unsigned system,const char* kind,int slot,const std::string& value){
    auto found=gEffectStringFields.find(&value);Require(found!=gEffectStringFields.end(),"actual string object traced");
    const auto& field=found->second;Require(field.value==value,"native string value preserved");
    printf("F\t%s\t%u\t%s\t%d\t%ld\t%d\t",Field(effect),system,kind,slot,field.offset,field.bytes);
    static const char* digits="0123456789abcdef";
    for(unsigned char byte:value){putchar(digits[byte>>4]);putchar(digits[byte&15]);}putchar('\n');
}
#include "component-trace.cpp"
