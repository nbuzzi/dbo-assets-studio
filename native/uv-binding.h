// Read-only bounded UVA probe. Dictionary must be current before clump streaming,
// as in CNtlInstanceMeshSystem::Create. Never fabricate a material association.
struct UvBindingResult {
    int materials=0,slots=0,steps=0; float duration=0; bool changed=false;
    int entries=1,frames=2;
    std::string name;
};
static std::vector<RpUVAnim*> gBindingUvAnimations;
static unsigned uvWord(const std::vector<unsigned char>& b,size_t p){unsigned v=0;std::memcpy(&v,b.data()+p,4);return v;}
static float uvFloat(const std::vector<unsigned char>& b,size_t p){float v=0;std::memcpy(&v,b.data()+p,4);return v;}
// Separate Source profile. Legacy two-frame readers below keep their bounds.
static RtDict* loadSourceMultiFrameBindingUv(const char* path,RpUVAnim*& animation){
    std::vector<unsigned char> bytes;if(!readFile(path,bytes)||(bytes.size()!=224&&bytes.size()!=352))return 0;
    const unsigned count=(unsigned)((bytes.size()-128)/32);
    if((count!=3&&count!=7)||uvWord(bytes,0)!=rwID_UVANIMDICT||uvWord(bytes,4)!=bytes.size()-12||
       uvWord(bytes,12)!=rwID_STRUCT||uvWord(bytes,16)!=4||uvWord(bytes,24)!=1||
       uvWord(bytes,28)!=rwID_ANIMANIMATION||uvWord(bytes,32)!=bytes.size()-40||
       uvWord(bytes,40)!=256||uvWord(bytes,44)!=(unsigned)_rpUVAnimParamInterpolatorInfo.typeID||
       uvWord(bytes,48)!=count||uvWord(bytes,52)!=0||uvWord(bytes,60)!=0||uvWord(bytes,96)!=0)return 0;
    float duration=uvFloat(bytes,56),last=-1;
    if(!std::isfinite(duration)||duration<=0||uvFloat(bytes,128)!=0||uvFloat(bytes,128+32*(count-1))!=duration)return 0;
    const char* name=(const char*)bytes.data()+64;size_t n=0;
    while(n<32&&name[n]){if((unsigned char)name[n]<32||(unsigned char)name[n]==127)return 0;++n;}
    if(n==0||n==32)return 0;
    for(unsigned i=0;i<count;++i){size_t base=128+32*i;float time=uvFloat(bytes,base);
        if(!std::isfinite(time)||time<0||time>duration||time<last||(i>0&&uvWord(bytes,base+28)!=i-1))return 0;
        last=time;for(size_t p=base+4;p<base+28;p+=4)if(!std::isfinite(uvFloat(bytes,p)))return 0;
    }
    // Serialized map slots1..7 are retained. Only one native node/channel0
    // may be consumed; prove that after the production reader loads the bytes.
    RwMemory memory={bytes.data(),(RwUInt32)bytes.size()};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);
    RwChunkHeaderInfo header={};RtDict* dict=0;
    if(stream&&RwStreamReadChunkHeaderInfo(stream,&header)&&header.type==rwID_UVANIMDICT)
        dict=RtDictSchemaStreamReadDict(RpUVAnimGetDictSchema(),stream);
    bool ok=dict&&stream->Type.memory.position==bytes.size();if(stream)RwStreamClose(stream,0);
    struct Entries { unsigned count=0;RpUVAnim* animation=0; } entries;
    if(ok)RtDictForAllEntries(dict,[](RtDictEntryType entry,void* data)->RtDictEntryType {
        auto& entries=*(Entries*)data;++entries.count;entries.animation=(RpUVAnim*)entry;return entry;
    },&entries);
    animation=entries.animation;
    ok=ok&&entries.count==1&&animation&&animation->interpInfo&&animation->customData&&animation->pFrames&&
       animation->numFrames==(int)count&&animation->duration==duration&&RtAnimAnimationGetNumNodes(animation)==1&&
       RpUVAnimGetUVNodeToChannelMap(animation)[0]==0&&animation->interpInfo->typeID==_rpUVAnimParamInterpolatorInfo.typeID&&
       animation->interpInfo->animKeyFrameSize==sizeof(RpUVAnimKeyFrame)&&RpUVAnimGetName(animation)&&
       std::string(RpUVAnimGetName(animation))==std::string(name,n);
    if(ok){auto* frames=(RpUVAnimKeyFrame*)animation->pFrames;
        for(unsigned i=0;ok&&i<count;++i){size_t base=128+32*i;
            // As in the legacy two-frame profile, the serialized predecessor
            // of the initial time-zero key is inactive/uninitialized Source
            // data. Preserve it; bound every predecessor actually traversed.
            ok=frames[i].time==uvFloat(bytes,base)&&(i==0||frames[i].prevFrame==frames+i-1);
            for(float value:{frames[i].data.param.theta,frames[i].data.param.s0,frames[i].data.param.s1,
                frames[i].data.param.skew,frames[i].data.param.x,frames[i].data.param.y})if(!std::isfinite(value))ok=false;
        }
    }
    if(!ok){if(dict)RtDictDestroy(dict);animation=0;return 0;}return dict;
}
static RtDict* loadSingleBindingUv(const char* path,RpUVAnim*& animation){
    std::vector<unsigned char> bytes;if(!readFile(path,bytes)||bytes.size()!=192)return 0;
    // Narrow observed single entry/two param frames profile; bound allocations
    // and references BEFORE the production dictionary/animation reader runs.
    if(uvWord(bytes,0)!=rwID_UVANIMDICT||uvWord(bytes,12)!=rwID_STRUCT||
       uvWord(bytes,4)!=180||uvWord(bytes,16)!=4||uvWord(bytes,24)!=1||
       uvWord(bytes,28)!=rwID_ANIMANIMATION||uvWord(bytes,32)!=152||
       uvWord(bytes,40)!=256||uvWord(bytes,44)!=(unsigned)_rpUVAnimParamInterpolatorInfo.typeID||
       uvWord(bytes,48)!=2||uvWord(bytes,52)!=0||uvWord(bytes,60)!=0||uvWord(bytes,188)!=0)return 0;
    float duration=uvFloat(bytes,56);
    if(!std::isfinite(duration)||duration<=0||uvFloat(bytes,128)!=0||uvFloat(bytes,160)!=duration)return 0;
    const char* name=(const char*)bytes.data()+64;size_t n=0;
    while(n<32&&name[n]){if((unsigned char)name[n]<32||(unsigned char)name[n]==127)return 0;++n;}
    if(n==0||n==32)return 0;
    // One node, first UV channel only. Other channel mappings are out of scope.
    if(uvWord(bytes,96)!=0)return 0;
    // Explicit OMB4 profile preserves unused serialized map words. Only node0
    // exists in this two-frame animation; prove that again after native load.
    // Legacy OMB3 still rejects every nonzero unused map word.
    if(!gAllowInactiveUvMaps)for(size_t p=100;p<128;p+=4)if(uvWord(bytes,p)!=0)return 0;
    for(size_t base:{132u,164u})for(size_t p=base;p<base+24;p+=4)if(!std::isfinite(uvFloat(bytes,p)))return 0;
    RwMemory memory={bytes.data(),(RwUInt32)bytes.size()};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);
    RwChunkHeaderInfo header={};RtDict* dict=0;
    if(stream&&RwStreamReadChunkHeaderInfo(stream,&header)&&header.type==rwID_UVANIMDICT)
        dict=RtDictSchemaStreamReadDict(RpUVAnimGetDictSchema(),stream);
    bool ok=dict&&stream->Type.memory.position==bytes.size();
    if(stream)RwStreamClose(stream,0);
    struct Entries { unsigned count=0; RpUVAnim* animation=0; } entries;
    if(ok)RtDictForAllEntries(dict,[](RtDictEntryType entry,void* data)->RtDictEntryType {
        auto& entries=*(Entries*)data;++entries.count;entries.animation=(RpUVAnim*)entry;return entry;
    },&entries);
    animation=entries.animation;
    ok=ok&&entries.count==1&&animation&&animation->interpInfo&&animation->customData&&animation->pFrames&&
        animation->numFrames==2&&animation->duration==duration&&
        RtAnimAnimationGetNumNodes(animation)==1&&RpUVAnimGetUVNodeToChannelMap(animation)[0]==0&&
        animation->interpInfo->typeID==_rpUVAnimParamInterpolatorInfo.typeID&&
        animation->interpInfo->animKeyFrameSize==sizeof(RpUVAnimKeyFrame)&&
        RpUVAnimGetName(animation)&&std::string(RpUVAnimGetName(animation))==std::string(name,n);
    if(ok){auto* frames=(RpUVAnimKeyFrame*)animation->pFrames;
        ok=frames[0].time==0&&frames[1].time==duration&&frames[1].prevFrame==frames;
        for(const auto& frame:{frames[0],frames[1]})for(float value:{frame.data.param.theta,frame.data.param.s0,
            frame.data.param.s1,frame.data.param.skew,frame.data.param.x,frame.data.param.y})if(!std::isfinite(value))ok=false;
    }
    if(!ok){if(dict)RtDictDestroy(dict);animation=0;return 0;}
    return dict;
}
static RtDict* loadBindingUv(const char* path,RpUVAnim*& animation){
    gBindingUvAnimations.clear();animation=0;
    std::vector<unsigned char> bytes;if(!readFile(path,bytes))return 0;
    if(gAllowSourceMultiFrameUv){auto* dict=loadSourceMultiFrameBindingUv(path,animation);if(dict)gBindingUvAnimations.push_back(animation);return dict;}
    if((gAllowInactiveUvMaps&&bytes.size()!=192)||(gAllowTwoEntryInactiveUvMaps&&bytes.size()!=356))return 0;
    if(bytes.size()==192){auto* dict=loadSingleBindingUv(path,animation);if(dict)gBindingUvAnimations.push_back(animation);return dict;}
    // Separate observed profile: two entries, two param frames per entry.
    // Bound both chunks before the production dictionary reader allocates.
    if(bytes.size()!=356||uvWord(bytes,0)!=rwID_UVANIMDICT||uvWord(bytes,4)!=344||
       uvWord(bytes,12)!=rwID_STRUCT||uvWord(bytes,16)!=4||uvWord(bytes,24)!=2)return 0;
    std::vector<std::string> names;std::vector<float> durations;
    for(size_t base:{28u,192u}){
        if(uvWord(bytes,base)!=rwID_ANIMANIMATION||uvWord(bytes,base+4)!=152||uvWord(bytes,base+12)!=256||
           uvWord(bytes,base+16)!=(unsigned)(gAllowTwoEntryInactiveUvMaps?_rpUVAnimLinearInterpolatorInfo.typeID:_rpUVAnimParamInterpolatorInfo.typeID)||uvWord(bytes,base+20)!=2||
           uvWord(bytes,base+24)!=0||uvWord(bytes,base+32)!=0||uvWord(bytes,base+160)!=0)return 0;
        float duration=uvFloat(bytes,base+28);
        if(!std::isfinite(duration)||duration<=0||uvFloat(bytes,base+100)!=0||uvFloat(bytes,base+132)!=duration)return 0;
        const char* name=(const char*)bytes.data()+base+36;size_t n=0;
        while(n<32&&name[n]){if((unsigned char)name[n]<32||(unsigned char)name[n]==127)return 0;++n;}
        if(n==0||n==32)return 0;std::string literal(name,n);
        if(!gAllowIdenticalNamedUv&&std::find(names.begin(),names.end(),literal)!=names.end())return 0;
        names.push_back(literal);durations.push_back(duration);
        if(uvWord(bytes,base+68)!=0)return 0;
        // Explicit OMB5: node0/channel0 remains bounded. Other serialized map
        // slots are unused by the one native node; preserve their raw words.
        if(!gAllowTwoEntryInactiveUvMaps)for(size_t p=base+72;p<base+100;p+=4)if(uvWord(bytes,p)!=0)return 0;
        for(size_t frame:{base+104,base+136})for(size_t p=frame;p<frame+24;p+=4)if(!std::isfinite(uvFloat(bytes,p)))return 0;
    }
    // Exact Source pair repeats the same dictionary name and animation.
    // Only the inactive first predecessor word differs. Preserve both records;
    // every consumed parameter/time/map must be byte-identical before loading.
    if(gAllowIdenticalNamedUv){
        if(names.size()!=2||names[0]!=names[1]||durations[0]!=durations[1]||
           std::memcmp(bytes.data()+64,bytes.data()+228,92)||
           std::memcmp(bytes.data()+160,bytes.data()+324,32))return 0;
    }
    RwMemory memory={bytes.data(),(RwUInt32)bytes.size()};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);
    RwChunkHeaderInfo header={};RtDict* dict=0;
    if(stream&&RwStreamReadChunkHeaderInfo(stream,&header)&&header.type==rwID_UVANIMDICT)
        dict=RtDictSchemaStreamReadDict(RpUVAnimGetDictSchema(),stream);
    bool ok=dict&&stream->Type.memory.position==bytes.size();if(stream)RwStreamClose(stream,0);
    std::vector<RpUVAnim*> entries;
    if(ok)RtDictForAllEntries(dict,[](RtDictEntryType entry,void* data)->RtDictEntryType {
        auto& entries=*(std::vector<RpUVAnim*>*)data;entries.push_back((RpUVAnim*)entry);return entry;
    },&entries);
    ok=ok&&entries.size()==2;
    for(size_t i=0;ok&&i<names.size();++i){
        auto match=std::find_if(entries.begin(),entries.end(),[&](RpUVAnim* entry){return entry&&RpUVAnimGetName(entry)&&names[i]==RpUVAnimGetName(entry);});
        if(match==entries.end()){ok=false;break;}auto* entry=*match;
        entries.erase(match);
        ok=entry->interpInfo&&entry->customData&&entry->pFrames&&entry->numFrames==2&&entry->duration==durations[i]&&
           RtAnimAnimationGetNumNodes(entry)==1&&RpUVAnimGetUVNodeToChannelMap(entry)[0]==0&&
           entry->interpInfo->typeID==(gAllowTwoEntryInactiveUvMaps?_rpUVAnimLinearInterpolatorInfo.typeID:_rpUVAnimParamInterpolatorInfo.typeID)&&entry->interpInfo->animKeyFrameSize==sizeof(RpUVAnimKeyFrame);
        if(ok){auto* frames=(RpUVAnimKeyFrame*)entry->pFrames;
            ok=frames[0].time==0&&frames[1].time==durations[i]&&frames[1].prevFrame==frames;
            for(const auto& frame:{frames[0],frames[1]})
            {
                if(gAllowTwoEntryInactiveUvMaps){for(float v:frame.data.linear.uv)if(!std::isfinite(v))ok=false;}
                else for(float v:{frame.data.param.theta,frame.data.param.s0,frame.data.param.s1,frame.data.param.skew,frame.data.param.x,frame.data.param.y})if(!std::isfinite(v))ok=false;
            }
        }
        if(ok)gBindingUvAnimations.push_back(entry);
    }
    if(!ok){if(dict)RtDictDestroy(dict);gBindingUvAnimations.clear();return 0;}
    animation=gBindingUvAnimations.front();return dict;
}
struct UvMaterialSearch { RpUVAnim* animation; std::vector<RpMaterial*> materials; int slots=0; bool failed=false; };
static RpMaterial* collectUvMaterial(RpMaterial* material,void* data){
    auto& search=*(UvMaterialSearch*)data;
    if(!RpMaterialUVAnimExists(material)||std::find(search.materials.begin(),search.materials.end(),material)!=search.materials.end())return material;
    if(search.materials.size()>=MAX_MATERIALS){search.failed=true;return 0;}
    int slots=0;
    for(unsigned slot=0;slot<RP_UVANIM_MAXSLOTS;++slot){
        auto* interp=RpMaterialUVAnimGetInterpolator(material,slot);if(!interp)continue;
        ++slots;
        if(std::find(gBindingUvAnimations.begin(),gBindingUvAnimations.end(),interp->pCurrentAnim)==gBindingUvAnimations.end()||interp->numNodes!=1)search.failed=true;
    }
    if(slots!=1)search.failed=true;
    search.slots+=slots;search.materials.push_back(material);return search.failed?0:material;
}
static RpAtomic* collectUvAtomic(RpAtomic* atomic,void* data){
    auto& search=*(UvMaterialSearch*)data;RpGeometry* geometry=RpAtomicGetGeometry(atomic);
    if(geometry)RpGeometryForAllMaterials(geometry,collectUvMaterial,data);
    return search.failed?0:atomic;
}
static bool playbackBindingUv(RpClump* clump,RpUVAnim* animation,UvBindingResult& result){
    UvMaterialSearch search={animation};RpClumpForAllAtomics(clump,collectUvAtomic,&search);
    bool ok=!search.failed&&!search.materials.empty();std::vector<RwMatrix> initial;
    float duration=animation->duration;for(auto* entry:gBindingUvAnimations)duration=std::max(duration,entry->duration);
    for(int step=0;ok&&step<=40;++step){
        for(size_t i=0;ok&&i<search.materials.size();++i){auto* material=search.materials[i];
            ok=(step?RpMaterialUVAnimAddAnimTime(material,duration/16.f):RpMaterialUVAnimSetCurrentTime(material,0))!=0;
            ok=ok&&RpMaterialUVAnimApplyUpdate(material)!=0;
            RwMatrix *base=0,*dual=0;
            ok=ok&&RpMatFXMaterialGetUVTransformMatrices(material,&base,&dual)!=0&&finiteBindingMatrix(base)&&!dual;
            auto* interp=RpMaterialUVAnimGetInterpolator(material,0);
            // This profile is slot0 only; reject unresolved or shifted bindings.
            ok=ok&&interp&&std::find(gBindingUvAnimations.begin(),gBindingUvAnimations.end(),interp->pCurrentAnim)!=gBindingUvAnimations.end()&&std::isfinite(interp->currentTime);
            if(step==0&&ok)initial.push_back(*base);
            else if(ok)for(const auto& pair:{std::make_pair(&base->right,&initial[i].right),std::make_pair(&base->up,&initial[i].up),
                std::make_pair(&base->at,&initial[i].at),std::make_pair(&base->pos,&initial[i].pos)})
                if(std::fabs(pair.first->x-pair.second->x)>1e-6||std::fabs(pair.first->y-pair.second->y)>1e-6||std::fabs(pair.first->z-pair.second->z)>1e-6)result.changed=true;
        }
        if(step&&ok)++result.steps;
    }
    if(ok){result.materials=(int)search.materials.size();result.slots=search.slots;result.duration=duration;result.name=RpUVAnimGetName(animation);result.entries=(int)gBindingUvAnimations.size();result.frames=animation->numFrames;}
    return ok;
}
