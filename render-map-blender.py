"""Render the shared map candidate and save an editable Blender scene + GLB.
blender --background --python render-map-blender.py -- candidate-dir
Never accesses PAK/RDF or a runtime. Sources are unchanged.
"""
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

root = Path(sys.argv[sys.argv.index('--') + 1]).resolve()
data = json.loads((root / 'scene.json').read_text(encoding='utf-8'))
if data['profile'] != 'assets-map-preview-scene-v1':
    raise ValueError('Expected original map candidate scene')
for name in ('arena.blend', 'arena.glb', 'arena-perspective.png', 'arena-top.png', 'arena-walk.png'):
    if (root / name).exists():
        raise ValueError('Preserve existing output: ' + name)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
for item in data['meshes']:
    p, idx = item['positions'], item['indices']
    vertices = [(p[i], -p[i + 2], p[i + 1]) for i in range(0, len(p), 3)]
    faces = [idx[i:i + 3] for i in range(0, len(idx), 3)]
    mesh = bpy.data.meshes.new(item['name'])
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    uv = mesh.uv_layers.new(name='Native UV')
    for loop in mesh.loops:
        uv.data[loop.index].uv = item['uv'][loop.vertex_index * 2:loop.vertex_index * 2 + 2]
    obj = bpy.data.objects.new(item['name'], mesh)
    bpy.context.collection.objects.link(obj)
    mat = bpy.data.materials.new(item['name'])
    mat.diffuse_color = tuple(v / 255 for v in item['rgba'])
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = mat.diffuse_color
    shader.inputs['Roughness'].default_value = .84
    if item.get('texture', 'orig_power_white') != 'orig_power_white':
        texture = mat.node_tree.nodes.new('ShaderNodeTexImage')
        texture.image = bpy.data.images.load(str(root / (item['texture'] + '.bmp')), check_existing=True)
        texture.extension = 'REPEAT'
        mix = mat.node_tree.nodes.new('ShaderNodeMixRGB')
        mix.blend_type = 'MULTIPLY'
        mix.inputs[0].default_value = 1
        mix.inputs[1].default_value = mat.diffuse_color
        mat.node_tree.links.new(texture.outputs['Color'], mix.inputs[2])
        mat.node_tree.links.new(mix.outputs[0], shader.inputs['Base Color'])
    if item['name'] == 'lamp_5':
        shader.inputs['Emission Color'].default_value = mat.diffuse_color
        shader.inputs['Emission Strength'].default_value = 2
    obj.data.materials.append(mat)

scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.samples = 24
scene.cycles.use_denoising = True
scene.render.resolution_x = 1440
scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.world.use_nodes = True
nodes = scene.world.node_tree.nodes
links = scene.world.node_tree.links
noise = nodes.new('ShaderNodeTexNoise')
noise.inputs['Scale'].default_value = 3
noise.inputs['Detail'].default_value = 5
ramp = nodes.new('ShaderNodeValToRGB')
ramp.color_ramp.elements[0].position = .2
ramp.color_ramp.elements[0].color = (.008, .012, .026, 1)
ramp.color_ramp.elements[1].position = .8
ramp.color_ramp.elements[1].color = (.22, .13, .29, 1)
if data['recipe'].get('surfaceStyle') == 'apocalypse-stone-v1':
    ramp.color_ramp.elements[0].color = (.025, .004, .012, 1)
    ramp.color_ramp.elements[1].color = (.48, .055, .028, 1)
if data['recipe'].get('profile') == 'assets-namek-terrain-v1':
    # Namek: bright green sky with pale yellow clouds.
    ramp.color_ramp.elements[0].color = (.16, .62, .05, 1)
    ramp.color_ramp.elements[1].color = (.55, .75, .25, 1)
links.new(noise.outputs['Fac'], ramp.inputs['Fac'])
links.new(ramp.outputs['Color'], nodes.get('Background').inputs['Color'])
nodes.get('Background').inputs['Strength'].default_value = .65
for pos, energy, color in [((30, -80, 160), 4, (1, .88, .68)), ((-100, 60, 100), 2, (.7, .65, 1))]:
    light = bpy.data.lights.new('Arena light', 'SUN')
    light.energy = energy
    light.angle = math.radians(10)
    light.color = color
    obj = bpy.data.objects.new(light.name, light)
    bpy.context.collection.objects.link(obj)
    obj.location = pos
    obj.rotation_euler = (Vector((0, 0, 0)) - obj.location).to_track_quat('-Z', 'Y').to_euler()
camera = bpy.data.objects.new('Arena camera', bpy.data.cameras.new('Arena camera'))
bpy.context.collection.objects.link(camera)
scene.camera = camera
camera.location = (175, -210, 105)
camera.rotation_euler = (Vector((0, 0, 5)) - camera.location).to_track_quat('-Z', 'Y').to_euler()
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 285
scene.view_settings.view_transform = 'Standard' if data['recipe'].get('profile') == 'assets-namek-terrain-v1' else 'AgX'   # Namek: show the texture colours as painted
bpy.ops.wm.save_as_mainfile(filepath=str(root / 'arena.blend'))
bpy.ops.export_scene.gltf(filepath=str(root / 'arena.glb'), export_format='GLB', use_selection=False, export_cameras=False, export_lights=False)
scene.render.filepath = str(root / 'arena-perspective.png')
bpy.ops.render.render(write_still=True)
camera.location = (0, 0, 300)
camera.rotation_euler = (0, 0, 0)
camera.data.ortho_scale = 250
scene.render.filepath = str(root / 'arena-top.png')
bpy.ops.render.render(write_still=True)
if data['recipe'].get('surfaceStyle') == 'apocalypse-stone-v1':
    camera.data.type = 'PERSP'
    camera.data.lens = 28
    camera.location = (-44, -8, 1.8)
    camera.rotation_euler = (Vector((0, 0, 15)) - camera.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = str(root / 'arena-walk.png')
    bpy.ops.render.render(write_still=True)
if data['recipe'].get('profile') == 'assets-namek-terrain-v1':
    # Low shot across the sea, like the reference stills.
    camera.data.type = 'PERSP'
    camera.data.lens = 24
    camera.location = (-150, 60, 14)
    camera.rotation_euler = (Vector((60, -40, 6)) - camera.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = str(root / 'arena-walk.png')
    bpy.ops.render.render(write_still=True)
print('PASS Blender shared scene render + editable BLEND + GLB; not DBO world loading')
