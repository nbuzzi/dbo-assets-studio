#include <windows.h>
#include <cstdio>
#include <cstddef>
#include "MobTable.h"
#include "NPCTable.h"
#include "ObjectTable.h"
#include "SkillTable.h"
#include "ItemTable.h"
static unsigned off(size_t n){return (unsigned)(n-sizeof(void*));}
int main(int argc,char**argv){
 if(argc!=2)return 2;FILE*f=fopen(argv[1],"wb");if(!f)return 3;
 fprintf(f,"[\n");
 fprintf(f," {\"table\":\"Table_Mob_Data.rdf\",\"kind\":\"Mob\",\"rowSize\":%u,\"idOffset\":%u,\"modelOffset\":%u,\"modelLength\":33,\"nameOffset\":%u,\"nameLength\":41,\"nameWide\":true,\"nameIdOffset\":%u,\"validityOffset\":%u},\n",(unsigned)(sizeof(sMOB_TBLDAT)-sizeof(void*)),off(offsetof(sMOB_TBLDAT,tblidx)),off(offsetof(sMOB_TBLDAT,szModel)),off(offsetof(sMOB_TBLDAT,wszNameText)),off(offsetof(sMOB_TBLDAT,Name)),off(offsetof(sMOB_TBLDAT,bValidity_Able)));
 fprintf(f," {\"table\":\"Table_NPC_Data.rdf\",\"kind\":\"Npc\",\"rowSize\":%u,\"idOffset\":%u,\"modelOffset\":%u,\"modelLength\":33,\"nameOffset\":%u,\"nameLength\":41,\"nameWide\":true},\n",(unsigned)(sizeof(sNPC_TBLDAT)-sizeof(void*)),off(offsetof(sNPC_TBLDAT,tblidx)),off(offsetof(sNPC_TBLDAT,szModel)),off(offsetof(sNPC_TBLDAT,wszNameText)));
 fprintf(f," {\"table\":\"Table_Object_Data.rdf\",\"kind\":\"Object\",\"rowSize\":%u,\"idOffset\":%u,\"modelOffset\":%u,\"modelLength\":33,\"nameOffset\":0,\"nameLength\":0,\"nameWide\":false},\n",(unsigned)(sizeof(sOBJECT_TBLDAT)-sizeof(void*)),off(offsetof(sOBJECT_TBLDAT,tblidx)),off(offsetof(sOBJECT_TBLDAT,szModelName)));
 fprintf(f," {\"table\":\"Table_Skill_Data.rdf\",\"kind\":\"Skill\",\"rowSize\":%u,\"idOffset\":%u,\"modelOffset\":0,\"modelLength\":0,\"nameOffset\":0,\"nameLength\":0,\"nameWide\":false,\"validityOffset\":%u}\n",(unsigned)(sizeof(sSKILL_TBLDAT)-sizeof(void*)),off(offsetof(sSKILL_TBLDAT,tblidx)),off(offsetof(sSKILL_TBLDAT,bValidity_Able)));
 // Item uses the same native ABI serializer as the runtime reader.
 fprintf(f,", {\"table\":\"Table_Item_Data.rdf\",\"kind\":\"Item\",\"rowSize\":%u,\"idOffset\":%u,\"modelOffset\":0,\"modelLength\":0,\"nameOffset\":0,\"nameLength\":0,\"nameWide\":false,\"validityOffset\":%u}\n",(unsigned)(sizeof(sITEM_TBLDAT)-sizeof(void*)),off(offsetof(sITEM_TBLDAT,tblidx)),off(offsetof(sITEM_TBLDAT,bValidity_Able)));
 fprintf(f,"]\n");fclose(f);return 0;
}
