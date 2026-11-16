"""Checks the Testbed World Import Bundle against its schema and against Studio's glTF import rules.

Usage: python check_testbed.py [--bundle DIR] [--compare DIR]
--compare also requires every file of DIR to match the bundle byte for byte.
"""

import argparse
import json
import math
from pathlib import Path
import re
import struct
import sys

HERE = Path(__file__).resolve().parent
SCHEMA = HERE / 'schema' / 'world-import-bundle.schema.json'

# Studio's glTF reader maps glTF (x, y, z) to game (x, -z, y) before anything else.
def to_game(v):
    return (v[0], -v[2], v[1])

# MeshAssetImporter.ValidateMesh limits.
MAX_VERTICES = 4_000_000
MAX_INDICES = 24_000_000

# Exact game-space bounds of each fixture node, by glb file name; they prove the plate is flat, the
# blocks stand up, and no file moved or mirrored its geometry.
EXPECTED_NODES = {
    'static.glb': {'terrain': ((-12.0, -12.0, 0.0), (12.0, 12.0, 0.0)), 'block': ((5.0, 3.0, 0.0), (7.0, 5.0, 3.0))},
    'profiles.glb': {f'block_{kind}': ((x, 9.25, 0.0), (x + 1.5, 10.75, 1.5))
                     for kind, x in (('opaque', -10.0), ('cutout', -7.0), ('vertex_colour', -4.0), ('emissive', -1.0))},
    'lod_block.glb': {'lod_block': ((9.0, -7.0, 0.0), (11.0, -5.0, 2.0))},
    'lod_block_lod1.glb': {'lod_block': ((9.0, -7.0, 0.0), (11.0, -5.0, 2.0))},
    'far_bubble_b.glb': {'far_bubble_b': ((0.0, -16.0, 0.0), (64.0, 16.0, 0.0))},
}

# Each file's materials: alpha mode and whether it emits. Studio refuses blending and double sides.
EXPECTED_MATERIALS = {
    'static.glb': {'testbed_grey': ('OPAQUE', False)},
    'profiles.glb': {'checker_opaque': ('OPAQUE', False), 'leaf_cutout': ('MASK', False),
                     'vertex_painted': ('OPAQUE', False), 'night_window': ('OPAQUE', True)},
    'lod_block.glb': {'lod_grey': ('OPAQUE', False)},
    'lod_block_lod1.glb': {'lod_grey': ('OPAQUE', False)},
    'far_bubble_b.glb': {'far_atlas': ('OPAQUE', False)},
}

# The one node whose primitive carries COLOR_0.
COLOURED_NODES = {'block_vertex_colour'}

COMPONENTS = {5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2), 5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
WIDTHS = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}

failures = []


def fail(message):
    failures.append(message)
    print('FAIL ' + message)


# ---------------------------------------------------------------- schema
def subset_validate(schema, root, value, path, errors):
    """Validates the keywords this schema uses; any other keyword is an error, not a pass."""
    annotations = {'$schema', '$id', 'title', 'description', '$defs'}
    known = annotations | {'$ref', 'const', 'enum', 'type', 'required', 'properties',
                           'additionalProperties', 'items', 'minItems', 'maxItems', 'uniqueItems',
                           'pattern', 'maxLength', 'minimum', 'maximum', 'exclusiveMinimum'}
    unknown = set(schema) - known
    if unknown:
        errors.append(f'{path}: validator has no support for {sorted(unknown)}')
        return
    if '$ref' in schema:
        target = root
        for part in schema['$ref'].removeprefix('#/').split('/'):
            target = target[part]
        subset_validate(target, root, value, path, errors)
    if 'const' in schema and value != schema['const']:
        errors.append(f'{path}: must be {schema["const"]!r}')
    if 'enum' in schema and value not in schema['enum']:
        errors.append(f'{path}: must be one of {schema["enum"]}')
    kind = schema.get('type')
    types = {'object': dict, 'array': list, 'string': str}
    if kind in types and not isinstance(value, types[kind]):
        errors.append(f'{path}: must be {kind}')
        return
    is_number = isinstance(value, (int, float)) and not isinstance(value, bool)
    if kind == 'number' and not is_number:
        errors.append(f'{path}: must be number')
        return
    if isinstance(value, dict):
        for name in schema.get('required', []):
            if name not in value:
                errors.append(f'{path}: missing {name}')
        properties = schema.get('properties', {})
        for name, item in value.items():
            if name in properties:
                subset_validate(properties[name], root, item, f'{path}.{name}', errors)
            elif schema.get('additionalProperties') is False:
                errors.append(f'{path}: unexpected {name}')
    if isinstance(value, list):
        if len(value) < schema.get('minItems', 0) or len(value) > schema.get('maxItems', math.inf):
            errors.append(f'{path}: wrong item count {len(value)}')
        if schema.get('uniqueItems') and len({json.dumps(x, sort_keys=True) for x in value}) != len(value):
            errors.append(f'{path}: items must be unique')
        if 'items' in schema:
            for index, item in enumerate(value):
                subset_validate(schema['items'], root, item, f'{path}[{index}]', errors)
    if isinstance(value, str):
        if 'pattern' in schema and not re.search(schema['pattern'], value):
            errors.append(f'{path}: does not match {schema["pattern"]}')
        if len(value) > schema.get('maxLength', math.inf):
            errors.append(f'{path}: too long')
    if is_number:
        if value < schema.get('minimum', -math.inf) or value > schema.get('maximum', math.inf):
            errors.append(f'{path}: out of range')
        if 'exclusiveMinimum' in schema and value <= schema['exclusiveMinimum']:
            errors.append(f'{path}: must be above {schema["exclusiveMinimum"]}')


def make_validator(schema):
    """Returns (label, validate); validate(document, sub_schema) gives a list of error strings."""
    try:
        import jsonschema
    except ImportError:
        jsonschema = None
    if jsonschema is not None:
        jsonschema.Draft202012Validator.check_schema(schema)

        def validate(document, sub):
            validator = jsonschema.Draft202012Validator(sub)
            return [f'{"/".join(map(str, e.absolute_path)) or "<root>"}: {e.message}'
                    for e in validator.iter_errors(document)]
        from importlib.metadata import version
        return 'jsonschema ' + version('jsonschema'), validate

    def validate(document, sub):
        errors = []
        subset_validate(sub, sub, document, '<root>', errors)
        return errors
    return 'built-in subset validator (jsonschema is not installed)', validate


def bundle_schema(schema):
    """The bundle definition as a standalone schema that still resolves #/$defs references."""
    sub = dict(schema['$defs']['bundle'])
    sub['$schema'] = schema['$schema']
    sub['$defs'] = schema['$defs']
    return sub


# ---------------------------------------------------------------- glb
def read_glb(path):
    data = path.read_bytes()
    if len(data) < 20:
        raise ValueError('file is shorter than a GLB header')
    magic, version, length = struct.unpack_from('<III', data, 0)
    if magic != 0x46546C67 or version != 2 or length != len(data):
        raise ValueError('bad GLB header')
    chunks = []
    offset = 12
    while offset < len(data):
        size, kind = struct.unpack_from('<II', data, offset)
        if size % 4 or offset + 8 + size > len(data):
            raise ValueError('bad GLB chunk size')
        chunks.append((kind, data[offset + 8:offset + 8 + size]))
        offset += 8 + size
    if not chunks or chunks[0][0] != 0x4E4F534A:
        raise ValueError('GLB does not start with a JSON chunk')
    if len(chunks) > 2 or len(chunks) == 2 and chunks[1][0] != 0x004E4942:
        raise ValueError('GLB has chunks other than JSON and one BIN')
    document = json.loads(chunks[0][1])
    binary = chunks[1][1] if len(chunks) == 2 else b''
    if document.get('asset', {}).get('version') != '2.0':
        raise ValueError('not glTF 2.0')
    return document, binary


def check_entity(path, entity, label, box):
    """Studio's entity import rules: one skinned mesh of one primitive, unique joint names, named actions."""
    if not inside(entity['position'], box):
        fail(f'{label}: outside the bubble bounds')
    try:
        document, _ = read_glb(path)
    except (OSError, ValueError) as error:
        fail(f'{label}: {error}')
        return
    nodes = document.get('nodes', [])
    skinned = [node for node in nodes if 'mesh' in node]
    skins = document.get('skins', [])
    if len(skinned) != 1 or 'skin' not in skinned[0] or len(skins) != 1:
        fail(f'{label}: needs one skinned mesh and one skin')
        return
    if len(document['meshes'][skinned[0]['mesh']]['primitives']) != 1:
        fail(f'{label}: needs one primitive')
    joints = [nodes[index].get('name', '') for index in skins[0]['joints']]
    if len(set(joints)) != len(joints) or '' in joints or len(joints) > 256:
        fail(f'{label}: joints need unique names and number at most 256')
    if 'inverseBindMatrices' not in skins[0]:
        fail(f'{label}: needs inverse bind matrices')
    actions = [animation.get('name', '') for animation in document.get('animations', [])]
    # An entity either loops one action or is a device whose position channel scrubs one.
    if ('plays' in entity) == ('device' in entity):
        fail(f'{label}: needs either plays or device')
        return
    action = entity['plays'] if 'plays' in entity else entity['device']['position']
    role = 'plays' if 'plays' in entity else f'device {entity["device"]["slot"]} scrubs'
    for event in entity.get('events', []):
        if event['action'] not in actions:
            fail(f'{label}: an event names action {event["action"]}, which is not one of {actions}')
        if ('sound' in event) == ('effect' in event):
            fail(f'{label}: an event needs either sound or effect')
    if action not in actions:
        fail(f'{label}: {role} {action}, which is not one of {actions}')
    else:
        print(f'  entity {entity["name"]} at {entity["position"]}: {len(joints)} joints, {role} {action} of {actions}')


def read_accessor(document, binary, index):
    accessor = document['accessors'][index]
    if 'sparse' in accessor:
        raise ValueError('sparse accessor')
    view = document['bufferViews'][accessor['bufferView']]
    if view['buffer'] != 0 or 'uri' in document['buffers'][0]:
        raise ValueError('accessor data is not in the GLB binary chunk')
    code, size = COMPONENTS[accessor['componentType']]
    width = WIDTHS[accessor['type']]
    stride = view.get('byteStride', width * size)
    start = view.get('byteOffset', 0) + accessor.get('byteOffset', 0)
    count = accessor['count']
    end = view.get('byteOffset', 0) + view['byteLength']
    if count and start + (count - 1) * stride + width * size > end:
        raise ValueError('accessor leaves its buffer view')
    rows = [struct.unpack_from('<' + code * width, binary, start + i * stride) for i in range(count)]
    if accessor.get('normalized'):
        raise ValueError('normalized accessors are not expected in this fixture')
    return accessor, rows


def matmul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def node_matrix(node):
    """Column-vector 4x4 matrix of a glTF node: translation * rotation * scale."""
    if 'matrix' in node:
        m = node['matrix']
        return [[m[c * 4 + r] for c in range(4)] for r in range(4)]
    tx, ty, tz = node.get('translation', [0, 0, 0])
    x, y, z, w = node.get('rotation', [0, 0, 0, 1])
    sx, sy, sz = node.get('scale', [1, 1, 1])
    r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    s = (sx, sy, sz)
    out = [[r[i][j] * s[j] for j in range(3)] + [(tx, ty, tz)[i]] for i in range(3)]
    return out + [[0, 0, 0, 1]]


def apply(m, p):
    return tuple(m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] for i in range(3))


def normal_matrix(m):
    """Inverse transpose of the 3x3 part of m."""
    a = [row[:3] for row in m[:3]]
    cof = [[a[(i + 1) % 3][(j + 1) % 3] * a[(i + 2) % 3][(j + 2) % 3]
            - a[(i + 1) % 3][(j + 2) % 3] * a[(i + 2) % 3][(j + 1) % 3] for j in range(3)] for i in range(3)]
    det = sum(a[0][j] * cof[0][j] for j in range(3))
    if abs(det) < 1e-12:
        raise ValueError('singular node transform')
    return [[cof[i][j] / det for j in range(3)] for i in range(3)], det


def normalize(v):
    length = math.sqrt(sum(c * c for c in v))
    return tuple(c / length for c in v) if length > 1e-12 else (0.0, 0.0, 1.0)


def bounds_of(points):
    return (tuple(min(p[i] for p in points) for i in range(3)),
            tuple(max(p[i] for p in points) for i in range(3)))


def check_material(document, label, expected):
    materials = document.get('materials', [])
    names = [material.get('name', '?') for material in materials]
    if sorted(names) != sorted(expected):
        fail(f'{label}: materials {names}, expected {sorted(expected)}')
    for material in materials:
        name = material.get('name', '?')
        mode, emits = expected.get(name, ('OPAQUE', False))
        if material.get('alphaMode', 'OPAQUE') != mode:
            fail(f'{label}: material {name} is {material.get("alphaMode", "OPAQUE")}, expected {mode}')
        if material.get('doubleSided', False):
            fail(f'{label}: material {name} is double-sided; Studio refuses it')
        if bool(any(material.get('emissiveFactor', [0, 0, 0])) or 'emissiveTexture' in material) != emits:
            fail(f'{label}: material {name} {"lacks" if emits else "has"} emission')
        if material.get('extensions'):
            fail(f'{label}: material {name} has extensions; Studio refuses them')


def check_glb(path, label):
    """Returns (game-space bounds, vertex count, triangle count), or None when the file is unusable."""
    try:
        document, binary = read_glb(path)
    except (ValueError, KeyError, json.JSONDecodeError, struct.error) as error:
        fail(f'{label}: {error}')
        return None
    for key in ('extensionsRequired', 'skins', 'animations'):
        if document.get(key):
            fail(f'{label}: has {key}; the static import path refuses it')
    expected_nodes = EXPECTED_NODES.get(path.name, {})
    check_material(document, label, EXPECTED_MATERIALS.get(path.name, {}))
    nodes = document['nodes']
    scene = document['scenes'][document.get('scene', 0)]
    identity = [[1 if i == j else 0 for j in range(4)] for i in range(4)]
    stack = [(index, identity) for index in reversed(scene['nodes'])]
    points, vertices, triangles = [], 0, 0
    while stack:
        index, parent = stack.pop()
        node = nodes[index]
        world = matmul(parent, node_matrix(node))
        for child in reversed(node.get('children', [])):
            stack.append((child, world))
        if 'mesh' not in node:
            continue
        name = node.get('name', f'node{index}')
        inverse_t, det = normal_matrix(world)
        node_points = []
        for primitive in document['meshes'][node['mesh']]['primitives']:
            where = f'{label}: node {name}'
            attributes = primitive['attributes']
            if primitive.get('mode', 4) != 4 or 'targets' in primitive:
                fail(f'{where}: primitive is not plain triangles')
                continue
            wanted = ('POSITION', 'NORMAL', 'TEXCOORD_0') + (('COLOR_0',) if name in COLOURED_NODES else ())
            missing = [a for a in wanted if a not in attributes]
            extra = [a for a in attributes if a not in wanted]
            if missing or extra:
                fail(f'{where}: needs exactly {", ".join(wanted)}; missing {missing}, extra {extra}')
                continue
            accessor, positions = read_accessor(document, binary, attributes['POSITION'])
            _, normals = read_accessor(document, binary, attributes['NORMAL'])
            _, uvs = read_accessor(document, binary, attributes['TEXCOORD_0'])
            index_accessor, rows = read_accessor(document, binary, primitive['indices'])
            indices = [row[0] for row in rows]
            if accessor['componentType'] != 5126 or index_accessor['componentType'] not in (5121, 5123, 5125):
                fail(f'{where}: unexpected component types')
            if not len(positions) == len(normals) == len(uvs):
                fail(f'{where}: attribute counts differ')
            if len(indices) % 3 or any(i >= len(positions) for i in indices):
                fail(f'{where}: bad triangle indices')
                continue
            declared = (tuple(accessor.get('min', ())), tuple(accessor.get('max', ())))
            measured = bounds_of(positions)
            if declared != measured:
                fail(f'{where}: accessor min/max {declared} differ from the data {measured}')
            game = [to_game(apply(world, p)) for p in positions]
            game_normals = [normalize(to_game(tuple(sum(inverse_t[i][j] * n[j] for j in range(3))
                                                    for i in range(3)))) for n in normals]
            # Front faces wind counter-clockwise around their normal; a mirror reverses winding.
            flip = -1 if det < 0 else 1
            for t in range(0, len(indices), 3):
                a, b, c = (game[i] for i in indices[t:t + 3])
                cross = ((b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]),
                         (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]),
                         (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]))
                n = game_normals[indices[t]]
                if flip * sum(cross[i] * n[i] for i in range(3)) <= 0:
                    fail(f'{where}: triangle {t // 3} winds against its normal')
                    break
            if name == 'terrain' and any(abs(n[2] - 1) > 1e-6 for n in game_normals):
                fail(f'{where}: plate normals do not all point to game +Z')
            node_points += game
            vertices += len(positions)
            triangles += len(indices) // 3
        if node_points and name in expected_nodes:
            got = bounds_of(node_points)
            want = expected_nodes[name]
            if any(abs(g - w) > 1e-6 for pair in zip(got, want) for g, w in zip(*pair)):
                fail(f'{label}: node {name} game bounds {got}, expected {want}')
            else:
                print(f'  node {name}: game bounds {got[0]} .. {got[1]}')
        points += node_points
    missing = set(expected_nodes) - {n.get('name') for n in nodes}
    if missing:
        fail(f'{label}: missing nodes {sorted(missing)}')
    if vertices > MAX_VERTICES or triangles * 3 > MAX_INDICES or vertices < 3:
        fail(f'{label}: {vertices} vertices / {triangles} triangles is outside the importer limits')
    if not points:
        fail(f'{label}: no triangle geometry')
        return None
    return bounds_of(points), vertices, triangles


# ---------------------------------------------------------------- bundle
def rotate(q, v):
    x, y, z, w = q
    u = (x, y, z)
    t = [2 * (u[(i + 1) % 3] * v[(i + 2) % 3] - u[(i + 2) % 3] * v[(i + 1) % 3]) for i in range(3)]
    return tuple(v[i] + w * t[i] + (u[(i + 1) % 3] * t[(i + 2) % 3] - u[(i + 2) % 3] * t[(i + 1) % 3])
                 for i in range(3))


def inside(point, box, slack=1e-6):
    return all(box['min'][i] - slack <= point[i] <= box['max'][i] + slack for i in range(3))


def check_static(folder, bubble, static, label):
    glb = folder / static['glb']
    if not glb.resolve().is_relative_to(folder.resolve()):
        fail(f'{label}: glb path leaves the bubble folder')
        return
    if not glb.is_file():
        fail(f'{label}: missing {static["glb"]}')
        return
    q = static['rotation']
    if abs(math.sqrt(sum(c * c for c in q)) - 1) > 1e-4:
        fail(f'{label}: rotation is not unit length')
        return
    result = check_glb(glb, label)
    if result is None:
        return
    (low, high), vertices, triangles = result
    corners = [(a, b, c) for a in (low[0], high[0]) for b in (low[1], high[1]) for c in (low[2], high[2])]
    placed = [tuple(p + r for p, r in zip(static['position'], rotate(q, tuple(static['scale'] * c for c in corner))))
              for corner in corners]
    placed_bounds = bounds_of(placed)
    print(f'  {label}: {vertices} vertices, {triangles} triangles, '
          f'placed bounds {placed_bounds[0]} .. {placed_bounds[1]}')
    if not all(inside(p, bubble['bounds']) for p in placed):
        fail(f'{label}: placed bounds {placed_bounds} leave the bubble bounds')
    lods = static.get('lods', [])
    if len(static.get('lod_distances', [])) > len(lods):
        fail(f'{label}: more lod_distances than lods')
    for lod in lods:
        path = folder / lod
        if not path.resolve().is_relative_to(folder.resolve()) or not path.is_file():
            fail(f'{label}: lod {lod} is missing or leaves the bubble folder')
            continue
        lod_result = check_glb(path, f'{label} lod {lod}')
        if lod_result is not None:
            print(f'  {label} lod {lod}: {lod_result[1]} vertices, {lod_result[2]} triangles')


def check_bundle(root, validate):
    try:
        bundle = json.loads((root / 'bundle.json').read_text(encoding='utf-8'))
    except (OSError, json.JSONDecodeError) as error:
        fail(f'bundle.json: {error}')
        return
    schema = json.loads(SCHEMA.read_text(encoding='utf-8'))
    for error in validate(bundle, bundle_schema(schema)):
        fail(f'bundle.json: {error}')
    bubbles = {}
    for name in bundle.get('bubbles', []):
        path = root / name / 'bubble.json'
        try:
            bubble = json.loads(path.read_text(encoding='utf-8'))
        except (OSError, json.JSONDecodeError) as error:
            fail(f'{name}/bubble.json: {error}')
            continue
        errors = validate(bubble, schema)
        for error in errors:
            fail(f'{name}/bubble.json: {error}')
        if errors:
            continue
        if bubble['name'] != name:
            fail(f'{name}/bubble.json: name {bubble["name"]} differs from its folder')
        bubbles[name] = bubble
    listed = {p.parent.name for p in root.glob('*/bubble.json')}
    for name in sorted(listed - set(bundle.get('bubbles', []))):
        fail(f'{name}/bubble.json is not listed in bundle.json')
    for name, bubble in bubbles.items():
        print(f'{name} ({bubble["kind"]})')
        box = bubble['bounds']
        if any(box['min'][i] >= box['max'][i] for i in range(3)):
            fail(f'{name}: bounds min is not below max on every axis')
        for other in bubble['neighbours']:
            if other == name or other not in bubbles:
                fail(f'{name}: neighbour {other} is not another bubble of the bundle')
                continue
            if name not in bubbles[other]['neighbours']:
                fail(f'{name}: neighbour {other} does not list it back')
            ob = bubbles[other]['bounds']
            if any(box['min'][i] > ob['max'][i] or ob['min'][i] > box['max'][i] for i in range(3)):
                fail(f'{name}: neighbour {other} neither touches nor overlaps it')
        names = [s['name'] for s in bubble['statics'] if 'name' in s]
        if len(names) != len(set(names)):
            fail(f'{name}: static names repeat')
        for index, static in enumerate(bubble['statics']):
            check_static(root / name, bubble, static, f'{name} static {static.get("name", index)}')
        spawns = bubble.get('spawn_points', [])
        if len({s['name'] for s in spawns}) != len(spawns):
            fail(f'{name}: spawn point names repeat')
        for spawn in spawns:
            if not inside(spawn['position'], box):
                fail(f'{name}: spawn point {spawn["name"]} is outside the bubble bounds')
            else:
                print(f'  spawn {spawn["name"]} at {spawn["position"]}')
        lights = bubble.get('lights', [])
        if len({light['name'] for light in lights}) != len(lights):
            fail(f'{name}: light names repeat')
        for light in lights:
            if not inside(light['position'], box):
                fail(f'{name}: light {light["name"]} is outside the bubble bounds')
            else:
                print(f'  light {light["name"]} at {light["position"]}')
        entities = bubble.get('entities', [])
        if len({entity['name'] for entity in entities}) != len(entities):
            fail(f'{name}: entity names repeat')
        for entity in entities:
            check_entity(root / name / entity['glb'], entity, f'{name} entity {entity["name"]}', box)
        if 'permissions' in bubble:
            check_permissions(root / name, bubble['permissions'], f'{name} permissions')
    for tile in bundle.get('far', []):
        label = f'far {tile["name"]}'
        for covered in tile['covers']:
            if covered not in bubbles or bubbles[covered]['kind'] != 'outdoor':
                fail(f'{label}: covers {covered}, which is not an outdoor bubble of the bundle')
        path = root / tile['glb']
        if not path.resolve().is_relative_to(root.resolve()) or not path.is_file():
            fail(f'{label}: {tile["glb"]} is missing or leaves the bundle folder')
            continue
        result = check_glb(path, label)
        if result is not None:
            print(f'{label}: {result[1]} vertices, {result[2]} triangles, covers {tile["covers"]}')
    sounds = bundle.get('sounds', [])
    if len({sound['name'] for sound in sounds}) != len(sounds):
        fail('sounds: names repeat')
    for sound in sounds:
        extra = list(sound.get('variations', [])) + list(sound.get('switches', {}).values())
        for other in extra:
            if not (root / other).resolve().is_relative_to(root.resolve()) or not (root / other).is_file():
                fail(f'sound {sound["name"]}: {other} is missing or leaves the bundle folder')
        path = root / sound['wav']
        if not path.resolve().is_relative_to(root.resolve()) or not path.is_file():
            fail(f'sound {sound["name"]}: {sound["wav"]} is missing or leaves the bundle folder')
            continue
        data = path.read_bytes()
        # A canonical PCM header: format 1, 1 or 2 channels, 16 bits.
        if data[:4] != b'RIFF' or data[8:16] != b'WAVEfmt ' or struct.unpack('<H', data[20:22])[0] != 1 \
                or struct.unpack('<H', data[22:24])[0] not in (1, 2) or struct.unpack('<H', data[34:36])[0] != 16:
            fail(f'sound {sound["name"]}: {sound["wav"]} is not mono or stereo PCM16')
        else:
            rate = struct.unpack('<I', data[24:28])[0]
            print(f'sound {sound["name"]}: {sound["wav"]} {rate} Hz, like {sound["like"]}')
    # An event names a bundle sound or an installed tag in hex.
    for name in bubbles:
        for entity in bubbles[name].get('entities', []):
            for event in entity.get('events', []):
                text = event.get('sound')
                if text is not None and not re.fullmatch(r'0x[0-9A-Fa-f]{8}', text) and text not in {sound['name'] for sound in sounds}:
                    fail(f'{name} entity {entity["name"]}: sound {text} is not one of the bundle sounds')
    sky = bundle.get('sky')
    if sky is not None:
        path = root / sky['panorama']
        if not path.resolve().is_relative_to(root.resolve()) or not path.is_file():
            fail(f'sky: {sky["panorama"]} is missing or leaves the bundle folder')
        elif path.read_bytes()[:8] != b'\x89PNG\r\n\x1a\n':
            fail(f'sky: {sky["panorama"]} is not a PNG')
        else:
            width, height = struct.unpack('>II', path.read_bytes()[16:24])
            print(f'sky: {sky["panorama"]} {width} by {height}')


def compare(root, other):
    mine = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    theirs = {p.relative_to(other).as_posix() for p in other.rglob('*') if p.is_file()}
    for name in sorted(mine ^ theirs):
        fail(f'compare: {name} exists on one side only')
    for name in sorted(mine & theirs):
        if (root / name).read_bytes() != (other / name).read_bytes():
            fail(f'compare: {name} differs')
    print(f'compare: {len(mine & theirs)} files checked against {other}')


def read_png(path):
    """Width, height and RGBA rows of an 8-bit RGBA PNG with filter 0 rows, as the generator writes."""
    import zlib
    data = path.read_bytes()
    width, height, depth, kind = struct.unpack('>IIBB', data[16:26])
    if depth != 8 or kind != 6:
        fail(f'{path.name}: not 8-bit RGBA')
        return 0, 0, []
    chunks, at = b'', 8
    while at < len(data):
        length, tag = struct.unpack('>I4s', data[at:at + 8])
        if tag == b'IDAT':
            chunks += data[at + 8:at + 8 + length]
        at += 12 + length
    raw = zlib.decompress(chunks)
    stride = 1 + 4 * width
    return width, height, [raw[y * stride + 1:(y + 1) * stride] for y in range(height)]


def check_permissions(folder, grid, label):
    """The grid PNG is square cells; the fixture blocks a 1 by 12 cell wall."""
    width, height, rows = read_png(folder / grid['png'])
    blocked = sum(1 for row in rows for x in range(width) if row[4 * x] >= 128)
    if width != 32 or height != 32 or blocked != 12:
        fail(f'{label}: {width} by {height} cells with {blocked} blocked, not 32 by 32 with 12')
    else:
        print(f'  {label}: {width} by {height} cells of {grid["cell"]} m, {blocked} blocked, walls {grid["height"]} m')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--bundle', type=Path, default=HERE / 'bundle')
    parser.add_argument('--compare', type=Path)
    args = parser.parse_args()
    schema = json.loads(SCHEMA.read_text(encoding='utf-8'))
    label, validate = make_validator(schema)
    print(f'schema: {SCHEMA.name}, validated with {label}')
    check_bundle(args.bundle, validate)
    if args.compare:
        compare(args.bundle, args.compare)
    print('FAILED' if failures else 'OK', f'({len(failures)} failures)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
