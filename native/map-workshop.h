// Bounded original OSM1 -> native DFF and collision BSP. Uses the same native
// BSP proof as existing maps, without weakening the reviewed Snake Way profile.
static std::string gMapWorkshopTextureDirectory;
static bool MapWorkshopTextureName(const char* name){
    // PLANET-001 F2: Namek terrain surfaces use the orig_namek_ prefix; the reader below still requires the exact
    // 512x512 24-bit BMP size (MapWorkshopService terrain textures and scripts/prepare-planet-native-textures.py).
    return name&&(!strcmp(name,"orig_power_white")||!strcmp(name,"orig_power_stone_v2")||!strcmp(name,"orig_power_metal_v2")||
        (!strncmp(name,"orig_namek_",11)&&strlen(name)<64&&!strpbrk(name,"/\\.:")));
}
static int MapWorkshop(const char* input,const char* output){
    unsigned inputBytes=0;if(!fileSize(input,inputBytes)||inputBytes>8*1024*1024)return 51;
    std::ifstream in(input,std::ios::binary);char magic[4];unsigned count=0;
    in.read(magic,4);in.read((char*)&count,4);
    if(!in||memcmp(magic,"OSM1",4)||!count||count>32)return 51;
    HWND wnd=0;int initialized=openEngine(input,wnd);if(initialized)return initialized;
    std::string texturePath=input;auto slash=texturePath.find_last_of("/\\");texturePath=texturePath.substr(0,slash+1);
    gMapWorkshopTextureDirectory=texturePath;
    auto previous=RwTextureGetReadCallBack();
    RwTextureSetReadCallBack([](const RwChar* name,const RwChar* mask)->RwTexture*{
        if(!MapWorkshopTextureName(name)||(mask&&*mask))return NULL;
        const std::string path=gMapWorkshopTextureDirectory+name+".bmp";
        unsigned size=0;if(!fileSize(path.c_str(),size)||(strcmp(name,"orig_power_white")?size!=786486:size!=102))return NULL;
        SNtlPackImage image;image.strPackFileName=path;image.uiPackOffset=0;image.uiPackSize=size;image.pName=name;
        RwTexture* t=Ntl_BMPTextureRead(&image);if(t)RwTextureSetName(t,name);return t;
    });
    RwTexture* white=RwTextureRead("orig_power_white",NULL);
    if(!white){message("map workshop white texture missing");closeEngine(wnd);return 52;}
    RwTextureSetFilterMode(white,rwFILTERLINEAR);RwTextureSetAddressing(white,rwTEXTUREADDRESSWRAP);
    RpClump* clump=RpClumpCreate();RwFrame* root=RwFrameCreate();
    if(!clump||!root)return 53;RpClumpSetFrame(clump,root);bool ok=true;unsigned totalV=0,totalT=0;
    for(unsigned m=0;m<count&&ok;m++){
        unsigned nv=0,nt=0,nm=0;in.read((char*)&nv,4);in.read((char*)&nt,4);in.read((char*)&nm,4);
        if(!in||nv<3||nv>60000||nt<1||nt>20000||nm!=1||totalV+nv>60000||totalT+nt>20000){ok=false;break;}
        struct V{float x,y,z,u,v;};std::vector<V> v(nv);std::vector<std::array<unsigned,4>> faces(nt);RwRGBA color;char name[128];
        in.read((char*)v.data(),nv*sizeof(V));in.read((char*)faces.data(),nt*16);in.read((char*)&color,4);in.read(name,128);
        if(!in||!memchr(name,0,128)||!MapWorkshopTextureName(name)){ok=false;break;}
        RpGeometry* g=RpGeometryCreate(nv,nt,rpGEOMETRYPOSITIONS|rpGEOMETRYNORMALS|rpGEOMETRYTEXTURED|rpGEOMETRYPRELIT|rpGEOMETRYMODULATEMATERIALCOLOR);
        if(!g){ok=false;break;}RpGeometryLock(g,rpGEOMETRYLOCKALL);
        auto* mt=RpGeometryGetMorphTarget(g,0);auto* positions=RpMorphTargetGetVertices(mt);auto* normals=RpMorphTargetGetVertexNormals(mt);auto* uv=RpGeometryGetVertexTexCoords(g,rwTEXTURECOORDINATEINDEX0);auto* prelight=RpGeometryGetPreLightColors(g);
        for(unsigned i=0;i<nv;i++){
            positions[i]={v[i].x,v[i].y,v[i].z};normals[i]={0,0,0};uv[i]={v[i].u,v[i].v};prelight[i]={255,255,255,255};
            if(!BspFinite(positions[i])||fabs(v[i].x)>512||fabs(v[i].y)>512||fabs(v[i].z)>512||!std::isfinite(v[i].u)||!std::isfinite(v[i].v))ok=false;
        }
        RwTexture* texture=RwTextureRead(name,NULL);
        if(!texture){RpGeometryUnlock(g);RpGeometryDestroy(g);ok=false;break;}
        RwTextureSetFilterMode(texture,rwFILTERLINEAR);RwTextureSetAddressing(texture,rwTEXTUREADDRESSWRAP);
        RpMaterial* mat=RpMaterialCreate();RpMaterialSetTexture(mat,texture);RpMaterialSetColor(mat,&color);
        RwTextureDestroy(texture);
        RwSurfaceProperties surface={1,0,1};RpMaterialSetSurfaceProperties(mat,&surface);
        auto* triangles=RpGeometryGetTriangles(g);
        for(unsigned i=0;i<nt&&ok;i++){
            auto& f=faces[i];if(f[0]>=nv||f[1]>=nv||f[2]>=nv||f[3]){ok=false;break;}
            RpGeometryTriangleSetVertexIndices(g,&triangles[i],f[0],f[1],f[2]);RpGeometryTriangleSetMaterial(g,&triangles[i],mat);
            RwV3d a,b,n;RwV3dSub(&a,&positions[f[1]],&positions[f[0]]);RwV3dSub(&b,&positions[f[2]],&positions[f[0]]);RwV3dCrossProduct(&n,&a,&b);
            if(RwV3dLength(&n)<1e-6f){ok=false;break;}for(int k=0;k<3;k++)RwV3dAdd(&normals[f[k]],&normals[f[k]],&n);
        }
        for(unsigned i=0;i<nv&&ok;i++){if(RwV3dLength(&normals[i])<=0){ok=false;break;}RwV3dNormalize(&normals[i],&normals[i]);}
        RpMaterialDestroy(mat);RwSphere bounds;RpMorphTargetCalcBoundingSphere(mt,&bounds);RpMorphTargetSetBoundingSphere(mt,&bounds);RpGeometryUnlock(g);
        if(ok){RpAtomic* atomic=RpAtomicCreate();RwFrame* frame=RwFrameCreate();if(!atomic||!frame){ok=false;}else{RwFrameAddChild(root,frame);RpAtomicSetFrame(atomic,frame);RpAtomicSetGeometry(atomic,g,0);RpClumpAddAtomic(clump,atomic);}}
        RpGeometryDestroy(g);totalV+=nv;totalT+=nt;
    }
    ok=ok&&in.peek()==EOF;in.close();RwTextureDestroy(white);
    std::string dff=output;dff=dff.substr(0,dff.find_last_of("/\\")+1)+"arena.dff";
    if(ok){RwStream* stream=RwStreamOpen(rwSTREAMFILENAME,rwSTREAMWRITE,dff.c_str());ok=stream&&RpClumpStreamWrite(clump,stream);if(stream)RwStreamClose(stream,NULL);}
    RpClumpDestroy(clump);clump=NULL;
    if(ok){RwStream* stream=RwStreamOpen(rwSTREAMFILENAME,rwSTREAMREAD,dff.c_str());ok=stream&&RwStreamFindChunk(stream,rwID_CLUMP,NULL,NULL);if(ok)clump=RpClumpStreamRead(stream);if(stream)RwStreamClose(stream,NULL);ok=ok&&clump;}
    if(ok){RwFrameUpdateObjects(RpClumpGetFrame(clump));ok=BakeStaticBsp(clump,output);}
    if(clump)RpClumpDestroy(clump);RwTextureSetReadCallBack(previous);gMapWorkshopTextureDirectory.clear();closeEngine(wnd);
    if(!ok){message("map workshop bounded geometry/native readback failed");return 54;}
    printf("PASS map workshop native DFF/BSP meshes=%u vertices=%u triangles=%u oriented-faces/UV/prelight/materials preserved collision-tree=true; NOT world loading or gameplay\n",count,totalV,totalT);return 0;
}
