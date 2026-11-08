// Production Taiwan callback observer. No effect/audio/combat consumer invoked.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "precomp_ntlpresentation.h"
#include "NtlPLCharacterProperty.h"
#include "NtlSerializer.h"
#include "NtlAnimLayer.h"
// Compile the current implementations, rather than borrowing an older library's
// timing implementation. The matching client libraries supply other dependencies.
#include "NtlAnimChannel.cpp"
#include "NtlAnimLayer.cpp"
#define main MediaPreviewEntryNotInvoked
#include "media-helper.cpp"
#undef main
#include <memory>
#include <iomanip>
#include <set>
#include <sstream>

static void ProbeRequire(bool ok,const char* why){if(!ok){fprintf(stderr,"FAIL %s\n",why);exit(1);}}
static RpClump* ProbeClump(const std::vector<unsigned char>& bytes){
    RwTextureSetReadCallBack([](const RwChar* name,const RwChar* mask)->RwTexture*{
        if(!name||strlen(name)>127||(mask&&strlen(mask)>127))return 0;
        RwRaster* raster=RwRasterCreate(4,4,32,rwRASTERTYPETEXTURE|rwRASTERFORMAT8888);
        RwTexture* texture=raster?RwTextureCreate(raster):0;
        if(texture){RwTextureSetName(texture,name);if(mask)RwTextureSetMaskName(texture,mask);}
        return texture;
    });
    RwMemory memory={(RwUInt8*)bytes.data(),(RwUInt32)bytes.size()};
    RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);RwChunkHeaderInfo head={};
    RpClump* clump=stream&&RwStreamReadChunkHeaderInfo(stream,&head)&&head.type==rwID_CLUMP?RpClumpStreamRead(stream):0;
    if(stream)RwStreamClose(stream,0);return clump;
}
struct ProbeEvent {int sourceIndex,kind;float sourceTime,clockBefore,elapsed;bool end;};
struct ProbeCapture {
    STypeAnimData* source;float clockBefore=0,elapsed=0;std::vector<ProbeEvent> events;
    int Receive(void* data){
        auto* event=(SEventAnim*)data;int index=-1;
        for(size_t i=0;i<source->vecAnimEvent.size();++i)if(source->vecAnimEvent[i]==event)index=(int)i;
        bool end=event->eEventID==EVENT_ANIM_END;
        ProbeRequire(index>=0||end,"callback must be exact source event or native end");
        ProbeRequire(events.size()<16384&&std::isfinite(event->fTime),"bounded finite callbacks");
        events.push_back({index,(int)event->eEventID,event->fTime,clockBefore,elapsed,end});return 0;
    }
};
static void ProbeCase(std::ostream& out,const char* name,int mode,
                      const std::vector<unsigned char>& model,RtAnimAnimation* animation,STypeAnimData* type){
    RpClump* clump=ProbeClump(model);ProbeRequire(clump!=0,"fresh native clump");
    HierarchySearch search;RwFrameForAllChildren(RpClumpGetFrame(clump),FindBindingHierarchy,&search);
    RpHAnimHierarchy* hierarchy=search.hierarchy;ProbeRequire(hierarchy&&!search.failed,"native hierarchy");
    RpHAnimHierarchySetFlags(hierarchy,(RpHAnimHierarchyFlag)(hierarchy->flags|rpHANIMHIERARCHYUPDATELTMS|rpHANIMHIERARCHYUPDATEMODELLINGMATRICES));
    ProbeRequire(RpHAnimHierarchyAttach(hierarchy)!=0&&RpHAnimHierarchySetCurrentAnim(hierarchy,animation)!=0,"native attach/animation");
    CNtlPLResource resource;resource.SetData(animation);SInstanceAnimData instance;
    instance.pResource=&resource;instance.pTypeAnimData=type;
    CNtlAnimLayer layer;ProbeRequire(layer.Create(hierarchy)!=0,"production layer Create");
    ProbeCapture capture;capture.source=type;layer.SetCallBack(&capture,&ProbeCapture::Receive);
    bool looping=mode>=4;
    ProbeRequire(layer.SetAllAnimation(&instance,0,looping?TRUE:FALSE)!=0,"production clock reset");
    unsigned steps=0,wraps=0;float elapsedTotal=0,largestEvent=animation->duration;
    for(auto* event:type->vecAnimEvent)largestEvent=std::max(largestEvent,event->fTime);
    auto update=[&](float elapsed){capture.clockBefore=layer.GetCurrentAnimTime();capture.elapsed=elapsed;
        ProbeRequire(layer.Update(elapsed)!=0,"production layer Update");++steps;elapsedTotal+=elapsed;
        if(layer.GetCurrentAnimTime()<capture.clockBefore)++wraps;};
    bool flushed=false;
    if(mode==3){update(std::min(animation->duration*0.5f,0.25f));capture.clockBefore=layer.GetCurrentAnimTime();capture.elapsed=0;
        layer.OnAfterAnimEventRun();flushed=true;}
    else if(mode==5)update(animation->duration*2.f+0.1f);
    else if(mode==4)while(elapsedTotal<animation->duration*2.f+0.05f&&steps<4096)update(1.f/60.f);
    else if(mode==2)update(largestEvent+0.1f);
    else while(!layer.GetCurrentAnimEnd()&&steps<4096)update(mode==0?1.f/60.f:0.1f);
    float finalClock=layer.GetCurrentAnimTime();bool ended=layer.GetCurrentAnimEnd()!=0;
    ProbeRequire(std::isfinite(finalClock)&&steps>0&&steps<4096&&(mode==3||(looping?!ended:ended)),"bounded native termination");
    out<<"{\"name\":\""<<name<<"\",\"steps\":"<<steps<<",\"finalClock\":"<<finalClock
       <<",\"ended\":"<<(ended?"true":"false")<<",\"looping\":"<<(looping?"true":"false")
       <<",\"elapsedTotal\":"<<elapsedTotal<<",\"observedClockWraps\":"<<wraps
       <<",\"cancelFlushed\":"<<(flushed?"true":"false")<<",\"callbacks\":[";
    for(size_t i=0;i<capture.events.size();++i){const auto& event=capture.events[i];if(i)out<<",";
        out<<"{\"sourceIndex\":"<<event.sourceIndex<<",\"kind\":"<<event.kind<<",\"sourceTime\":"<<event.sourceTime
           <<",\"clockBefore\":"<<event.clockBefore<<",\"elapsed\":"<<event.elapsed<<",\"nativeEnd\":"<<(event.end?"true":"false")<<"}";}
    out<<"]}";
    layer.Destroy();RpHAnimHierarchyDetach(hierarchy);hierarchy->currentAnim->pCurrentAnim=0;RpClumpDestroy(clump);
}
int main(int argc,char** argv){
    ProbeRequire(argc==6,"character RDF model DFF animation ANM model-key animation-key");
    std::string key=argv[4];ProbeRequire(key.size()>0&&key.size()<128&&key.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_")==std::string::npos,"bounded model key");
    char* end=0;unsigned long animationKey=strtoul(argv[5],&end,10);ProbeRequire(*argv[5]&&end&&!*end&&animationKey<65535,"native animation key");
    std::vector<unsigned char> character,model,rawAnimation;
    ProbeRequire(readFile(argv[1],character)&&readFile(argv[2],model)&&readFile(argv[3],rawAnimation),"bounded inputs");
    CNtlSerializer input;input.In(character.data(),(int)character.size());unsigned version,count;std::string root;
    input>>version;input>>root;input>>count;ProbeRequire(version==1&&count>0&&count<=10000,"native character header");
    CNtlPLCharacterProperty::m_strVer=version;CNtlPLCharacterProperty::m_strCharacterDataPath=root;
    std::unique_ptr<CNtlPLCharacterProperty> selected;std::set<std::string> names;
    for(unsigned i=0;i<count;++i){std::unique_ptr<CNtlPLCharacterProperty> row(new CNtlPLCharacterProperty());
        row->LoadSerialize(input);ProbeRequire(names.insert(row->GetName()).second,"unique character identity");
        if(key==row->GetName())selected=std::move(row);}
    ProbeRequire(input.GetDataSize()==0&&selected.get()!=0,"complete native EOF and selected model");
    STypeAnimData* type=selected->GetAnimTable()->Get(animationKey);ProbeRequire(type!=0&&type->vecAnimEvent.size()<=4096,"exact native animation type");
    HWND window=0;ProbeRequire(openEngine(argv[2],window)==0,"native RenderWare init");
    RpClump* validation=ProbeClump(model);AnimationBindingResult binding;
    ProbeRequire(validation&&playbackBinding(validation,argv[3],binding),"canonical DFF/ANM structural and native binding preflight");RpClumpDestroy(validation);
    RwMemory memory={rawAnimation.data(),(RwUInt32)rawAnimation.size()};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);RwChunkHeaderInfo header={};
    RtAnimAnimation* animation=stream&&RwStreamReadChunkHeaderInfo(stream,&header)?RtAnimAnimationStreamRead(stream):0;
    ProbeRequire(animation&&stream->Type.memory.position==rawAnimation.size()&&animation->duration<=30,"native animation EOF and duration bound");RwStreamClose(stream,0);
    std::ostringstream out;out<<std::setprecision(9)<<"{\"profile\":\"taiwan-native-animation-callbacks-v1\",\"nativeCharacterEOF\":true,\"records\":"<<count
        <<",\"animationKey\":"<<animationKey<<",\"declaredDuration\":"<<type->fPlayTime<<",\"nativeDuration\":"<<animation->duration
        <<",\"sourceEvents\":[";
    for(size_t i=0;i<type->vecAnimEvent.size();++i){const auto* event=type->vecAnimEvent[i];if(i)out<<",";
        ProbeRequire(std::isfinite(event->fTime)&&event->fTime>=0&&event->fTime<=30,"bounded source event time");
        out<<"{\"index\":"<<i<<",\"kind\":"<<(int)event->eEventID<<",\"time\":"<<event->fTime<<"}";}
    out<<"],\"hitEventKind\":"<<(int)EVENT_ANIM_HIT<<",\"visualEffectEventKind\":"<<(int)EVENT_ANIM_VISUAL_EFFECT<<",\"visualSoundEventKind\":"<<(int)EVENT_ANIM_VISUAL_SOUND<<",\"cases\":[";
    const char* cases[]={"normal-60hz","normal-10hz","single-long-frame","cancel-before-native-end","loop-two-cycles-60hz","loop-single-long-frame"};
    for(int i=0;i<6;++i){if(i)out<<",";ProbeCase(out,cases[i],i,model,animation,type);}
    out<<"],\"productionLayerAndChannel\":true,\"callbacksDispatched\":true,\"effectConsumersInvoked\":false,\"combatConsumersInvoked\":false,\"texturesSubstituted\":true,\"renderingTested\":false,\"worldTested\":false}";
    RtAnimAnimationDestroy(animation);closeEngine(window);printf("%s\n",out.str().c_str());return 0;
}
