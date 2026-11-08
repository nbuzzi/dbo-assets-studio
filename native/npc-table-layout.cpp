// Native schema evidence only: report the serialized ABI used by CNPCTable.
// No table bytes are read or written by this program.
#include <windows.h>
#include <cstdio>
#include <cstddef>
#include "NpcTable.h"
#define REPORT(field) printf("%s %zu %zu\n", #field, offsetof(sNPC_TBLDAT, field)-sizeof(void*), sizeof(((sNPC_TBLDAT*)0)->field))
int main() {
    printf("abi %zu\nrow %zu\nbot %zu\n", sizeof(void*)*8,
        sizeof(sNPC_TBLDAT)-sizeof(void*), sizeof(sBOT_TBLDAT)-sizeof(void*));
    REPORT(tblidx); REPORT(bValidity_Able); REPORT(Name); REPORT(wszNameText);
    REPORT(szModel); REPORT(szNameText); REPORT(byNpcType); REPORT(byJob);
    REPORT(dwFunc_Bit_Flag); REPORT(Dialog_Script_Index); REPORT(amerchant_Tblidx);
    REPORT(statusTransformTblidx); REPORT(contentsTblidx); REPORT(wUnknown);
    REPORT(dwUnknown2); REPORT(dwUnknown3);
    return 0;
}
