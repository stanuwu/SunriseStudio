"""Writes the Testbed World Import Bundle: bundle.json, one bubble.json per bubble, and its glb and PNG files.

Run headless; the same Blender version gives byte-identical files:
  blender -b --factory-startup --gpu-backend vulkan --python generate_testbed.py -- --out <dir>
--heightfield <file> also writes the 2M-vertex stress heightfield there; it is not part of the bundle.
"""

import argparse
import json
import math
from pathlib import Path
import struct
import sys
import zlib

import bmesh
import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
import generate_entities

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
        }, {
            # One block per static profile: textured opaque, alpha cutout, vertex colour, emissive.
            'name': 'profiles',
            'glb': 'profiles.glb',
            'position': [0, 0, 0],
            'rotation': [0, 0, 0, 1],
            'scale': 1,
        }, {
            # A detailed block that switches to a 12-triangle copy past 30 m.
            'name': 'lod_block',
            'glb': 'lod_block.glb',
            'lods': ['lod_block_lod1.glb'],
            'lod_distances': [30],
            'position': [0, 0, 0],
            'rotation': [0, 0, 0, 1],
            'scale': 1,
        }],
        # The arrival spawn sits 0.1 m below the plate, so the plate clears the floor it lands on.
        'spawn_points': [{'name': 'arrival', 'position': [0, -8, -0.1], 'facing': 90}],
        'lights': [{'name': 'lamp', 'position': [6, 4, 4]}],
        # One-metre movement cells over the bubble; a red cell is blocked, so its open edges become walls.
        'permissions': {'png': 'permissions.png', 'origin': [-16, -16, 0], 'cell': 1, 'height': 3},
        # Animated entities: an NPC, two creatures and a door, each a skinned glb with named actions.
        'entities': generate_entities.ENTITIES,
        # A stock trigger volume moved in front of the device door.
        'volumes': generate_entities.VOLUMES,
        # Two new box triggers a mission script arms by name; the second is turned.
        'doors': [{'name': 'lane:0', 'position': [-8, 0, -1], 'size': [2, 1, 3]},
                  {'name': 'house_1', 'position': [8, 0, -1], 'size': [1.5, 1.5, 2.5], 'yaw': 45}],
        # A nav mesh baked from the bubble's collision, so enemies can find paths over it.
        'nav': True,
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


FAR = [{'name': 'bubble_b', 'glb': 'far/far_bubble_b.glb', 'covers': ['bubble_b'], 'margin': 2, 'sink': 0.5}]
SKY = {'panorama': 'sky/dome.png'}

# Texture size of the generated checker, cutout and atlas images.
TEXTURE = 64


def arguments():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    parser = argparse.ArgumentParser(prog='generate_testbed.py')
    parser.add_argument('--out', required=True, type=Path, help='bundle folder to write')
    parser.add_argument('--heightfield', type=Path, help='also write the 2M-vertex stress heightfield glb here')
    return parser.parse_args(argv)


# ---------------------------------------------------------------- plain glb and PNG writers
def png(width, height, rgba):
    """An 8-bit RGBA PNG; rgba is a flat list of bytes, rows top first."""
    raw = b''.join(b'\x00' + bytes(rgba[y * width * 4:(y + 1) * width * 4]) for y in range(height))

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def to_gltf(v):
    """Game (x, y, z), Z up, to glTF (x, z, -y); Studio's reader maps it back."""
    return (v[0], v[2], -v[1])


class Glb:
    """A glb of triangle primitives, materials and PNG images, in one binary chunk."""

    def __init__(self):
        self.binary = bytearray()
        self.views, self.accessors, self.meshes, self.nodes = [], [], [], []
        self.materials, self.textures, self.images = [], [], []

    def _view(self, data, target=None):
        while len(self.binary) % 4:
            self.binary.append(0)
        view = {'buffer': 0, 'byteOffset': len(self.binary), 'byteLength': len(data)}
        if target:
            view['target'] = target
        self.binary += data
        self.views.append(view)
        return len(self.views) - 1

    def _floats(self, rows, kind, bounds=False):
        view = self._view(b''.join(struct.pack('<' + 'f' * len(r), *r) for r in rows), 34962)
        accessor = {'bufferView': view, 'componentType': 5126, 'count': len(rows), 'type': kind}
        if bounds:
            accessor['min'] = [min(r[i] for r in rows) for i in range(len(rows[0]))]
            accessor['max'] = [max(r[i] for r in rows) for i in range(len(rows[0]))]
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def image(self, data):
        view = self._view(data)
        self.images.append({'bufferView': view, 'mimeType': 'image/png'})
        self.textures.append({'source': len(self.images) - 1})
        return len(self.textures) - 1

    def material(self, value):
        self.materials.append(value)
        return len(self.materials) - 1

    def mesh(self, name, positions, normals, uvs, indices, material, colors=None):
        """Game-space positions and normals; uvs in glTF image order."""
        attributes = {'POSITION': self._floats([to_gltf(p) for p in positions], 'VEC3', True),
                      'NORMAL': self._floats([to_gltf(n) for n in normals], 'VEC3'),
                      'TEXCOORD_0': self._floats(uvs, 'VEC2')}
        if colors is not None:
            attributes['COLOR_0'] = self._floats(colors, 'VEC4')
        view = self._view(struct.pack('<' + 'I' * len(indices), *indices), 34963)
        self.accessors.append({'bufferView': view, 'componentType': 5125, 'count': len(indices), 'type': 'SCALAR'})
        self.meshes.append({'name': name, 'primitives': [{'attributes': attributes, 'indices': len(self.accessors) - 1,
                                                          'material': material, 'mode': 4}]})
        self.nodes.append({'name': name, 'mesh': len(self.meshes) - 1})

    def write(self, path):
        while len(self.binary) % 4:
            self.binary.append(0)
        document = {'asset': {'version': '2.0', 'generator': 'generate_testbed.py'}, 'scene': 0,
                    'scenes': [{'nodes': list(range(len(self.nodes)))}], 'nodes': self.nodes, 'meshes': self.meshes,
                    'accessors': self.accessors, 'bufferViews': self.views,
                    'buffers': [{'byteLength': len(self.binary)}]}
        for key, value in (('materials', self.materials), ('textures', self.textures), ('images', self.images)):
            if value:
                document[key] = value
        text = json.dumps(document, separators=(',', ':')).encode('ascii')
        text += b' ' * (-len(text) % 4)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(struct.pack('<III', 0x46546C67, 2, 28 + len(text) + len(self.binary))
                         + struct.pack('<II', len(text), 0x4E4F534A) + text
                         + struct.pack('<II', len(self.binary), 0x004E4942) + bytes(self.binary))


def box(low, high, subdivisions=1, puff=0.0):
    """A box with one UV square per face; puff pushes subdivided vertices toward a sphere."""
    centre = [(a + b) / 2 for a, b in zip(low, high)]
    half = [(b - a) / 2 for a, b in zip(low, high)]
    positions, normals, uvs, indices = [], [], [], []
    for axis in range(3):
        for sign in (-1, 1):
            # Cyclic lanes wind each face counter-clockwise seen from outside.
            u_axis, v_axis = (axis + 1) % 3, (axis + 2) % 3
            if sign < 0:
                u_axis, v_axis = v_axis, u_axis
            start = len(positions)
            n = subdivisions
            for j in range(n + 1):
                for i in range(n + 1):
                    local = [0.0, 0.0, 0.0]
                    local[axis] = sign
                    local[u_axis] = -1 + 2 * i / n
                    local[v_axis] = -1 + 2 * j / n
                    length = math.sqrt(sum(c * c for c in local))
                    shaped = [c * (1 - puff) + c / length * puff for c in local]
                    positions.append(tuple(centre[k] + shaped[k] * half[k] for k in range(3)))
                    normal = [0.0, 0.0, 0.0]
                    normal[axis] = float(sign)
                    normals.append(tuple(normal))
                    uvs.append((i / n, 1 - j / n))
            for j in range(n):
                for i in range(n):
                    a = start + j * (n + 1) + i
                    b, c, d = a + 1, a + n + 1, a + n + 2
                    indices += [a, b, d, a, d, c]
    return positions, normals, uvs, indices


def grid(x0, x1, y0, y1, cell, height=lambda x, y: 0.0):
    """A grid of square cells at Z = height, normals up, UVs one unit per cell."""
    columns, rows = int((x1 - x0) / cell) + 1, int((y1 - y0) / cell) + 1
    positions = [(x0 + i * cell, y0 + j * cell, height(x0 + i * cell, y0 + j * cell)) for j in range(rows) for i in range(columns)]
    normals = [(0.0, 0.0, 1.0)] * len(positions)
    uvs = [(i, j) for j in range(rows) for i in range(columns)]
    indices = []
    for j in range(rows - 1):
        for i in range(columns - 1):
            a = j * columns + i
            indices += [a, a + 1, a + columns + 1, a, a + columns + 1, a + columns]
    return positions, normals, uvs, indices


def checker():
    """8 by 8 texel cells, two greys and a red corner cell, so a flip shows."""
    pixels = []
    for y in range(TEXTURE):
        for x in range(TEXTURE):
            on = (x // 8 + y // 8) % 2 == 0
            red = x < 8 and y < 8
            pixels += [230, 40, 40, 255] if red else [200, 200, 200, 255] if on else [60, 60, 60, 255]
    return png(TEXTURE, TEXTURE, pixels)


def cutout():
    """Green leaves: opaque inside a circle of each 16 texel cell, transparent outside it."""
    pixels = []
    for y in range(TEXTURE):
        for x in range(TEXTURE):
            dx, dy = x % 16 - 7.5, y % 16 - 7.5
            inside = dx * dx + dy * dy < 36
            pixels += [50, 160, 60, 255 if inside else 0]
    return png(TEXTURE, TEXTURE, pixels)


def atlas():
    """Four colour quadrants; each far cell samples one."""
    colours = [(90, 140, 70), (120, 110, 80), (70, 110, 150), (160, 150, 120)]
    pixels = []
    for y in range(TEXTURE):
        for x in range(TEXTURE):
            pixels += list(colours[(y >= TEXTURE // 2) * 2 + (x >= TEXTURE // 2)]) + [255]
    return png(TEXTURE, TEXTURE, pixels)


def dome():
    """Deep blue at the top to pale orange at the horizon, 256 by 96."""
    pixels = []
    for y in range(96):
        t = y / 95
        pixels += [int(20 + t * 220), int(40 + t * 140), int(140 - t * 60), 255] * 256
    return png(256, 96, pixels)


def profiles(path):
    """Four 1.5 m blocks along game +X at Y = 10, one per static profile."""
    glb = Glb()
    checker_texture, cutout_texture = glb.image(checker()), glb.image(cutout())
    opaque = glb.material({'name': 'checker_opaque', 'pbrMetallicRoughness': {
        'baseColorTexture': {'index': checker_texture}, 'metallicFactor': 0.0, 'roughnessFactor': 0.8}})
    masked = glb.material({'name': 'leaf_cutout', 'alphaMode': 'MASK', 'alphaCutoff': 0.5, 'pbrMetallicRoughness': {
        'baseColorTexture': {'index': cutout_texture}, 'metallicFactor': 0.0, 'roughnessFactor': 0.9}})
    painted = glb.material({'name': 'vertex_painted', 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 0.7}})
    glow = glb.material({'name': 'night_window', 'emissiveFactor': [1.0, 0.6, 0.2], 'pbrMetallicRoughness': {
        'baseColorFactor': [0.2, 0.2, 0.2, 1.0], 'metallicFactor': 0.0, 'roughnessFactor': 0.5}})
    for index, (name, material) in enumerate((('block_opaque', opaque), ('block_cutout', masked),
                                              ('block_vertex_colour', painted), ('block_emissive', glow))):
        x = -10 + index * 3
        positions, normals, uvs, indices = box((x, 9.25, 0.0), (x + 1.5, 10.75, 1.5))
        colors = None
        if material == painted:
            # Red at the bottom to blue at the top, so the colour stream is visibly per vertex.
            colors = [(1.0 - p[2] / 1.5, 0.2, p[2] / 1.5, 1.0) for p in positions]
        glb.mesh(name, positions, normals, uvs, indices, material, colors)
    glb.write(path)


def lod_block(near_path, far_path):
    """A rounded 2 m block of 768 triangles, and its 12-triangle copy, at game (10, -6)."""
    low, high = (9.0, -7.0, 0.0), (11.0, -5.0, 2.0)
    for path, subdivisions, puff in ((near_path, 8, 0.25), (far_path, 1, 0.0)):
        glb = Glb()
        material = glb.material({'name': 'lod_grey', 'pbrMetallicRoughness': {
            'baseColorFactor': [0.55, 0.5, 0.45, 1.0], 'metallicFactor': 0.0, 'roughnessFactor': 0.8}})
        glb.mesh('lod_block', *box(low, high, subdivisions, puff), material)
        glb.write(path)


def far_tile(path):
    """A 64 by 32 m grid of 4 m cells over bubble B and past it; the atlas spans it, one quadrant per corner region."""
    glb = Glb()
    texture = glb.image(atlas())
    material = glb.material({'name': 'far_atlas', 'pbrMetallicRoughness': {
        'baseColorTexture': {'index': texture}, 'metallicFactor': 0.0, 'roughnessFactor': 1.0}})
    positions, normals, _, indices = grid(0, 64, -16, 16, 4)
    uvs = [(p[0] / 64, (p[1] + 16) / 32) for p in positions]
    glb.mesh('far_bubble_b', positions, normals, uvs, indices, material)
    glb.write(path)

def heightfield(path):
    """1,415 by 1,415 vertices over 1.2 km: the stress case for snorm16 splitting."""
    glb = Glb()
    material = glb.material({'name': 'heightfield', 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 1.0}})
    def height(x, y):
        return 20 * math.sin(x * 0.01) * math.cos(y * 0.013) + 3 * math.sin(x * 0.11 + y * 0.07)
    glb.mesh('heightfield', *grid(0, 1414 * 0.85, 0, 1414 * 0.85, 0.85, height), material)
    glb.write(path)


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


# Blocked cells: a 1 m wide, 12 m long wall across the plate at X -3 to -2, Y -6 to 6.
WALL_COLUMNS = range(13, 14)
WALL_ROWS_FROM_SOUTH = range(10, 22)


def permissions(path):
    """A 32 by 32 grid; image row 0 is the +Y edge, so the south row is written last."""
    side = 32
    pixels = bytearray()
    for row in range(side):
        south = side - 1 - row
        for column in range(side):
            blocked = column in WALL_COLUMNS and south in WALL_ROWS_FROM_SOUTH
            pixels += bytes((255, 0, 0, 255) if blocked else (0, 0, 0, 255))
    path.write_bytes(png(side, side, bytes(pixels)))


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(value, indent=2) + '\n').encode('ascii'))


def main():
    args = arguments()
    out = args.out.resolve()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    shared = material()
    plate(shared)
    block(shared)
    write_json(out / 'bundle.json', {
        'schema_version': SCHEMA_VERSION,
        'name': BUNDLE_NAME,
        'bubbles': [bubble['name'] for bubble in BUBBLES],
        'far': FAR,
        'sounds': generate_entities.SOUNDS,
        'sky': SKY,
    })
    for bubble in BUBBLES:
        write_json(out / bubble['name'] / 'bubble.json', bubble)
    static = out / 'bubble_a' / 'static.glb'
    bpy.ops.export_scene.gltf(filepath=str(static), **GLTF_OPTIONS)
    profiles(out / 'bubble_a' / 'profiles.glb')
    permissions(out / 'bubble_a' / BUBBLES[0]['permissions']['png'])
    lod_block(out / 'bubble_a' / 'lod_block.glb', out / 'bubble_a' / 'lod_block_lod1.glb')
    generate_entities.write_all(out / 'bubble_a')
    far_tile(out / FAR[0]['glb'])
    (out / SKY['panorama']).parent.mkdir(parents=True, exist_ok=True)
    (out / SKY['panorama']).write_bytes(dome())
    if args.heightfield:
        heightfield(args.heightfield.resolve())
        print(f'testbed: wrote {args.heightfield}')
    print(f'testbed: wrote {out}')


main()
