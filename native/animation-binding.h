// Isolated native object hierarchy playback; never renders or dispatches events.
struct BoneFrameRecord { int parent=-1; std::string name; bool userDataPresent=false,clientMapped=false; };
struct AnimationBindingResult { int nodes=0,frames=0,steps=0,skinAtomics=0; float duration=0; bool changed=false; std::vector<BoneFrameRecord> boneFrames; };
struct BoneFrameCollection { std::vector<BoneFrameRecord>* frames; int parent=0; unsigned depth=0; bool clientReachable=true,failed=false; };
static RwFrame* CollectBoneFrame(RwFrame* frame,void* data){
    auto& state=*(BoneFrameCollection*)data;
    if(!frame||state.frames->size()>=65536||state.depth>=256){state.failed=true;return 0;}
    RpUserDataArray* names=RwFrameGetUserDataArray(frame,0);
    BoneFrameRecord record;record.parent=state.parent;record.userDataPresent=names!=0;
    if(names&&names->format==rpSTRINGUSERDATA&&names->numElements>0){
        const char* name=RpUserDataArrayGetString(names,0);
        if(name){
            size_t length=strnlen(name,1025);
            if(length>1024){state.failed=true;return 0;}
            record.name.assign(name,length);record.clientMapped=state.clientReachable;
        }
    }
    int index=(int)state.frames->size();state.frames->push_back(record);
    int parent=state.parent;bool reachable=state.clientReachable;
    state.parent=index;state.clientReachable=reachable&&record.userDataPresent;++state.depth;
    RwFrameForAllChildren(frame,CollectBoneFrame,data);
    --state.depth;state.parent=parent;state.clientReachable=reachable;
    return state.failed?0:frame;
}
struct AnimationPoseSample { float time=0; std::string bone; RwV3d position={}; };
static std::vector<AnimationPoseSample> gPoseSamples;
struct PoseFrameSearch { const std::string* name; RwFrame* found=0; unsigned visited=0,depth=0; bool failed=false; };
static RwFrame* FindPoseFrame(RwFrame* frame,void* data){
    auto& search=*(PoseFrameSearch*)data;
    if(++search.visited>65536||search.depth>=256){search.failed=true;return 0;}
    RpUserDataArray* names=RwFrameGetUserDataArray(frame,0);
    if(names&&names->format==rpSTRINGUSERDATA&&names->numElements>0){
        const char* name=RpUserDataArrayGetString(names,0);
        if(name&&*search.name==name){if(search.found)search.failed=true;else search.found=frame;}
    }
    ++search.depth;RwFrameForAllChildren(frame,FindPoseFrame,data);--search.depth;
    return search.failed?0:frame;
}
struct HierarchySearch { RpHAnimHierarchy* hierarchy=0; unsigned visited=0,depth=0; bool failed=false; };
static RwFrame* FindBindingHierarchy(RwFrame* frame,void* data){
    auto& search=*(HierarchySearch*)data;
    if(++search.visited>65536||search.depth>=256){search.failed=true;return 0;}
    RpHAnimHierarchy* found=RpHAnimFrameGetHierarchy(frame);
    if(found){if(search.hierarchy&&search.hierarchy!=found)search.failed=true;else search.hierarchy=found;}
    ++search.depth;RwFrameForAllChildren(frame,FindBindingHierarchy,data);--search.depth;
    return search.failed?0:frame;
}
struct SkinBinding { RpHAnimHierarchy* hierarchy; bool failed=false; int count=0; };
static RpAtomic* BindAnimationSkin(RpAtomic* atomic,void* data){
    auto& binding=*(SkinBinding*)data;RpGeometry* geometry=RpAtomicGetGeometry(atomic);
    RpSkin* skin=geometry?RpSkinGeometryGetSkin(geometry):0;
    if(skin){
        if(RpSkinGetNumBones(skin)>(unsigned)binding.hierarchy->numNodes||!RpSkinAtomicSetHAnimHierarchy(atomic,binding.hierarchy))binding.failed=true;
        ++binding.count;
    }
    return binding.failed?0:atomic;
}
static bool finiteBindingMatrix(const RwMatrix* matrix){
    if(!matrix)return false;
    for(const RwV3d* value:{&matrix->right,&matrix->up,&matrix->at,&matrix->pos})
        if(!std::isfinite(value->x)||!std::isfinite(value->y)||!std::isfinite(value->z))return false;
    return true;
}
static bool playbackBinding(RpClump* clump,const char* path,AnimationBindingResult& result){
    // Bound the standard ANM stream before its production reader allocates frames.
    std::vector<unsigned char> bytes;if(!readFile(path,bytes)||bytes.size()<32)return false;
    unsigned words[8]={};std::memcpy(words,bytes.data(),32);
    float duration=0;std::memcpy(&duration,bytes.data()+28,4);
    if(words[0]!=rwID_ANIMANIMATION||words[1]!=bytes.size()-12||words[3]!=256||words[4]!=rpHANIMSTDKEYFRAMETYPEID||
       !words[5]||words[5]>1000000||bytes.size()!=32ull+36ull*words[5]||!std::isfinite(duration)||duration<=0)return false;
    RwMemory memory={bytes.data(),(RwUInt32)bytes.size()};RwStream* stream=RwStreamOpen(rwSTREAMMEMORY,rwSTREAMREAD,&memory);
    RwChunkHeaderInfo header={};RtAnimAnimation* animation=0;
    if(stream&&RwStreamReadChunkHeaderInfo(stream,&header))animation=RtAnimAnimationStreamRead(stream);
    bool ok=animation&&animation->interpInfo&&animation->interpInfo->typeID==rpHANIMSTDKEYFRAMETYPEID&&
        animation->interpInfo->animKeyFrameSize==sizeof(RpHAnimKeyFrame)&&animation->pFrames&&
        animation->numFrames==(int)words[5]&&stream->Type.memory.position==bytes.size();
    if(stream)RwStreamClose(stream,0);
    int nodes=0;
    if(ok){
        auto* frames=(RpHAnimKeyFrame*)animation->pFrames;
        while(nodes<animation->numFrames&&frames[nodes].time==0)++nodes;
        ok=nodes>0&&nodes<=4096&&animation->numFrames>=nodes*2;
        uintptr_t begin=(uintptr_t)frames,end=begin+animation->numFrames*sizeof(*frames);
        for(int i=0;ok&&i<animation->numFrames;++i){auto& frame=frames[i];
            for(float value:{frame.time,frame.q.imag.x,frame.q.imag.y,frame.q.imag.z,frame.q.real,frame.t.x,frame.t.y,frame.t.z})if(!std::isfinite(value))ok=false;
            if(frame.time<0||frame.time>duration)ok=false;
            uintptr_t previous=(uintptr_t)frame.prevFrame;
            // Initial exporter links are unused and preserved, like baseline ANM.
            if(i>=nodes&&(previous<begin||previous>=end||(previous-begin)%sizeof(*frames)))ok=false;
        }
    }
    HierarchySearch search;
    // Match Helper_GetHierarchyClump's child search, not a guessed skeleton.
    if(ok)RwFrameForAllChildren(RpClumpGetFrame(clump),FindBindingHierarchy,&search);
    RpHAnimHierarchy* hierarchy=search.hierarchy;
    ok=ok&&!search.failed&&hierarchy&&hierarchy->numNodes==nodes&&hierarchy->pNodeInfo&&hierarchy->currentAnim&&
        !(hierarchy->flags&rpHANIMHIERARCHYSUBHIERARCHY);
    bool attached=false;
    if(ok&&!gPoseSamples.empty()){
        // Observe the real Helper_GetBoneList traversal: it starts at root's
        // children and only descends through frames with UserData array0.
        // Keep all frames for diagnostics, including those the client skips.
        BoneFrameRecord root;root.parent=-1;result.boneFrames.push_back(root);
        BoneFrameCollection frames={&result.boneFrames};
        RwFrameForAllChildren(RpClumpGetFrame(clump),CollectBoneFrame,&frames);
        ok=!frames.failed;
    }
    if(ok){
        RpHAnimHierarchySetFlags(hierarchy,(RpHAnimHierarchyFlag)(hierarchy->flags|rpHANIMHIERARCHYUPDATELTMS|rpHANIMHIERARCHYUPDATEMODELLINGMATRICES));
        attached=RpHAnimHierarchyAttach(hierarchy)!=0;ok=attached;
        for(int i=0;ok&&i<nodes;++i)ok=hierarchy->pNodeInfo[i].pFrame&&hierarchy->pNodeInfo[i].nodeIndex==i;
        SkinBinding skin={hierarchy};if(ok)RpClumpForAllAtomics(clump,BindAnimationSkin,&skin);ok=ok&&!skin.failed;
        result.skinAtomics=skin.count;
        for(auto& sample:gPoseSamples){
            if(!ok)break;
            // Native AddAnimTime wraps at duration. Reject the endpoint rather
            // than reporting the first pose as the non-looping final pose.
            if(!std::isfinite(sample.time)||sample.time<0||sample.time>=duration){ok=false;break;}
            ok=RpHAnimHierarchySetCurrentAnim(hierarchy,animation)!=0;
            if(ok&&sample.time>0)ok=RpHAnimHierarchyAddAnimTime(hierarchy,sample.time)!=0;
            ok=ok&&RpHAnimHierarchyUpdateMatrices(hierarchy)!=0;
            PoseFrameSearch search={&sample.bone};
            if(sample.bone.empty())search.found=RpClumpGetFrame(clump);
            else RwFrameForAllChildren(RpClumpGetFrame(clump),FindPoseFrame,&search);
            RwMatrix* matrix=search.found?RwFrameGetLTM(search.found):0;
            ok=ok&&!search.failed&&finiteBindingMatrix(matrix);
            if(ok)sample.position=matrix->pos;
        }
        if(ok)ok=RpHAnimHierarchySetCurrentAnim(hierarchy,animation)!=0;
        std::vector<RwMatrix> initial;
        for(int step=0;ok&&step<=40;++step){
            if(step)ok=RpHAnimHierarchyAddAnimTime(hierarchy,duration/16.f)!=0;
            ok=ok&&std::isfinite(hierarchy->currentAnim->currentTime)&&RpHAnimHierarchyUpdateMatrices(hierarchy)!=0;
            for(int i=0;ok&&i<nodes;++i){RwMatrix* matrix=RwFrameGetLTM(hierarchy->pNodeInfo[i].pFrame);ok=finiteBindingMatrix(matrix);
                if(step==0&&ok)initial.push_back(*matrix);
                else if(ok){const auto& before=initial[i];
                    for(const auto& pair:{std::make_pair(&matrix->right,&before.right),std::make_pair(&matrix->up,&before.up),std::make_pair(&matrix->at,&before.at),std::make_pair(&matrix->pos,&before.pos)})
                        if(std::fabs(pair.first->x-pair.second->x)>1e-6||std::fabs(pair.first->y-pair.second->y)>1e-6||std::fabs(pair.first->z-pair.second->z)>1e-6)result.changed=true;
                }
            }
            if(step&&ok)++result.steps;
        }
    }
    if(attached)RpHAnimHierarchyDetach(hierarchy);
    // Clear the live interpolator's borrowed animation before releasing it.
    // The clump is destroyed immediately after this probe; do not activate NULL.
    if(hierarchy&&hierarchy->currentAnim&&hierarchy->currentAnim->pCurrentAnim==animation)hierarchy->currentAnim->pCurrentAnim=0;
    if(animation)RtAnimAnimationDestroy(animation);
    if(ok){result.nodes=nodes;result.frames=(int)words[5];result.duration=duration;}
    return ok;
}
