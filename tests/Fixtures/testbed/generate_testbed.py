"""Writes the Testbed World Import Bundle: bundle.json, one bubble.json per bubble, one static glb.

Run headless; the same Blender version gives byte-identical files:
  blender -b --factory-startup --gpu-backend vulkan --python generate_testbed.py -- --out <dir>
"""

import argparse
import json
from pathlib import Path
import sys

import bmesh
import bpy

SCHEMA_VERSION = 0
BUNDLE_NAME = 'testbed'

# Plate: 24 m square of 2 m cells at Z = 0, centred on the origin.
PLATE_HALF = 12
CELL = 2
# Block: 2 x 2 x 3 m on the plate, off-centre in +X and +Y so a mirror or a swap shows.
BLOCK_MIN = (5.0, 3.0, 0.0)
BLOCK_MAX = (7.0, 5.0, 3.0)

BUBBLES = [
    {
        'schema_version': SCHEMA_VERSION,
        'name': 'bubble_a',
        'kind': 'outdoor',
        'bounds': {'min': [-16, -16, -8], 'max': [16, 16, 24]},
        'neighbours': ['bubble_b'],
        'statics': [{
            'name': 'terrain_and_block',
            'glb': 'static.glb',
            'position': [0, 0, 0],
            'rotation': [0, 0, 0, 1],
            'scale': 1,
        }],
        # The arrival spawn sits 0.1 m below the plate, so the plate clears the floor it lands on.
        'spawn_points': [{'name': 'arrival', 'position': [0, -8, -0.1], 'facing': 90}],
    },
    {
        'schema_version': SCHEMA_VERSION,
        'name': 'bubble_b',
        'kind': 'outdoor',
        'bounds': {'min': [16, -16, -8], 'max': [48, 16, 24]},
        'neighbours': ['bubble_a'],
        'statics': [],
        'spawn_points': [{'name': 'east_field', 'position': [32, 0, 0], 'facing': 180}],
    },
    {
        'schema_version': SCHEMA_VERSION,
        'name': 'bubble_c',
        'kind': 'interior',
        'bounds': {'min': [-12, -12, -2008], 'max': [12, 12, -1992]},
        'neighbours': [],
        'statics': [],
        'spawn_points': [{'name': 'interior_entry', 'position': [0, 0, -2000], 'facing': 90}],
    },
]

# Every exporter option that changes the file is pinned; a renamed option fails the run.
GLTF_OPTIONS = {
    'export_format': 'GLB',
    'export_yup': True,
    'export_apply': True,
    'export_texcoords': True,
    'export_normals': True,
    'export_tangents': False,
    'export_materials': 'EXPORT',
    'export_vertex_color': 'NONE',
    'export_attributes': False,
    'export_image_format': 'NONE',
    'export_copyright': '',
    'export_extras': False,
    'export_cameras': False,
    'export_lights': False,
    'export_animations': False,
    'export_skins': False,
    'export_morph': False,
    'export_gpu_instances': False,
    'export_gn_mesh': False,
    'export_shared_accessors': False,
    'export_original_specular': False,
    'export_hierarchy_flatten_objs': False,
    'export_draco_mesh_compression_enable': False,
    'export_meshopt_compression_enable': False,
    'export_use_gltfpack': False,
    'use_selection': False,
    'use_visible': False,
    'use_renderable': False,
    'use_active_collection': False,
    'check_existing': False,
    'will_save_settings': False,
}


def arguments():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    parser = argparse.ArgumentParser(prog='generate_testbed.py')
    parser.add_argument('--out', required=True, type=Path, help='bundle folder to write')
    return parser.parse_args(argv)


def material():
    result = bpy.data.materials.new('testbed_grey')
    # Studio refuses double-sided materials; culling exports doubleSided false.
    result.use_backface_culling = True
    shader = result.node_tree.nodes['Principled BSDF']
    shader.inputs['Base Color'].default_value = (0.5, 0.5, 0.5, 1.0)
    shader.inputs['Metallic'].default_value = 0.0
    shader.inputs['Roughness'].default_value = 0.75
    return result


def link(name, mesh, shared):
    mesh.materials.append(shared)
    mesh.shade_flat()
    mesh.validate()
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)


def plate(shared):
    steps = range(-PLATE_HALF, PLATE_HALF + 1, CELL)
    count = len(steps)
    vertices = [(float(x), float(y), 0.0) for y in steps for x in steps]
    # Corners run counter-clockwise seen from +Z, so the face normal is +Z.
    faces = [(j * count + i, j * count + i + 1, (j + 1) * count + i + 1, (j + 1) * count + i)
             for j in range(count - 1) for i in range(count - 1)]
    mesh = bpy.data.meshes.new('terrain')
    mesh.from_pydata(vertices, [], faces)
    uv = mesh.uv_layers.new(name='UVMap')
    for loop in mesh.loops:
        x, y, _ = mesh.vertices[loop.vertex_index].co
        uv.data[loop.index].uv = (x / CELL, y / CELL)
    link('terrain', mesh, shared)


def block(shared):
    mesh = bpy.data.meshes.new('block')
    built = bmesh.new()
    bmesh.ops.create_cube(built, size=1.0)
    size = [high - low for low, high in zip(BLOCK_MIN, BLOCK_MAX)]
    centre = [(low + high) / 2 for low, high in zip(BLOCK_MIN, BLOCK_MAX)]
    for vertex in built.verts:
        vertex.co = [c * s + m for c, s, m in zip(vertex.co, size, centre)]
    built.to_mesh(mesh)
    built.free()
    uv = mesh.uv_layers.new(name='UVMap')
    # Each face is mapped flat onto the two axes it spans, in 2 m tiles like the plate.
    for polygon in mesh.polygons:
        axis = max(range(3), key=lambda a: abs(polygon.normal[a]))
        lanes = [a for a in range(3) if a != axis]
        for index in polygon.loop_indices:
            co = mesh.vertices[mesh.loops[index].vertex_index].co
            uv.data[index].uv = (co[lanes[0]] / CELL, co[lanes[1]] / CELL)
    link('block', mesh, shared)


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(value, indent=2) + '\n').encode('ascii'))


def main():
    out = arguments().out.resolve()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    shared = material()
    plate(shared)
    block(shared)
    write_json(out / 'bundle.json', {
        'schema_version': SCHEMA_VERSION,
        'name': BUNDLE_NAME,
        'bubbles': [bubble['name'] for bubble in BUBBLES],
    })
    for bubble in BUBBLES:
        write_json(out / bubble['name'] / 'bubble.json', bubble)
    static = out / 'bubble_a' / 'static.glb'
    bpy.ops.export_scene.gltf(filepath=str(static), **GLTF_OPTIONS)
    print(f'testbed: wrote {out}')


main()
