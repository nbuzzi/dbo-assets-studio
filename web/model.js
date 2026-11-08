import * as THREE from './vendor/three/three.module.js';
import {OrbitControls} from './vendor/three/OrbitControls.js';
export async function showModel(container,data){
 const bytes=await(await fetch(data.url)).arrayBuffer(),v=new DataView(bytes);let p=4;const u32=()=>{const n=v.getUint32(p,true);p+=4;return n},f32=()=>{const n=v.getFloat32(p,true);p+=4;return n};
 const count=u32(),group=new THREE.Group(),loaded={};
 try{
 for(const [name,url] of Object.entries(data.textures)){const texture=await new THREE.TextureLoader().loadAsync(url);texture.colorSpace=THREE.SRGBColorSpace;texture.wrapS=texture.wrapT=THREE.RepeatWrapping;loaded[name.toLowerCase()]=texture}
 for(let mesh=0;mesh<count;mesh++){
  const nv=u32(),nt=u32(),nm=u32(),positions=new Float32Array(nv*3),uv=new Float32Array(nv*2),batches=Array.from({length:nm},()=>[]);
  for(let i=0;i<nv;i++){positions[i*3]=f32();positions[i*3+1]=f32();positions[i*3+2]=f32();uv[i*2]=f32();uv[i*2+1]=1-f32()}
  for(let t=0;t<nt;t++){const a=u32(),b=u32(),c=u32(),mat=u32();batches[mat].push(a,b,c)}
  for(let m=0;m<nm;m++){const rgba=u32(),nameBytes=new Uint8Array(bytes,p,128);p+=128;const end=nameBytes.indexOf(0),name=new TextDecoder('windows-1252').decode(nameBytes.slice(0,end));if(!batches[m].length)continue;const geo=new THREE.BufferGeometry();geo.setAttribute('position',new THREE.BufferAttribute(positions,3));geo.setAttribute('uv',new THREE.BufferAttribute(uv,2));geo.setIndex(batches[m]);geo.computeVertexNormals();const texture=loaded[name.toLowerCase()];const mat=new THREE.MeshStandardMaterial({map:texture||null,color:texture?0xffffff:new THREE.Color((rgba&255)/255,((rgba>>>8)&255)/255,((rgba>>>16)&255)/255),roughness:1,metalness:0,side:THREE.DoubleSide,alphaTest:.05});group.add(new THREE.Mesh(geo,mat))}
 }
 const scene=new THREE.Scene();scene.add(group);scene.add(new THREE.HemisphereLight(0xdceaff,0x655c50,2.5));const light=new THREE.DirectionalLight(0xffeed8,2);light.position.set(3,5,4);scene.add(light);
 const bounds=new THREE.Box3().setFromObject(group),center=bounds.getCenter(new THREE.Vector3()),size=bounds.getSize(new THREE.Vector3()),radius=Math.max(.01,size.length()/2);
 const grid=new THREE.GridHelper(radius*5,20,0x55503f,0x2b3542);grid.position.set(center.x,bounds.min.y-radius*.01,center.z);scene.add(grid);
 const camera=new THREE.PerspectiveCamera(42,1,radius*.005,radius*100);const renderer=new THREE.WebGLRenderer({alpha:true,antialias:true});renderer.setPixelRatio(Math.min(devicePixelRatio,1.7));renderer.setClearColor(0,0);container.append(renderer.domElement);
 const controls=new OrbitControls(camera,renderer.domElement);controls.target.copy(center);controls.enableDamping=true;controls.minDistance=radius*.3;controls.maxDistance=radius*30;
 const fit=()=>{camera.position.copy(center).add(new THREE.Vector3(.9,.35,2.9).multiplyScalar(radius));controls.target.copy(center);controls.update()};fit();window.addEventListener('assets-fit-model',fit);
 const resize=()=>{const w=container.clientWidth,h=container.clientHeight;if(!w||!h)return;renderer.setSize(w,h);camera.aspect=w/h;camera.updateProjectionMatrix()};const observer=new ResizeObserver(resize);observer.observe(container);resize();renderer.setAnimationLoop(()=>{controls.update();renderer.render(scene,camera)});
 return ()=>{renderer.setAnimationLoop(null);observer.disconnect();controls.dispose();window.removeEventListener('assets-fit-model',fit);group.traverse(o=>{o.geometry?.dispose();o.material?.dispose()});Object.values(loaded).forEach(t=>t.dispose());grid.geometry.dispose();grid.material.dispose();renderer.dispose();renderer.domElement.remove()};
 }catch(e){group.traverse(o=>{o.geometry?.dispose();o.material?.dispose()});Object.values(loaded).forEach(t=>t.dispose());throw e}
}
