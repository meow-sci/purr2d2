"""Import the portable-authored hilt into an isolated Blender scene.

First run python3 tools/create_hilt_portable.py, then:
/Applications/Blender.app/Contents/MacOS/Blender --background --factory-startup --python tools/create_hilt.py

The portable generator is the exact editable geometry/texture source. This
optional script creates a .blend for further manual modeling and a Cycles review
render when Blender is available. No pre-existing objects are deleted or edited.
"""
from pathlib import Path
import json
import math
import bpy
from mathutils import Vector, Matrix

ROOT=Path(__file__).resolve().parents[1]
ART=ROOT/'art'
source=ROOT/'Assets/Models/Purr2D2_Hilt.glb'
if not source.exists():raise RuntimeError('Run tools/create_hilt_portable.py first')
old_scene=bpy.context.window.scene
scene=bpy.data.scenes.new('Purr2D2 Hilt Authoring')
bpy.context.window.scene=scene
try:
    scene.unit_settings.system='METRIC';scene.unit_settings.scale_length=1
    bpy.ops.import_scene.gltf(filepath=str(source))
    hilt=next(o for o in scene.objects if o.type=='MESH')
    # Importer bakes (X,Y,Z)->(X,-Z,Y) into vertices. Undo it in mesh
    # coordinates, not just the object rotation, to retain native +Z space.
    bpy.context.view_layer.objects.active=hilt;hilt.select_set(True)
    bpy.ops.object.transform_apply(location=True,rotation=True,scale=True)
    hilt.data.transform(Matrix.Rotation(-math.pi/2,4,'X'))
    hilt.name='Purr2D2_Hilt';hilt.data.name='Purr2D2_Hilt'
    for slot in hilt.material_slots:
        if slot.material and slot.material.use_nodes:
            for node in slot.material.node_tree.nodes:
                if node.type=='TEX_IMAGE' and node.image and not node.image.packed_file:node.image.pack()
    def aim(obj):obj.rotation_euler=(Vector((0,0,-.107))-obj.location).to_track_quat('-Z','Y').to_euler()
    for name,location,energy,size,color in [
        ('Key',(.20,-.22,.12),35,.18,(.72,.86,1)),
        ('Warm rim',(-.16,.10,.02),45,.13,(1,.57,.25)),
        ('Fill',(.02,.22,-.13),25,.12,(.32,.72,1)),
    ]:
        data=bpy.data.lights.new(name,'AREA');data.energy=energy;data.size=size;data.color=color
        light=bpy.data.objects.new(name,data);scene.collection.objects.link(light);light.location=location;aim(light)
    data=bpy.data.cameras.new('Hilt review camera');data.type='ORTHO';data.ortho_scale=.30
    camera=bpy.data.objects.new('Hilt review camera',data);scene.collection.objects.link(camera)
    camera.location=(.31,-.39,.11);aim(camera);scene.camera=camera
    scene.world=bpy.data.worlds.new('Purr2D2 Studio');scene.world.color=(.045,.045,.045)
    scene.render.engine='CYCLES';scene.cycles.samples=48
    scene.render.resolution_x=1000;scene.render.resolution_y=1200;scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG';scene.render.filepath=str(ART/'purr2d2-hilt-blender-preview.png')
    scene['asset_contract']='Meters; +Z blade axis; emitter z=0; grip z=-.12; baked geometry; ORM=AO/Roughness/Metallic'
    # Write only this scene's dependency closure, never other existing scenes.
    bpy.data.libraries.write(str(ART/'purr2d2-hilt.blend'),{scene},fake_user=True)
    bpy.ops.render.render(write_still=True)
    metadata_path=ART/'purr2d2-hilt-metadata.json'
    metadata=json.loads(metadata_path.read_text());metadata['blend_saved']=True
    metadata['blender_version']=bpy.app.version_string
    metadata_path.write_text(json.dumps(metadata,indent=2)+'\n')
    print('WROTE',ART/'purr2d2-hilt.blend')
finally:
    bpy.context.window.scene=old_scene
