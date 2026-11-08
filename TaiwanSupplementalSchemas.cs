namespace DboAssetsStudio;

// Native ItemRecipe84 field/offset proof: x86/x64 CItemRecipeTable.
// Keep taiwan.txt bytes stable.
internal static class TaiwanSupplementalSchemas
{
    internal static IEnumerable<string> Lines()
    {
        // Pack(4) layouts from the matching production headers in
        // DboShared/NtlGameTable; native offset proof: server-refresh-layout.cpp.
        yield return "ChattingFilter;table_chatting_filter_data.rdf;Table_Data_KOR;tblidx,int,1;wszSlangText,wstring,33;pad_70,byte,2;filteringTextIndex,int,1";
        yield return "DragonBallReturnPoint;table_db_returnpoint.rdf;Table_Data_KOR;tblidx,int,1;byScatterPoint,byte,1;pad_5,byte,3;fField_X,float,1;fField_Y,float,1;fField_Z,float,1";
        yield return "DragonBallReward;Table_DB_Reward_Data.rdf;Table_Data_KOR;tblidx,int,1;byBallType,byte,1;byRewardCategoryDepth,byte,1;pad_6,byte,2;rewardCategoryName,int,1;rewardCategoryDialog,int,1;byRewardType,byte,1;pad_17,byte,3;rewardName,int,1;rewardLinkTblidx,int,1;dwRewardZenny,int,1;rewardDialog1,int,1;rewardDialog2,int,1;dwClassBit,int,1";
        yield return "DragonBall;table_dragon_ball_data.rdf;Table_Data_KOR;tblidx,int,1;dwAltarGroup,int,1;byBallType,byte,1;pad_9,byte,3;aBallTblidx,int,7;ballDropTblidx,int,1;ballJunkTblidx,int,1;startDialog,int,1;endDialog,int,1;timeoverEndDialog,int,1;hurryDialog,int,1;timeoverDialog,int,1;noRepeatDialog,int,1;inventoryFullDialog,int,1;skillOverlapDialog,int,1;skillShortageOfLVDialog,int,1;dragonNPCTblidx,int,1;defaultSummonChat,int,1;fDir,float,2";
        yield return "Exp;table_exp_data.rdf;Table_Data_KOR;tblidx,int,1;dwExp,int,1;dwNeed_Exp,int,1;sIndividualRankPoint,short,5;sTeamRankPoint,short,5;wNormal_Race,short,1;wSuperRace,short,1;dwMobExp,int,1;dwPhyDefenceRef,int,1;dwEngDefenceRef,int,1;dwMobZenny,int,1";
        yield return "ItemMixExp;Table_Item_Mix_Exp_Data.rdf;Table_Data_KOR;tblidx,int,1;dwNeedEXP,int,1;byUnknown,byte,1;pad_9,byte,3";
        yield return "QuestItem;Table_Quest_Item_Data.rdf;Table_Data_KOR;tblidx,int,1;ItemName,int,1;szIconName,string,33;pad_41,byte,3;Note,int,1;byFunctionBitFlag,byte,1;pad_49,byte,3";
        yield return "ScriptLink;table_script_link_data.rdf;Table_Data_KOR;tblidx,int,1;wszValue,wstring,65;byType,byte,1;byAction,byte,1";
        yield return "SlotMachine;table_slot_machine_data.rdf;Table_Data_KOR;tblidx,int,1;dwName,int,1;wszNameText,wstring,45;szFile_Name,string,42;byCoin,short,1;bOnOff,bool,1;byType,byte,1;wfirstWinCoin,short,1;pad_146,byte,2;aItemTblidx,int,10;byStack,byte,10;wQuantity,short,10;pad_218,byte,2";
        var timeQuest = new List<string> { "tblidx,int,1", "byTimeQuestType,byte,1", "byDifficultyFlag,byte,1", "pad_6,byte,2", "dwStartTime,int,1", "startCharacterDirection,int,1", "startObjectIndex,int,1", "startTriggerId,int,1", "abyStartTriggerDirectionState,byte,10", "pad_34,byte,2", "arriveCharacterDirection,int,1", "arriveObjectIndex,int,1", "arriveTriggerId,int,1", "leaveCharacterDirection,int,1", "leaveObjectIndex,int,1", "leaveTriggerId,int,1", "dayRecordMailTblidx,int,1", "bestRecordMailTblidx,int,1", "byResetTime,byte,1", "pad_69,byte,1", "wszPrologueDirection,wstring,33" };
        for (int i=0;i<3;i++)
        {
            string p=$"sTimeQuestDataset[{i}]";
            timeQuest.AddRange([$"{p}.nameTblidx,int,1", $"{p}.questStringTblidx,int,1", $"{p}.worldTblidx,int,1", $"{p}.scriptTblidx,int,1", $"{p}.byMinMemberCount,byte,1", $"{p}.byMaxMemberCount,byte,1", $"{p}.byMinMemberLevel,byte,1", $"{p}.byMaxMemberLevel,byte,1", $"{p}.dwLimitTime,int,1", $"{p}.dwNeedZenny,int,1", $"{p}.needItemTblidx,int,1", $"{p}.byNeedLimitCount,byte,1", $"{p}.byWorldCount,byte,1", $"pad_dataset_{i},byte,2", $"{p}.dayRecordRewardTblidx,int,1", $"{p}.bestRecordRewardTblidx,int,1"]);
        }
        timeQuest.AddRange(["openCine,int,1", "Note,int,1", "wszStageBgm1,wstring,33", "wszStageBgm2,wstring,33", "wszLastBgm,wstring,33", "pad_474,byte,2"]);
        yield return "TimeQuest;Table_TMQ_Data.rdf;Table_Data_KOR;"+string.Join(';',timeQuest);
        // ActionTable.h / ActionTable.cpp: sACTION_TBLDAT, pack(4),
        // GetDataSize excludes the vptr; serialized payload is exactly 60 bytes.
        yield return "Action;table_action_data.rdf;Table_Data_KOR;tblidx,int,1;Validity_Able,bool,1;Action_Type,byte,1;pad_10,byte,2;Action_Name,int,1;Icon_Name,string,33;pad_49,byte,3;Note,int,1;Chat_Command_Index,int,1;ETC_Action_Type,byte,1;pad_61,byte,3";
        // ChatCommandTable.h/.cpp: sCHAT_COMMAND_TBLDAT, pack(4),
        // GetDataSize excludes the vptr; serialized payload is exactly 40 bytes.
        yield return "ChatCommand;table_chat_command_data.rdf;Table_Data_KOR;tblidx,int,1;Validity_Able,bool,1;pad_5,byte,1;Action_Animation_Index,short,1;aChat_Command,int,8";
        // DWCTable.h/NtlDWC.h: pack(4), 10 conditions and five missions.
        // Offsets/widths are also asserted by the native table reader.
        yield return "Dwc;table_dwc_data.rdf;Table_Data_KOR;tblidx,int,1;tblNameIndex,int,1;" +
            "byLevel_Min,byte,1;byLevel_Max,byte,1;wAdmission_Bit_Flag,short,1;" +
            "byAdmission_Num_Min,byte,1;byAdmission_Num_Max,byte,1;pad_14,byte,2;" +
            "aConditionTblidx,int,10;aMissionTblidx,int,5;prologueCinematicTblidx,int,1;prologueTblidx,int,1;worldTblidx,int,1";
        var dwcMission = new List<string> { "tblidx,int,1", "tblNameIndex,int,1", "szImageName,string,65",
            "pad_73,byte,3", "tblContainScenarioIndex,int,1", "byCompleteMinNum,byte,1",
            "byCompleteMaxNum,byte,1", "byDifficulty,byte,1", "pad_83,byte,1" };
        for (int mission=0;mission<5;++mission)
        {
            string prefix=$"asReward[{mission}]";
            dwcMission.AddRange([$"{prefix}.cardNameTblidx,int,1", $"{prefix}.byRequireCount,byte,1",
                $"pad_reward_{mission}_require,byte,3", $"{prefix}.fAcquireRate,float,1"]);
            foreach(string rewardArray in new[]{"asBasicReward","asRepeatReward"})
                for(int entity=0;entity<4;++entity)
                {
                    string item=$"{prefix}.{rewardArray}[{entity}]";
                    dwcMission.AddRange([$"{item}.byType,byte,1", $"pad_reward_{mission}_{rewardArray}_{entity}_type,byte,3",
                        $"{item}.tblidx,int,1", $"{item}.byValue,byte,1", $"pad_reward_{mission}_{rewardArray}_{entity}_value,byte,3"]);
                }
        }
        dwcMission.AddRange(["clearObjectTextTblidx,int,1", "clearConditionTextTblidx,int,1"]);
        yield return "DwcMission;table_dwc_mission_data.rdf;Table_Data_KOR;"+string.Join(';',dwcMission);
        // QuestDropTable.h: five uint32 quest-item references and five float
        // probabilities after tblidx. Serialized ABI44, pack(4), both builds.
        yield return "QuestDrop;table_quest_drop_data.rdf;Table_Data_KOR;tblidx,int,1;aQuestItemTblidx,int,5;aDropRate,float,5";
        // NewbieTable.h: native serialized row200, items10/skills7/quick5x6.
        // PORTALID is one byte; its three-element array ends at189, then3 pad.
        var newbie = new List<string> { "tblidx,int,1", "byRace,byte,1", "byClass,byte,1", "pad_6,byte,2",
            "world_Id,int,1", "tutorialWorld,int,1", "vSpawn_Loc,float,3", "vSpawn_Dir,float,3",
            "vBind_Loc,float,3", "vBind_Dir,float,3", "aitem_Tblidx,int,10", "abyPos,byte,10",
            "abyStack_Quantity,byte,10", "mapNameTblidx,int,1", "aSkillTblidx,int,7" };
        for (int i=0;i<5;++i) newbie.AddRange([$"asQuickData[{i}].tbilidx,int,1", $"asQuickData[{i}].byType,byte,1", $"asQuickData[{i}].byQuickSlot,byte,1"]);
        newbie.AddRange(["defaultPortalId,byte,3", "pad_189,byte,3", "qItemTblidx1,int,1", "byQPosition1,byte,1", "byQStackQuantity1,byte,1", "wMixLevelData,short,1"]);
        yield return "Newbie;table_newbie_data.rdf;Table_Data_KOR;" + string.Join(';', newbie);
        // Native pack(4) layouts used by the cumulative map/table integration.
        // Reserved spans are kept immutable; no inferred layout or TSV writer.
        yield return "Dungeon;Table_Dungeon_Data.rdf;Table_Data_KOR;tblidx,int,1;byDungeonType,byte,1;byMaxMember,byte,1;pad_6,byte,2;linkWorld,int,1;byMinLevel,byte,1;byMaxLevel,byte,1;pad_14,byte,2;needItemTblidx,int,1;dwHonorPoint,int,1;wpsTblidx,int,1;openCine,int,1;groupIdx,int,1";
        yield return "Portal;Table_Portal_Data.rdf;Table_Data_KOR;tblidx,int,1;dwPointName,int,1;szPointNameText,string,41;pad_49,byte,3;dwUnknown,int,1;worldId,int,1;byGrade,byte,1;pad_61,byte,3;vLoc,float,3;vDir,float,3;vMap,float,3;aPoint,byte,70;pad_170,byte,2;adwPointZenny,int,70";
        yield return "WorldMap;Table_Worldmap_Data.rdf;Table_Data_KOR;tblidx,int,1;World_Tblidx,int,1;Zone_Tblidx,int,1;Worldmap_Name,int,1;wszNameText,wstring,33;bValidityAble,bool,1;byMapType,byte,1;vStandardLoc,float,3;fWorldmapScale,float,1;dwLinkMapIdx,int,1;dwComboBoxType,int,1;wWarfog,short,8;byRecomm_Min_Level,byte,1;byRecomm_Max_Level,byte,1;pad_126,byte,2;vUiModify,float,3";
        yield return "Landmark;table_landmark_data.rdf;Table_Data_KOR;tblidx,int,1;wszNameText,wstring,41;pad_86,byte,2;LandmarkName,int,1;byLandmarkType,byte,1;bValidityAble,bool,1;byLandmarkBitflag,byte,1;byLandmarkDisplayBitFlag,byte,1;LandmarkLoc,float,3;LinkMapIdx,int,1;ZoneMapIdx,int,1;wLinkWarfogIdx,short,1;wszIconName,wstring,33;byIconSize,byte,1;pad_185,byte,3;Note,int,1";
        yield return "WorldPlay;table_world_play_data.rdf;Table_Data_KOR;tblidx,int,1;dwGroup,int,1;byExecuterType,byte,1;byShareType,byte,1;pad_10,byte,2;dwShareLimitTime,int,1";
        yield return "ItemDisassemble;table_item_disassemble_data.rdf;Table_Data_KOR;tblidx,int,1;ItemTblidxResult,int,3;ByMat2Rate,byte,1;ByMat3Rate,byte,1;pad_18,byte,2";
        var selectedRewards = new List<string> { "tblidx,int,1", "bySelect_Type,byte,1", "pad_5,byte,3" };
        for (int i=0;i<20;++i) selectedRewards.AddRange([$"aRewardSet[{i}].byRewardType,byte,1", $"pad_reward_{i},byte,3", $"aRewardSet[{i}].dwRewardIdx,int,1", $"aRewardSet[{i}].dwRewardVal,int,1"]);
        yield return "QuestRewardSelect;Table_Quest_Reward_Select_Data.rdf;Table_Data_KOR;" + string.Join(';', selectedRewards);
        // StatusTransformTable.h, pack(4): serialized row76 excludes the vptr.
        // HTBSetTable.h, pack(4), ten sHTB_ACTION(byte + pad3 + DWORD).
        var htb = new List<string> { "tblidx,int,1", "wszNameText,wstring,41", "pad_86,byte,2",
            "HTB_Skill_Name,int,1", "bValidityAble,bool,1", "pad_93,byte,3", "dwPC_Class_Bit_Flag,int,1",
            "bySlot_Index,byte,1", "bySkill_Grade,byte,1", "szIcon_Name,string,33", "pad_135,byte,1",
            "wNeed_EP,short,1", "byRequire_Train_Level,byte,1", "pad_139,byte,1", "dwRequire_Zenny,int,1",
            "wNext_Skill_Train_Exp,short,1", "wCool_Time,short,1", "dwCoolTimeInMilliSecs,int,1", "Note,int,1",
            "bySetCount,byte,1", "byStop_Point,byte,1", "pad_158,byte,2" };
        for (int i = 0; i < 10; i++) htb.AddRange([$"aHTBAction[{i}].bySkillType,byte,1", $"pad_htb_{i},byte,3", $"aHTBAction[{i}].skillTblidx,int,1"]);
        htb.AddRange(["wRequireSP,short,1", "pad_242,byte,2"]);
        yield return "HTBSet;table_htb_set_data.rdf;Table_Data_KOR;" + string.Join(';', htb);
        // The complete native row includes both duration fields; neither is discarded.
        yield return "StatusTransform;table_status_transform_data.rdf;Table_Data_KOR;" +
            "tblidx,int,1;fLP_Transform,float,1;fEP_Transform,float,1;" +
            "fPhysical_Offence_Transform,float,1;fEnergy_Offence_Transform,float,1;" +
            "fPhysical_Defence_Transform,float,1;fEnergy_Defence_Transform,float,1;" +
            "fRun_Speed_Transform,float,1;fAttack_Speed_Transform,float,1;" +
            "fAttack_Rate_Transform,float,1;fDodge_Rate_Transform,float,1;fBlock_Rate_Transform,float,1;" +
            "fCurse_Success_Transform,float,1;fCurse_Tolerance_Transform,float,1;fAttack_Range_Change,float,1;" +
            "fLP_Consume_Rate,float,1;fEP_Consume_Rate,float,1;dwDuration,int,1;dwDurationInMilliSecs,int,1";
        // Native ItemGroupListTable296 / ItemBagListTable256, pack(4).
        // These references are pools and probabilities, not direct Item IDs.
        yield return "ItemGroupList;table_item_group_list_data.rdf;0;tblidx,int,1;wszName,wstring,41;byLevel,byte,1;byTry_Count,byte,1;" +
            "mob_Index,int,1;dwMob_Type,int,1;dwWorld_Rule_Type,int,1;dwInterval,int,1;dwSuperior,int,1;dwExcellent,int,1;dwRare,int,1;" +
            "dwLegendary,int,1;dwNo_Drop,int,1;dwZenny,int,1;aItemBag,int,20;adwProb,int,20;dwItemBagCount,int,1;dwTotalProb,int,1";
        yield return "ItemBagList;table_item_bag_list_data.rdf;0;tblidx,int,1;wszName,wstring,41;byLevel,byte,1;bEnchant_Able,bool,1;" +
            "aItem,int,20;adwProb,int,20;dwItemCount,int,1;dwTotalProb,int,1";
        // The NPC and MOB consumers share CSpawnTable/sSPAWN_TBLDAT84.
        // Keep the NPC reference name explicit in the editing surface.
        const string npcSpawn = "NpcSpawn;spawn_npc.rdf;0;tblidx,int,1;npc_tblidx,int,1;vSpawn_Loc,float,3;vSpawn_Dir,float,3;" +
            "bySpawnLocRange,byte,1;bySpawnQuantity,byte,1;wSpawnCoolTime,short,1;bySpawnMoveType,byte,1;byWanderRange,byte,1;" +
            "byMoveRange,byte,1;actionPatternTblidx,byte,1;pathTableIndex,int,1;vFollowDistance,float,3;playScript,int,1;playScriptScene,int,1;" +
            "aiScript,int,1;aiScriptScene,int,1;dwPartyIndex,int,1;bPartyLeader,bool,1;pad_77,byte,3;spawnGroup,int,1";
        yield return npcSpawn;
        yield return npcSpawn.Replace("NpcSpawn;spawn_npc.rdf", "MobSpawn;spawn_mob.rdf").Replace("npc_tblidx", "mob_tblidx");
        // NpcTable.h: common sBOT_TBLDAT prefix is the existing native MOB
        // catalog's first444 bytes. npc-table-layout.cpp reports the NPC tail
        // and serialized row500 on both x86/x64; six merchant tabs are DWORDs.
        yield return "NpcData;table_npc_data.rdf;Table_Data_KOR;" +
            "tblidx,int,1;dwBasic_LP,int,1;wBasic_EP,short,1;wBasic_Physical_Defence,short,1;wBasic_Energy_Defence,short,1;" +
            "wBasicStr,short,1;wBasicCon,short,1;wBasicFoc,short,1;wBasicDex,short,1;wBasicSol,short,1;wBasicEng,short,1;pad_26,byte,2;" +
            "fScale,float,1;wAttack_Speed_Rate,short,1;byAttack_Type,byte,1;pad_35,byte,1;fAttack_Range,float,1;wAttack_Rate,short,1;" +
            "wDodge_Rate,short,1;wBlock_Rate,short,1;wCurse_Success_Rate,short,1;wCurse_Tolerance_Rate,short,1;pad_50,byte,2;fRadius,float,1;" +
            "wBasic_Aggro_Point,short,1;pad_58,byte,2;bValidity_Able,bool,1;pad_61,byte,3;Name,int,1;wszNameText,wstring,41;szModel,string,33;" +
            "byLevel,byte,1;byGrade,byte,1;pad_185,byte,3;dwAi_Bit_Flag,int,1;wLP_Regeneration,short,1;wEP_Regeneration,short,1;" +
            "byAttack_Animation_Quantity,byte,1;byBattle_Attribute,byte,1;wBasic_Physical_Offence,short,1;wBasic_Energy_Offence,short,1;pad_202,byte,2;" +
            "fWalk_Speed_Origin,float,1;fWalk_Speed,float,1;fRun_Speed_Origin,float,1;fRun_Speed,float,1;fRadius_X,float,1;fRadius_Z,float,1;" +
            "wSight_Range,short,1;wScan_Range,short,1;wUse_Skill_Time,short,7;pad_246,byte,2;use_Skill_Tblidx,int,7;byUse_Skill_Basis,byte,7;pad_283,byte,1;" +
            "wUse_Skill_LP,short,7;byVisible_Sight_Range,byte,1;szCamera_Bone_Name,string,33;wAttackCoolTime,short,1;pad_334,byte,2;fFly_Height,float,1;" +
            "szNameText,string,41;bSpawn_Animation,bool,1;pad_382,byte,2;dwDialogGroup,int,1;szILLust,string,33;pad_421,byte,3;dwAllianceIdx,int,1;" +
            "wAggroMaxCount,short,1;pad_430,byte,2;dwNpcAttributeFlag,int,1;wStomachacheDefence,short,1;wPoisonDefence,short,1;wBleedDefence,short,1;wBurnDefence,short,1;" +
            "byNpcType,byte,1;byJob,byte,1;pad_446,byte,2;dwFunc_Bit_Flag,int,1;Dialog_Script_Index,int,1;amerchant_Tblidx,int,6;" +
            "statusTransformTblidx,int,1;contentsTblidx,int,1;wUnknown,short,1;pad_490,byte,2;dwUnknown2,int,1;dwUnknown3,int,1";
        // NPCServerTable.h, pack(4), serialized payload12 on x86/x64.
        yield return "NpcServerTable;table_npc_data_server.rdf;Table_Data_KOR;" +
            "tblidx,int,1;bValidity_Able,bool,1;pad_5,byte,3;dwServerBitFlag,int,1";
        // MobServerTable.h, pack(4), serialized payload12 on both native ABIs.
        yield return "MobDataServer;table_mob_data_server.rdf;Table_Data_KOR;" +
            "tblidx,int,1;bValidity_Able,bool,1;pad_5,byte,3;dwServerBitFlag,int,1";
        // DungeonTable.h, pack(4): serialized payload excludes the vptr.
        // Difficulty 1=normal, 2=hard; linkWorld and groupIdx select the variant.
        yield return "DungeonTable;table_dungeon_data.rdf;Table_Data_KOR;" +
            "tblidx,int,1;byDungeonType,byte,1;byMaxMember,byte,1;pad_6,byte,2;" +
            "linkWorld,int,1;byMinLevel,byte,1;byMaxLevel,byte,1;pad_14,byte,2;" +
            "needItemTblidx,int,1;dwHonorPoint,int,1;wpsTblidx,int,1;openCine,int,1;groupIdx,int,1";
        // WorldPlayTable.h, pack(4): serialized row16 excludes the vptr.
        yield return "WorldPlayTable;table_world_play_data.rdf;Table_Data_KOR;" +
            "tblidx,int,1;dwGroup,int,1;byExecuterType,byte,1;byShareType,byte,1;pad_10,byte,2;dwShareLimitTime,int,1";
        // WorldMapTable.h pack(4), native serialized row140 on x86/x64.
        // scripts/tests/world-map-abi.cpp checks every field offset/width.
        yield return "WorldMapTable;table_worldmap_data.rdf;Table_Data_KOR;" +
            "tblidx,int,1;World_Tblidx,int,1;Zone_Tblidx,int,1;Worldmap_Name,int,1;" +
            "wszNameText,wstring,33;bValidityAble,bool,1;byMapType,byte,1;vStandardLoc,float,3;" +
            "fWorldmapScale,float,1;dwLinkMapIdx,int,1;dwComboBoxType,int,1;wWarfog,short,8;" +
            "byRecomm_Min_Level,byte,1;byRecomm_Max_Level,byte,1;pad_126,byte,2;vUiModify,float,3";
        var fields = new List<string> { "tblidx,int,1", "bValidityAble,bool,1", "pad_5,byte,3", "dwName,int,1",
            "byRecipeType,byte,1", "byNeedMixLevel,byte,1", "pad_14,byte,2", "dwNeedMixZenny,int,1" };
        for (int i = 0; i < 3; ++i)
            fields.AddRange([$"asCreateItemTblidx[{i}].itemTblidx,int,1", $"asCreateItemTblidx[{i}].itemRate,byte,1",
                $"asCreateItemTblidx[{i}].byUnknown,byte,1", $"pad_create_{i},byte,2"]);
        for (int i = 0; i < 5; ++i)
            fields.AddRange([$"asMaterial[{i}].materialTblidx,int,1", $"asMaterial[{i}].byMaterialCount,byte,1", $"pad_material_{i},byte,3"]);
        yield return "ItemRecipe;table_item_recipe_data.rdf;Table_Data_KOR;" + string.Join(';', fields);
        // QuestRewardTable.h / NtlQuest.h: four default and four selected
        // sQUEST_REWARD_DATASET records, pack(4), GetDataSize excludes vptr.
        var rewards = new List<string> { "tblidx,int,1", "dwDef_Reward_EXP,int,1", "dwDef_Reward_Zeny,int,1" };
        foreach (string array in new[] { "arsDefRwd", "arsSelRwd" })
            for (int i = 0; i < 4; ++i)
                rewards.AddRange([$"{array}[{i}].byRewardType,byte,1", $"pad_{array}_{i},byte,3",
                    $"{array}[{i}].dwRewardIdx,int,1", $"{array}[{i}].dwRewardVal,int,1"]);
        yield return "QuestReward;table_quest_reward_data.rdf;Table_Data_KOR;" + string.Join(';', rewards);
        // ItemOptionTable.h, pack(4), four native effects. Serialized row260 excludes vptr.
        // Padding stays noneditable and byte-exact when cloning the template.
        // CharmTable.h / ItemOptionTable.h, pack(4), serialized without vptr.
        // Explicit padding stays immutable under RdfCli.Apply.
        yield return "Charm;table_charm_data.rdf;Table_Data_KOR;tblidx,int,1;wDrop_Rate,short,1;wEXP,short,1;wRP_Sharing,short,1;wCool_Time,short,1;wKeep_Time,short,1;pad_14,byte,2;dwKeep_Time_In_Millisecs,int,1;dwNeed_Zenny,int,1;byDice_Min,byte,1;byDice_Max,byte,1;byCharm_Type_Bit_Flag,byte,1;pad_27,byte,1";
        var options = new List<string> { "tblidx,int,1", "wszOption_Name,wstring,31", "bValidity_Able,bool,1",
            "byOption_Rank,byte,1", "byItem_Group,byte,1", "byMaxQuality,byte,1", "byQuality,byte,1",
            "byQualityIndex,byte,1", "dwCost,int,1", "byLevel,byte,1", "pad_77,byte,3" };
        for (int i = 0; i < 4; ++i) options.Add($"system_Effect[{i}],int,1");
        for (int i = 0; i < 4; ++i) options.Add($"bAppliedInPercent[{i}],bool,1");
        for (int i = 0; i < 4; ++i) options.Add($"nValue[{i}],int,1");
        options.AddRange(["activeEffect,int,1", "fActiveRate,float,1", "szNote,string,129"]);
        for (int i = 0; i < 4; ++i) options.Add($"byScouterInfo[{i}],byte,1");
        options.Add("pad_257,byte,3");
        yield return "ItemOption;table_item_option_data.rdf;Table_Data_KOR;" + string.Join(';', options);
        // Native NPCTable.h/PCTable.h/Table.h pack(4) payload; the binary
        // omits sTBLDAT's vptr. Keep the full 500-byte layout and reserved spans.
        yield return "MasterNPC;Table_NPC_Data.rdf;Table_Data_KOR;tblidx,int,1;dwBasic_LP,int,1;wBasic_EP,short,1;wBasic_Physical_Defence,short,1;wBasic_Energy_Defence,short,1;wBasicStr,short,1;wBasicCon,short,1;wBasicFoc,short,1;wBasicDex,short,1;wBasicSol,short,1;wBasicEng,short,1;pad_26,byte,2;fScale,float,1;wAttack_Speed_Rate,short,1;byAttack_Type,byte,1;pad_35,byte,1;fAttack_Range,float,1;wAttack_Rate,short,1;wDodge_Rate,short,1;wBlock_Rate,short,1;wCurse_Success_Rate,short,1;wCurse_Tolerance_Rate,short,1;pad_50,byte,2;fRadius,float,1;wBasic_Aggro_Point,short,1;pad_58,byte,2;bValidity_Able,bool,1;pad_61,byte,3;Name,int,1;wszNameText,wstring,41;szModel,string,33;byLevel,byte,1;byGrade,byte,1;pad_185,byte,3;dwAi_Bit_Flag,int,1;wLP_Regeneration,short,1;wEP_Regeneration,short,1;byAttack_Animation_Quantity,byte,1;byBattle_Attribute,byte,1;wBasic_Physical_Offence,short,1;wBasic_Energy_Offence,short,1;pad_202,byte,2;fWalk_Speed_Origin,float,1;fWalk_Speed,float,1;fRun_Speed_Origin,float,1;fRun_Speed,float,1;fRadius_X,float,1;fRadius_Z,float,1;wSight_Range,short,1;wScan_Range,short,1;wUse_Skill_Time,short,7;pad_246,byte,2;use_Skill_Tblidx,int,7;byUse_Skill_Basis,byte,7;pad_283,byte,1;wUse_Skill_LP,short,7;byVisible_Sight_Range,byte,1;szCamera_Bone_Name,string,33;wAttackCoolTime,short,1;pad_334,byte,2;fFly_Height,float,1;szNameText,string,41;bSpawn_Animation,bool,1;pad_382,byte,2;dwDialogGroup,int,1;szILLust,string,33;pad_421,byte,3;dwAllianceIdx,int,1;wAggroMaxCount,short,1;pad_430,byte,2;dwNpcAttributeFlag,int,1;wStomachacheDefence,short,1;wPoisonDefence,short,1;wBleedDefence,short,1;wBurnDefence,short,1;byNpcType,byte,1;byJob,byte,1;pad_446,byte,2;dwFunc_Bit_Flag,int,1;Dialog_Script_Index,int,1;amerchant_Tblidx,int,6;statusTransformTblidx,int,1;contentsTblidx,int,1;wUnknown,short,1;pad_490,byte,2;dwUnknown2,int,1;dwUnknown3,int,1";
    }
}
