// Isolated visual preview of the exact client backdrop header and an authored
// Arena BSP. No packs, installed client, server or acceptance state are written.
#include "precomp_ntlpresentation.h"
#include "NtlAtomic.h"
#include "NtlMaterialExt.h"
#include "NtlArenaSpaceBackdrop.h"
#include <cstdio>
#include <cstdlib>
#include <string>

static void Require(bool ok, const char* operation)
{ if (!ok) { fprintf(stderr, "Preview failed: %s\n", operation); exit(1); } }
static RwTexture* ReadTexture(const RwChar* name, const RwChar*)
{
    if (!name || (strcmp(name,"orig_power_white") && strcmp(name,"orig_power_stone_v2") &&
        strcmp(name,"orig_power_metal_v2"))) return NULL;
    const std::string file = std::string(name)+".bmp";
    RwImage* image = RtBMPImageRead(file.c_str());
    if (!image) return NULL;
    RwInt32 width,height,depth,format;
    RwImageFindRasterFormat(image,rwRASTERTYPETEXTURE,&width,&height,&depth,&format);
    RwRaster* raster = RwRasterCreate(width,height,depth,format);
    if (!raster || !RwRasterSetFromImage(raster,image))
    { if (raster) RwRasterDestroy(raster); RwImageDestroy(image); return NULL; }
    RwImageDestroy(image);
    RwTexture* texture = RwTextureCreate(raster);
    if (!texture) { RwRasterDestroy(raster); return NULL; }
    RwTextureSetName(texture,name); RwTextureSetFilterMode(texture,rwFILTERLINEAR);
    RwTextureSetAddressing(texture,rwTEXTUREADDRESSWRAP); return texture;
}
static void Capture(RpWorld* world,NtlArenaSpaceBackdrop& backdrop,const char* path,RwV3d eye,RwV3d target)
{
    Require(GetFileAttributesA(path)==INVALID_FILE_ATTRIBUTES,"fresh output path");
    const int width=1280,height=720;
    RwCamera* camera=RwCameraCreate(); RwFrame* frame=RwFrameCreate();
    Require(camera && frame,"camera/frame"); RwCameraSetFrame(camera,frame);
    RwRaster* raster=RwRasterCreate(width,height,32,rwRASTERTYPECAMERATEXTURE|rwRASTERFORMAT8888);
    RwRaster* depth=RwRasterCreate(width,height,0,rwRASTERTYPEZBUFFER);
    Require(raster && depth,"framebuffer"); RwCameraSetRaster(camera,raster); RwCameraSetZRaster(camera,depth);
    RwV2d view={.8f,.45f}; RwCameraSetViewWindow(camera,&view);
    RwCameraSetNearClipPlane(camera,.1f); RwCameraSetFarClipPlane(camera,512);
    RwV3d at,right,up,vertical={0,1,0}; RwV3dSub(&at,&target,&eye); RwV3dNormalize(&at,&at);
    RwV3dCrossProduct(&right,&vertical,&at); RwV3dNormalize(&right,&right); RwV3dCrossProduct(&up,&at,&right);
    RwMatrix* matrix=RwFrameGetMatrix(frame); RwMatrixSetIdentity(matrix);
    *RwMatrixGetRight(matrix)=right; *RwMatrixGetUp(matrix)=up; *RwMatrixGetAt(matrix)=at; *RwMatrixGetPos(matrix)=eye;
    RwMatrixUpdate(matrix); RwFrameUpdateObjects(frame); RpWorldAddCamera(world,camera);
    RwRGBA clear={25,4,16,255}; Require(RwCameraClear(camera,&clear,rwCAMERACLEARIMAGE|rwCAMERACLEARZ)!=NULL,"clear");
    Require(RwCameraBeginUpdate(camera)!=NULL,"begin");
    RwRenderStateSet(rwRENDERSTATEFOGENABLE,(void*)FALSE);
    backdrop.Render(camera,25);
    RwRenderStateSet(rwRENDERSTATEZWRITEENABLE,(void*)TRUE); RwRenderStateSet(rwRENDERSTATEZTESTENABLE,(void*)TRUE);
    RwRenderStateSet(rwRENDERSTATECULLMODE,(void*)rwCULLMODECULLBACK);
    RpWorldRender(world); RwCameraEndUpdate(camera);
    RwImage* image=RwImageCreate(width,height,32);
    Require(image && RwImageAllocatePixels(image) && RwImageSetFromRaster(image,raster),"read framebuffer");
    FILE* output=fopen(path,"wbx"); Require(output!=NULL,"create image"); fprintf(output,"P6\n%d %d\n255\n",width,height);
    for (int y=0;y<height;++y) for (int x=0;x<width;++x)
        Require(fwrite(RwImageGetPixels(image)+y*RwImageGetStride(image)+x*4,3,1,output)==1,"write pixel");
    Require(fclose(output)==0,"close image"); RwImageDestroy(image);
    RpWorldRemoveCamera(world,camera); RwCameraSetRaster(camera,NULL); RwCameraSetZRaster(camera,NULL);
    RwCameraSetFrame(camera,NULL); RwCameraDestroy(camera); RwRasterDestroy(raster); RwRasterDestroy(depth); RwFrameDestroy(frame);
    printf("Rendered %s (isolated preview; not gameplay)\n",path);
}
int main(int argc,char** argv)
{
    if (argc!=3) { fprintf(stderr,"arena-space-preview <authored-map-directory> <fresh-output-directory>\n"); return 2; }
    const std::string output=argv[2];
    Require(GetFileAttributesA(output.c_str())==INVALID_FILE_ATTRIBUTES && CreateDirectoryA(output.c_str(),NULL),"new output directory");
    Require(SetCurrentDirectoryA(argv[1])!=FALSE,"map directory");
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    HWND window=CreateWindowA("STATIC","DBO Assets Arena Space Preview",0,0,0,64,64,NULL,NULL,GetModuleHandle(NULL),NULL);
    Require(window && RwEngineInit(NULL,0,1024*1024),"engine init");
    Require(RpWorldPluginAttach() && RpPVSPluginAttach() && RpCollisionPluginAttach() && RtAnimInitialize() &&
        RpLtMapPluginAttach() && RpHAnimPluginAttach() && RpSkinPluginAttach() && RpToonPluginAttach() &&
        RpUserDataPluginAttach() && RpIdPluginAttach() && RpMatFXPluginAttach() && RpUVAnimPluginAttach() &&
        RpNtlAtomicPluginAttach() && RpNtlMatFxPluginAttach(),"Taiwan plugins");
    RwEngineOpenParams open={}; open.displayID=window; Require(RwEngineOpen(&open)!=FALSE,"device");
    int mode=-1; for (int i=0;i<RwEngineGetNumVideoModes();++i)
    { RwVideoMode info; if (RwEngineGetVideoModeInfo(&info,i) && !(info.flags&rwVIDEOMODEEXCLUSIVE)) { mode=i; break; } }
    Require(mode>=0 && RwEngineSetVideoMode(mode) && RwEngineStart(),"engine start");
    Require(RtFSManagerOpen(RTFSMAN_UNLIMITED_NUM_FS)!=FALSE,"file manager");
    char drive[]="D:",name[]="D"; RtFileSystem* filesystem=RtWinFSystemInit(100,drive,name);
    Require(filesystem && RtFSManagerRegister(filesystem),"workspace file system");
    RtFSManagerSetDefaultFileSystem(filesystem);
    RwTextureSetReadCallBack(ReadTexture);
    RwStream* stream=RwStreamOpen(rwSTREAMFILENAME,rwSTREAMREAD,"arena.bsp");
    Require(stream && RwStreamFindChunk(stream,rwID_WORLD,NULL,NULL),"authored BSP");
    RpWorld* world=RpWorldStreamRead(stream); RwStreamClose(stream,NULL); Require(world!=NULL,"load BSP");
    RpLight* ambient=RpLightCreate(rpLIGHTAMBIENT); RwRGBAReal color={1,1,1,1};
    Require(ambient && RpLightSetColor(ambient,&color),"ambient light"); RpWorldAddLight(world,ambient);
    {
        NtlArenaSpaceBackdrop backdrop;
        Capture(world,backdrop,(output+"/arena-east.ppm").c_str(),{30,4,20},{200,30,20});
        Capture(world,backdrop,(output+"/arena-west.ppm").c_str(),{-30,4,20},{-200,30,20});
        Capture(world,backdrop,(output+"/arena-north.ppm").c_str(),{20,4,-30},{20,30,-200});
        Capture(world,backdrop,(output+"/arena-south.ppm").c_str(),{20,4,30},{20,30,200});
    }
    RpWorldRemoveLight(world,ambient); RpLightDestroy(ambient); RpWorldDestroy(world);
    RtFSManagerClose(); RwEngineStop(); RwEngineClose(); RwEngineTerm(); DestroyWindow(window); return 0;
}
