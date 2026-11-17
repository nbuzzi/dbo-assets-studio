// World Editor view: hosts the real-engine editor (world-engine.js) in its own section.
import {installWorldEngine} from './world-engine.js';

export function installWorldEditor({api,toast,escape}){
 const host=document.querySelector('#world-editor');
 if(!host)throw Error('Missing the #world-editor section.');
 host.innerHTML=`<div class="map-workshop-shell"><aside class="map-workshop-sidebar"><section id="map-engine-controls" class="map-native-controls map-engine-controls"></section></aside><main id="map-engine-main" class="map-workshop-main map-engine-main"></main></div>`;
 const engine=installWorldEngine({sidebar:host.querySelector('#map-engine-controls'),main:host.querySelector('#map-engine-main'),api,toast,escape});
 return {show:()=>engine.show(),hide:()=>engine.hide()};
}
