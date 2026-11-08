// Read-only native table consumer. Shares the established normalization and
// roundtrip checks; never writes inputs or installs tables.
#define main ContentUnionMain
#include "../../../scripts/tests/content-union-reader.cpp"
#undef main
#include "DungeonTable.h"
#include "ObjectTable.h"
#include "MobServerTable.h"
#include "ItemGroupListTable.h"
#include "WorldPlayTable.h"
#include "NPCServerTable.h"
#include "MobMovePatternTable.h"
#include "ModelToolCharDataTable.h"
#include "NtlActionPattern.h"
#include "CommonConfigTable.h"
#include "StatusTransformTable.h"
#include "HTBSetTable.h"
#include "QuestDropTable.h"
#include "DWCTable.h"
#include "DWCMissionTable.h"
#include <cmath>
#include <sstream>
static_assert(sizeof(sWORLDPLAY_TBLDAT)-sizeof(void*)==16,"WorldPlay serialized Taiwan ABI");
static_assert(sizeof(sOBJECT_TBLDAT)-sizeof(void*)==868,"Object placement serialized Taiwan ABI");
static_assert(sizeof(sNPC_SERVER_TBLDAT)-sizeof(void*)==12,"NPC server serialized Taiwan ABI");
static_assert(sizeof(sMOVE_PATTERN_TBLDAT)-sizeof(void*)==68,"Mob move pattern serialized Taiwan ABI");
static_assert(sizeof(sQUEST_DROP_TBLDAT)-sizeof(void*)==44,"QuestDrop serialized Taiwan ABI");
static_assert(sizeof(sDWC_TBLDAT)-sizeof(void*)==88,"DWC scenario serialized Taiwan ABI");
static_assert(offsetof(sDWC_TBLDAT,aConditionTblidx)-sizeof(void*)==16,"DWC condition offset");
static_assert(offsetof(sDWC_TBLDAT,aMissionTblidx)-sizeof(void*)==56,"DWC mission offset");
static_assert(offsetof(sDWC_TBLDAT,worldTblidx)-sizeof(void*)==84,"DWC world offset");
static_assert(sizeof(sDWCMISSION_REWARD)==108&&sizeof(sDWCMISSION_REWARD_ENTITY)==12,"DWC reward layouts");
static_assert(sizeof(sDWCMISSION_TBLDAT)-sizeof(void*)==632,"DWC mission serialized Taiwan ABI");
static_assert(offsetof(sDWCMISSION_TBLDAT,asReward)-sizeof(void*)==84,"DWC reward array offset");
static_assert(offsetof(sDWCMISSION_TBLDAT,clearObjectTextTblidx)-sizeof(void*)==624,"DWC mission text offset");

// Read-only equality check using the actual GameServer metadata consumer.
// TSV selection: exact model key, existing legacy key, new MODULE_SKILL key.
static int NativeAnimationAliases(const char* directory,const char* selection,const char* referenceDirectory=NULL) {
    CModelToolCharDataTable models;
    Require(models.Create(directory,false,NULL),"native server CharacterProperty load");
    CModelToolCharDataTable reference;
    if(referenceDirectory)Require(reference.Create(referenceDirectory,false,NULL),"native reference CharacterProperty load");
    std::ifstream input(selection);Require(bool(input),"native alias selection");
    std::set<std::string> selected;std::string line;unsigned count=0;
    while(std::getline(input,line)) {
        if(!line.empty()&&line.back()=='\r')line.pop_back();
        Require(!line.empty()&&line.size()<=256&&count<65536,"bounded explicit alias selection");
        const auto first=line.find('\t'),second=first==std::string::npos?first:line.find('\t',first+1);
        Require(first!=std::string::npos&&second!=std::string::npos&&line.find('\t',second+1)==std::string::npos,"three TSV fields");
        const auto name=line.substr(0,first);Require(!name.empty()&&name.size()<=128,"exact model key");
        for(unsigned char character:name)Require(character>=32&&character<=126&&character!='/'&&character!='\\',"literal model key");
        unsigned legacy=0,module=0;std::istringstream numbers(line.substr(first+1));
        Require(bool(numbers>>legacy>>module),"integer alias keys");numbers>>std::ws;
        Require(numbers.eof()&&legacy>0&&legacy<7000&&module>=7000&&module<7400,"legacy to MODULE_SKILL range");
        Require(selected.insert(name+":"+std::to_string(module)).second,"unique destination alias");
        auto* model=models.FindData(name.c_str());Require(model!=NULL,"selected native model exists");
        auto* sourceModel=referenceDirectory?reference.FindData(name.c_str()):model;
        Require(sourceModel!=NULL,"selected native reference model exists");
        const auto* source=sourceModel->GetAnim(0,static_cast<ANIMATIONID>(legacy));
        const auto* alias=model->GetAnim(0,static_cast<ANIMATIONID>(module));
        Require(source&&alias,"both metadata lookup keys exist");
        Require(source->bDataLoaded==alias->bDataLoaded&&source->bDataLoaded,"both metadata entries loaded");
        Require(source->fDurationTime==alias->fDurationTime&&source->byHitCount==alias->byHitCount&&
            source->byPushCount==alias->byPushCount&&source->byKnockDownCount==alias->byKnockDownCount&&
            source->bKB2Push==alias->bKB2Push&&source->fSkillCancelTime==alias->fSkillCancelTime&&
            source->fKnockdownHitTime==alias->fKnockdownHitTime,"native animation timing/hit/cancel equality");
        Require(source->byHitCount<=NTL_MAX_SIZE_HIT&&
            !memcmp(source->aHitTime,alias->aHitTime,sizeof(float)*source->byHitCount),"native hit timeline equality");
        Require(source->firstHitProjectile==alias->firstHitProjectile&&
            source->firstProjectileType==alias->firstProjectileType&&
            source->firstProjectileSpeed==alias->firstProjectileSpeed&&
            source->firstProjectileTargetHeight==alias->firstProjectileTargetHeight&&
            !memcmp(source->firstProjectileBone,alias->firstProjectileBone,sizeof(source->firstProjectileBone)),"native first projectile equality");
        ++count;
    }
    Require(input.eof()&&count>0,"complete nonempty alias selection");
    printf("PASS native GameServer animation aliases=%u; duration/hits/cancel/projectile fields match each original; no rendering/world claim\n",count);
    return 0;
}

static unsigned engineerWarnings=0;
static void EngineerWarning(char* message,void*) { ++engineerWarnings; fprintf(stderr,"TABLE_WARNING %s\n",message); }
static int EngineerDependencies(const char* directory,const char* idsPath,const char* propertyDirectory) {
    CSkillTable skills;CSystemEffectTable effects;CNPCTable npcs;
    CCommonConfigTable common;CStatusTransformTable status;CHTBSetTable htb;
    std::string root=directory;
    CTable::SetErrorCallbackFunction(EngineerWarning);
    Load(skills,root+"/table_skill_data.rdf");Load(effects,root+"/table_system_effect_data.rdf");
    Load(npcs,root+"/table_npc_data.rdf");Load(common,root+"/table_common_config_data.rdf");
    Load(status,root+"/table_status_transform_data.rdf");Load(htb,root+"/table_htb_set_data.rdf");
    CModelToolCharDataTable models;
    Require(models.Create(propertyDirectory,false,NULL),"native server CharacterProperty load");
    std::ifstream input(idsPath);Require(bool(input),"selected skill IDs file");
    std::set<unsigned> ids;unsigned id=0,missing=0;
    while(input>>id)Require(ids.insert(id).second,"unique selected skill IDs");
    Require(input.eof()&&!ids.empty(),"bounded integer skill ID list");
    auto check=[&](bool ok,const char* kind,unsigned consumer,unsigned target) {
        printf("DEPENDENCY %s skill=%u target=%u %s\n",kind,consumer,target,ok?"OK":"MISSING");
        if(!ok)++missing;
    };
    for(unsigned key:ids) {
        auto* row=static_cast<sSKILL_TBLDAT*>(skills.FindData(key));check(row!=NULL,"skill",key,key);if(!row)continue;
        const unsigned animationKeys[]={row->wCasting_Animation_Start,row->wCasting_Animation_Loop,
            row->wAction_Animation_Index,row->wAction_Loop_Animation_Index,row->wAction_End_Animation_Index};
        for(const char* modelKey:{"A_HEN_M_AD","A_HEN_F_AD","A_HEN_M_CH","A_HEN_F_CH"}) {
            auto* model=models.FindData(modelKey);check(model!=NULL,"actor-model",key,0);if(!model)continue;
            for(unsigned animation:animationKeys) {
                if(animation==0||animation==INVALID_WORD)continue;
                auto* data=model->GetAnim(0,static_cast<ANIMATIONID>(animation));
                check(data&&data->bDataLoaded&&std::isfinite(data->fDurationTime)&&data->fDurationTime>0,
                    modelKey,key,animation);
            }
        }
        for(unsigned n=0;n<NTL_MAX_EFFECT_IN_SKILL;++n) {
            unsigned effect=row->skill_Effect[n];if(effect==INVALID_TBLIDX||effect==0)continue;
            auto* e=static_cast<sSYSTEM_EFFECT_TBLDAT*>(effects.FindData(effect));check(e!=NULL,"effect",key,effect);if(!e)continue;
            check(e->effectCode!=INVALID_SYSTEM_EFFECT_CODE,"effect-code",key,effect);
            if(e->effectCode==ACTIVE_RABIES||e->effectCode==ACTIVE_DRUNK) {
                unsigned value=static_cast<unsigned>(row->aSkill_Effect_Value[n]);
                check(common.FindCommonConfig(value)!=NULL,"common-config",key,value);
            }
            if(e->effectCode==ACTIVE_SUMMON) {
                unsigned value=static_cast<unsigned>(row->aSkill_Effect_Value[n]);
                auto* npc=static_cast<sNPC_TBLDAT*>(npcs.FindData(value));check(npc!=NULL,"summon-npc",key,value);if(!npc)continue;
                check(models.FindData(npc->szModel)!=NULL,"summon-model",key,value);
                if(npc->statusTransformTblidx!=INVALID_TBLIDX&&npc->statusTransformTblidx!=0)
                    check(status.FindData(npc->statusTransformTblidx)!=NULL,"summon-status",key,npc->statusTransformTblidx);
                for(unsigned slot=0;slot<NTL_MAX_NPC_HAVE_SKILL;++slot) {
                    unsigned skill=npc->use_Skill_Tblidx[slot];
                    if(skill!=INVALID_TBLIDX&&skill!=0)check(skills.FindData(skill)!=NULL,"summon-skill",key,skill);
                }
            }
        }
        if(row->bySkill_Class==2) {
            auto* set=static_cast<sHTB_SET_TBLDAT*>(htb.FindData(key));check(set!=NULL,"htb-set",key,key);if(!set)continue;
            check(set->bySetCount<=NTL_HTB_MAX_SKILL_COUNT_IN_SET,"htb-count",key,set->bySetCount);
            for(unsigned n=0;n<set->bySetCount&&n<NTL_HTB_MAX_SKILL_COUNT_IN_SET;++n)
                if(set->aHTBAction[n].skillTblidx!=INVALID_TBLIDX)
                    check(skills.FindData(set->aHTBAction[n].skillTblidx)!=NULL,"htb-action",key,set->aHTBAction[n].skillTblidx);
        }
    }
    printf("ENGINEER_SUMMARY selected=%zu missing=%u unrelatedLoaderWarnings=%u worldTested=false\n",ids.size(),missing,engineerWarnings);
    return missing?3:0;
}

template<> bool Excluded<sMOB_TBLDAT>(const char* raw) {
    sMOB_TBLDAT row;memcpy(row.GetDataAddress(),raw,sizeof(row)-sizeof(void*));
    Require(row.bValidity_Able==0||row.bValidity_Able==1,"MOB validity flag");
    return !row.bValidity_Able;
}
// CMobServerTable::AddTable retains every row regardless of bValidity_Able;
// unlike CMobTable, it has no disabled-row exclusion. Keep the default codec.

int main(int argc,char**argv) {
    if(argc<3){fprintf(stderr,"usage: table-candidate-reader <mob|skill|world|dungeon|worldplay|npc|npc-server|spawn|mob-server|item-group|move-pattern|text> <before.rdf> <candidate.rdf> | table-candidate-reader model-tool-char <characterproperty-dir>\n");return 2;}
    std::string kind=argv[1];
    if(kind=="model-tool-char-cross-property-aliases") {
        if(argc!=5)return 2;
        return NativeAnimationAliases(argv[3],argv[4],argv[2]);
    }
    if(kind=="model-tool-char-module-readback") {
        if(argc!=4)return 2;
        CModelToolCharDataTable models;Require(models.Create(argv[2],false,NULL),"native CharacterProperty load");
        std::ifstream selection(argv[3]);Require(bool(selection),"module selection file");
        std::string modelName;unsigned key=0,count=0;std::set<std::string> seen;
        while(selection>>modelName>>key) {
            Require(++count<=65536&&!modelName.empty()&&modelName.size()<=128&&key>=7000&&key<7400,"bounded module selection");
            Require(seen.insert(modelName+":"+std::to_string(key)).second,"unique selected module key");
            auto* model=models.FindData(modelName.c_str());Require(model!=NULL,"selected module model exists");
            const auto* animation=model->GetAnim(0,static_cast<ANIMATIONID>(key));
            Require(animation&&animation->bDataLoaded&&std::isfinite(animation->fDurationTime)&&animation->fDurationTime>=0,"selected module entry loaded with finite duration");
            Require(animation->byHitCount<=NTL_MAX_SIZE_HIT&&std::isfinite(animation->fSkillCancelTime),"bounded hit and cancel metadata");
            for(unsigned hit=0;hit<animation->byHitCount;++hit)Require(std::isfinite(animation->aHitTime[hit]),"finite hit timeline");
            printf("ANIMATION\t%s\t%u\t%.9g\t%u\t%.9g\n",modelName.c_str(),key,animation->fDurationTime,animation->byHitCount,animation->fSkillCancelTime);
        }
        Require(selection.eof()&&count>0,"complete nonempty module selection");
        printf("PASS native module metadata entries=%u; no rendering/world claim\n",count);return 0;
    }
    if(kind=="dwc-readback"||kind=="dwc-mission-readback") {
        if(argc!=3)return 2;
        CTable::SetErrorCallbackFunction(Error);
        if(kind=="dwc-readback"){CDwcTable table;Check<CDwcTable,sDWC_TBLDAT>(table,argv[2]);}
        else {CDwcMissionTable table;Check<CDwcMissionTable,sDWCMISSION_TBLDAT>(table,argv[2]);}
        printf("Native DWC ABI/rows/roundtrip only; scenario gameplay and rewards are not certified.\n");return 0;
    }
    if(kind=="spawn-readback"||kind=="object-spawn-readback") {
        if(argc!=3)return 2;
        CTable::SetErrorCallbackFunction(Error);
        if(kind=="spawn-readback"){CSpawnTable table;Check<CSpawnTable,sSPAWN_TBLDAT>(table,argv[2]);}
        else {CObjectTable table;Check<CObjectTable,sOBJECT_TBLDAT>(table,argv[2]);}
        printf("Native placement ABI/rows/roundtrip only; no historical absent baseline, scripts or world claim.\n");return 0;
    }
    if(kind=="skill-readback") {
        if(argc!=3)return 2;
        CTable::SetErrorCallbackFunction(Error);CSkillTable table;
        Check<CSkillTable,sSKILL_TBLDAT>(table,argv[2]);
        printf("Candidate native load/full-row readback/roundtrip only; compare the reviewed field patch separately.\n");
        return 0;
    }
    if(kind=="model-tool-char-aliases") {
        if(argc!=4)return 2;
        return NativeAnimationAliases(argv[2],argv[3]);
    }
    if(kind=="engineer-dependencies") {
        if(argc!=5)return 2;
        return EngineerDependencies(argv[2],argv[3],argv[4]);
    }
    if((kind=="model-tool-char"&&argc!=3)||(kind!="model-tool-char"&&argc!=4)){fprintf(stderr,"invalid argument count for %s mode\n",kind.c_str());return 2;}
    CTable::SetErrorCallbackFunction(Error);
    if(kind=="mob"){CMobTable t;Check<CMobTable,sMOB_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="skill"){CSkillTable t;Check<CSkillTable,sSKILL_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="world"){CWorldTable t;Check<CWorldTable,sWORLD_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="worldplay"){
        CWorldPlayTable t;Check<CWorldPlayTable,sWORLDPLAY_TBLDAT>(t,argv[3],argv[2]);
        for(unsigned id:{81000u,83000u,86150u,86151u}){
            auto* row=static_cast<sWORLDPLAY_TBLDAT*>(t.FindData(id));
            if(row)printf("WORLDPLAY %u group=%u executer=%u share=%u shareLimitTime=%u\n",row->tblidx,row->dwGroup,row->byExecuterType,row->byShareType,row->dwShareLimitTime);
            else printf("WORLDPLAY %u missing\n",id);
        }
    }
    else if(kind=="npc-server"){
        CNpcServerTable t;Check<CNpcServerTable,sNPC_SERVER_TBLDAT>(t,argv[3],argv[2]);
        for(unsigned id:{3701201u,1260150u}){
            auto* row=static_cast<sNPC_SERVER_TBLDAT*>(t.FindData(id));
            if(row)printf("NPC_SERVER %u valid=%u serverFlags=%u\n",row->tblidx,row->bValidity_Able?1u:0u,row->dwServerBitFlag);
            else printf("NPC_SERVER %u missing\n",id);
        }
    }
    else if(kind=="move-pattern"){
        CMobMovePatternTable t;Check<CMobMovePatternTable,sMOVE_PATTERN_TBLDAT>(t,argv[3],argv[2]);
        auto* row=static_cast<sMOVE_PATTERN_TBLDAT*>(t.FindData(1));
        Require(row!=NULL,"required move pattern row 1");
        bool terminated=false;
        printf("MOVE_PATTERN %u sequence=",row->tblidx);
        for(size_t i=0;i<DBO_MAX_COUNT_MOVE_PATTERN;++i){
            const BYTE action=row->abyPattern[i];
            if(action==INVALID_BYTE){terminated=true;break;}
            Require(action<COUNT_ACTIONPATTERNID,"supported action-pattern enum value");
            printf("%s%u",i?",":"",static_cast<unsigned>(action));
        }
        Require(terminated,"move pattern terminates within its fixed array");
        printf("\n");
    }
    else if(kind=="model-tool-char"){
        CModelToolCharDataTable table;
        Require(table.Create(argv[2],false,NULL),"GameServer ModelTool CharacterProperty loader");
        const char* keys[]={"M_GKU_A3","M_BEJ_A9","M_REJ_D6","M_CHL_B2","M_NZA_C2","M_FRO_C2","M_BOW_D2","M_TRT_B1","M_BDK_B1","M_MRA_A1","N_INT_A2"};
        for(const char* key:keys){
            auto* row=table.FindData(key);
            Require(row!=NULL,key);
            const float values[]={row->m_vMin.x,row->m_vMin.y,row->m_vMin.z,row->m_vMax.x,row->m_vMax.y,row->m_vMax.z,row->m_fScale};
            for(float value:values)Require(std::isfinite(value),"finite ModelTool bounds and scale");
            const float x=(row->m_vMax.x-row->m_vMin.x)*0.5f;
            const float y=(row->m_vMax.y-row->m_vMin.y)*0.5f;
            const float z=(row->m_vMax.z-row->m_vMin.z)*0.5f;
            const float radius=std::sqrt(x*x+y*y+z*z);
            Require(row->m_vMax.x>=row->m_vMin.x&&row->m_vMax.y>=row->m_vMin.y&&row->m_vMax.z>=row->m_vMin.z&&std::isfinite(radius),"ordered ModelTool bounds and finite radius");
            printf("MODEL_TOOL_CHAR %s min=%.6f,%.6f,%.6f max=%.6f,%.6f,%.6f scale=%.6f extents=%.6f,%.6f,%.6f radius=%.6f\n",key,row->m_vMin.x,row->m_vMin.y,row->m_vMin.z,row->m_vMax.x,row->m_vMax.y,row->m_vMax.z,row->m_fScale,row->m_vMax.x-row->m_vMin.x,row->m_vMax.y-row->m_vMin.y,row->m_vMax.z-row->m_vMin.z,radius);
        }
    }
    else if(kind=="dungeon"){CDungeonTable t;Check<CDungeonTable,sDUNGEON_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="quest-drop"){CQuestDropTable t;Check<CQuestDropTable,sQUEST_DROP_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="npc"){CNPCTable t;Check<CNPCTable,sNPC_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="spawn"){CSpawnTable t;Check<CSpawnTable,sSPAWN_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="object-spawn"){CObjectTable t;Check<CObjectTable,sOBJECT_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="mob-server"){CMobServerTable t;Check<CMobServerTable,sMOB_SERVER_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="item-group"){CItemGroupListTable t;Check<CItemGroupListTable,sITEM_GROUP_LIST_TBLDAT>(t,argv[3],argv[2]);}
    else if(kind=="text"){
        char* args[]={argv[0],const_cast<char*>("--all-texts-additive"),argv[2],argv[3]};
        return ContentUnionMain(4,args);
    } else return 2;
    printf("Native consumer only; no client startup, rendering, authentication or gameplay acceptance.\n");
    return 0;
}
