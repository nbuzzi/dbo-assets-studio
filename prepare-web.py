"""Reproducible, build-time web dependency download. Runtime needs no network."""
import argparse, hashlib, io, json, tarfile, urllib.request, zipfile
from pathlib import Path
parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);args=parser.parse_args()
source=Path(__file__).resolve().parent
lock=json.loads((source/'web-dependencies.json').read_text(encoding='utf-8'))
cache=source/'.cache'/'web-downloads';cache.mkdir(parents=True,exist_ok=True)
out=Path(args.output).resolve()/'web/vendor';out.mkdir(parents=True,exist_ok=True)
for name,meta in lock.items():
 archive=cache/(name+'.archive')
 if not archive.exists() or hashlib.sha256(archive.read_bytes()).hexdigest()!=meta['sha256']:
  data=urllib.request.urlopen(meta['url'],timeout=90).read()
  if hashlib.sha256(data).hexdigest()!=meta['sha256']:raise RuntimeError('SHA256 mismatch: '+name)
  archive.write_bytes(data)
 data=archive.read_bytes();dest=out/name;dest.mkdir(exist_ok=True)
 if name=='ruffle':
  with zipfile.ZipFile(io.BytesIO(data)) as z:
   for entry in z.infolist():
    leaf=Path(entry.filename).name
    if entry.is_dir() or not (leaf.endswith(('.js','.wasm')) or leaf.startswith('LICENSE')):continue
    (dest/leaf).write_bytes(z.read(entry))
 else:
  with tarfile.open(fileobj=io.BytesIO(data),mode='r:gz') as t:
   for member in ['package/build/three.module.js','package/build/three.core.js','package/examples/jsm/controls/OrbitControls.js','package/examples/jsm/loaders/GLTFLoader.js','package/examples/jsm/utils/BufferGeometryUtils.js','package/LICENSE']:
    content=t.extractfile(member).read()
    if member.endswith('.js'):content=content.replace(b"from 'three'",b"from './three.module.js'").replace(b"from '../utils/BufferGeometryUtils.js'",b"from './BufferGeometryUtils.js'")
    (dest/Path(member).name).write_bytes(content)
(out/'dependencies.json').write_text(json.dumps(lock,indent=2)+'\n',encoding='utf-8')
print('Offline Ruffle and Three.js dependencies prepared: '+str(out))
