import * as THREE from './vendor/three/three.module.js';
import {OrbitControls} from './vendor/three/OrbitControls.js';
import {GLTFLoader} from './vendor/three/GLTFLoader.js';
export async function showMob(container,url,options={}){
 const gltf=await new GLTFLoader().loadAsync(url),model=gltf.scene;
 // Match the server-side GLB inspection cap. The input is rejected before it
 // is sent to this viewer; this second check protects against a stale/mismatched
 // inspection response and avoids adding oversized geometry to the WebGL scene.
 let vertices=0;const geometries=new Set();model.traverse(o=>{if(o.isMesh&&o.geometry&&!geometries.has(o.geometry)){geometries.add(o.geometry);vertices+=o.geometry.attributes.position?.count||0}});
 if(vertices<1||vertices>2_000_000){for(const g of geometries)g.dispose();throw new Error('La vista previa admite de 1 a 2.000.000 v茅rtices.')}
 const scene=new THREE.Scene();scene.add(model);
 const clips=gltf.animations||[],mixer=clips.length?new THREE.AnimationMixer(model):null;let action=null,playing=!!options.autoplay,clipIndex=0;
 const clock=new THREE.Clock();const bar=document.createElement('div');bar.className='mob-animation-bar';
 if(clips.length){
  const select=document.createElement('select');select.setAttribute('aria-label','Clip de animación');clips.forEach((clip,i)=>{const o=document.createElement('option');o.value=i;o.textContent=clip.name||`Clip ${i+1}`;select.append(o)});
  const play=document.createElement('button');play.className='button';play.textContent=playing?'Pausar':'Reproducir';
  const seek=document.createElement('input');seek.type='range';seek.min='0';seek.step='.01';seek.setAttribute('aria-label','Tiempo de animación');
  const speed=document.createElement('select');speed.setAttribute('aria-label','Velocidad');for(const value of [.25,.5,1,1.5,2]){const o=document.createElement('option');o.value=value;o.textContent=value+'×';o.selected=value===1;speed.append(o)}
  const time=document.createElement('span');
  const load=()=>{mixer.stopAllAction();action=mixer.clipAction(clips[clipIndex]);action.reset().play();seek.max=String(Math.max(.01,clips[clipIndex].duration));mixer.update(0)};
  select.onchange=()=>{clipIndex=Number(select.value);load()};play.onclick=()=>{playing=!playing;play.textContent=playing?'Pausar':'Reproducir'};seek.oninput=()=>{action.time=Number(seek.value);mixer.update(0)};speed.onchange=()=>mixer.timeScale=Number(speed.value);load();
  bar.append(select,play,seek,speed,time);bar.update=()=>{seek.value=action.time;time.textContent=`${action.time.toFixed(1)} / ${clips[clipIndex].duration.toFixed(1)} s`};
  options.onClips?.(clips.map(c=>({name:c.name,duration:c.duration})));
 }else{bar.textContent='Modelo estático · sin clips de animación'}
 container.append(bar);
 scene.add(new THREE.HemisphereLight(0xdceaff,0x584233,2.8));const light=new THREE.DirectionalLight(0xffe4b7,3);light.position.set(3,5,4);scene.add(light);
 const bounds=new THREE.Box3().setFromObject(model),center=bounds.getCenter(new THREE.Vector3()),radius=Math.max(.01,bounds.getSize(new THREE.Vector3()).length()/2);
 const grid=new THREE.GridHelper(radius*5,24,0x716043,0x272d35);grid.position.set(center.x,bounds.min.y-radius*.01,center.z);scene.add(grid);
 const renderer=new THREE.WebGLRenderer({alpha:true,antialias:false,powerPreference:'low-power'});renderer.setPixelRatio(Math.min(devicePixelRatio,1.25));renderer.setClearColor(0,0);container.append(renderer.domElement);
 const camera=new THREE.PerspectiveCamera(40,1,radius*.005,radius*100);camera.position.copy(center).add(new THREE.Vector3(.85,.25,2.9).multiplyScalar(radius));const controls=new OrbitControls(camera,renderer.domElement);controls.target.copy(center);controls.enableDamping=true;const fit=()=>{camera.position.copy(center).add(new THREE.Vector3(.85,.25,2.9).multiplyScalar(radius));controls.target.copy(center);controls.update()};window.addEventListener('assets-fit-model',fit);
 const resize=()=>{const w=container.clientWidth,h=container.clientHeight;if(!w||!h)return;renderer.setSize(w,h);camera.aspect=w/h;camera.updateProjectionMatrix()};const observer=new ResizeObserver(resize);observer.observe(container);resize();renderer.setAnimationLoop(()=>{const delta=Math.min(clock.getDelta(),.1);if(mixer&&playing)mixer.update(delta);bar.update?.();controls.update();renderer.render(scene,camera)});
 return ()=>{bar.remove();mixer?.stopAllAction();mixer?.uncacheRoot(model);renderer.setAnimationLoop(null);window.removeEventListener('assets-fit-model',fit);observer.disconnect();controls.dispose();model.traverse(o=>{o.geometry?.dispose();for(const m of (o.material?Array.isArray(o.material)?o.material:[o.material]:[])){for(const v of Object.values(m))if(v?.isTexture)v.dispose();m.dispose()}});grid.geometry.dispose();grid.material.dispose();renderer.dispose();renderer.domElement.remove()};
}
