"""Writes the testbed's animated entities: one skinned glb per entity, each with named actions.

Plain Python, no Blender. generate_testbed.py lists ENTITIES in bubble_a and calls write_all.
Run alone to rewrite the glb files and the `entities` list of an existing bundle:
  python generate_entities.py --out <bundle dir>

Rigs are given in bundle space (Z up, metres) and written in glTF space (Y up).
"""

import argparse
import json
import math
from pathlib import Path
import struct

FPS = 30


def to_gltf(v):
    """Bundle (x, y, z) with Z up to glTF (x, z, -y) with Y up."""
    return (v[0], v[2], -v[1])


def turn(axis, angle):
    """A glTF quaternion for a turn of `angle` radians about a bundle-space axis."""
    x, y, z = to_gltf(axis)
    s = math.sin(angle / 2)
    return (x * s, y * s, z * s, math.cos(angle / 2))


def box(low, high, bone):
    """24 vertices and 12 triangles of one box, all weighted to `bone`; bundle space in, glTF space out."""
    positions, normals, uvs, indices = [], [], [], []
    axes = ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    for axis in range(3):
        for side in range(2):
            u, v = axes[(axis + 1) % 3], axes[(axis + 2) % 3]
            if side == 0:
                u, v = v, u
            start = len(positions)
            for corner in range(4):
                cu, cv = (1 if corner in (1, 2) else 0), (1 if corner >= 2 else 0)
                point = [0.0, 0.0, 0.0]
                for k in range(3):
                    pick = side if axes[axis][k] else cu if u[k] else cv
                    point[k] = high[k] if pick else low[k]
                positions.append(to_gltf(point))
                normals.append(to_gltf([c * (1 if side else -1) for c in axes[axis]]))
                uvs.append((cu, cv))
            # Bundle space is right-handed like glTF, so the winding carries over.
            indices += [start, start + 1, start + 2, start, start + 2, start + 3]
    return positions, normals, uvs, indices, bone


class Glb:
    """One buffer, with accessors appended in call order."""

    def __init__(self):
        self.data, self.views, self.accessors = bytearray(), [], []

    def add(self, values, fmt, kind, component, target=None, bounds=False):
        while len(self.data) % 4:
            self.data.append(0)
        offset = len(self.data)
        flat = [x for value in values for x in (value if isinstance(value, (tuple, list)) else (value,))]
        self.data += struct.pack('<%d%s' % (len(flat), fmt), *flat)
        view = {'buffer': 0, 'byteOffset': offset, 'byteLength': len(self.data) - offset}
        if target:
            view['target'] = target
        self.views.append(view)
        accessor = {'bufferView': len(self.views) - 1, 'componentType': component, 'count': len(values), 'type': kind}
        if bounds:
            width = len(values[0]) if isinstance(values[0], (tuple, list)) else 1
            columns = [[(value[k] if width > 1 else value) for value in values] for k in range(width)]
            accessor['min'] = [float(min(column)) for column in columns]
            accessor['max'] = [float(max(column)) for column in columns]
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def write(self, path, document):
        while len(self.data) % 4:
            self.data.append(0)
        document['buffers'] = [{'byteLength': len(self.data)}]
        document['bufferViews'] = self.views
        document['accessors'] = self.accessors
        text = json.dumps(document, separators=(',', ':')).encode('ascii')
        text += b' ' * (-len(text) % 4)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(struct.pack('<III', 0x46546C67, 2, 28 + len(text) + len(self.data))
                         + struct.pack('<II', len(text), 0x4E4F534A) + text
                         + struct.pack('<II', len(self.data), 0x004E4942) + bytes(self.data))


def write_entity(path, bones, boxes, clips):
    """bones: (name, parent, bind position), parents first. boxes: (bone, low, high).
    clips: name -> (seconds, {bone: [(fraction, turn or None, offset or None, scale)]}); keys run from 0 to 1.
    The scale is optional; scale keys step, so a part shows or hides between two frames."""
    glb = Glb()
    positions, normals, uvs, indices, joints = [], [], [], [], []
    for bone, low, high in boxes:
        p, n, t, i, b = box(low, high, bone)
        indices += [index + len(positions) for index in i]
        positions += p
        normals += n
        uvs += t
        joints += [(b, 0, 0, 0)] * len(p)
    primitive = {
        'attributes': {
            'POSITION': glb.add(positions, 'f', 'VEC3', 5126, 34962, bounds=True),
            'NORMAL': glb.add(normals, 'f', 'VEC3', 5126, 34962),
            'TEXCOORD_0': glb.add(uvs, 'f', 'VEC2', 5126, 34962),
            'JOINTS_0': glb.add(joints, 'B', 'VEC4', 5121, 34962),
            'WEIGHTS_0': glb.add([(1.0, 0.0, 0.0, 0.0)] * len(positions), 'f', 'VEC4', 5126, 34962),
        },
        'indices': glb.add(indices, 'H', 'SCALAR', 5123, 34963),
        'mode': 4,
    }
    nodes, locals_ = [], []
    for index, (name, parent, position) in enumerate(bones):
        local = [a - b for a, b in zip(position, bones[parent][2])] if parent >= 0 else list(position)
        locals_.append(local)
        nodes.append({'name': name, 'translation': list(to_gltf(local))})
    for index, (name, parent, position) in enumerate(bones):
        if parent >= 0:
            nodes[parent].setdefault('children', []).append(index)
    # Column-major inverse binds: the joints bind unrotated, so each is a translation.
    inverse = []
    for name, parent, position in bones:
        x, y, z = to_gltf(position)
        inverse.append((1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -x, -y, -z, 1))
    skin = {'joints': list(range(len(bones))), 'inverseBindMatrices': glb.add(inverse, 'f', 'MAT4', 5126)}
    nodes.append({'name': 'mesh', 'mesh': 0, 'skin': 0})
    animations = []
    for clip, (seconds, tracks) in clips.items():
        samplers, channels = [], []
        for bone, keys in tracks.items():
            index = [name for name, _, _ in bones].index(bone)
            times = glb.add([key[0] * seconds for key in keys], 'f', 'SCALAR', 5126, bounds=True)
            if any(key[1] is not None for key in keys):
                values = [turn(*key[1]) if key[1] else (0.0, 0.0, 0.0, 1.0) for key in keys]
                samplers.append({'input': times, 'output': glb.add(values, 'f', 'VEC4', 5126), 'interpolation': 'LINEAR'})
                channels.append({'sampler': len(samplers) - 1, 'target': {'node': index, 'path': 'rotation'}})
            if any(key[2] is not None for key in keys):
                values = [to_gltf([a + b for a, b in zip(locals_[index], key[2] or (0, 0, 0))]) for key in keys]
                samplers.append({'input': times, 'output': glb.add(values, 'f', 'VEC3', 5126), 'interpolation': 'LINEAR'})
                channels.append({'sampler': len(samplers) - 1, 'target': {'node': index, 'path': 'translation'}})
            if any(len(key) > 3 for key in keys):
                values = [(key[3],) * 3 if len(key) > 3 else (1.0, 1.0, 1.0) for key in keys]
                samplers.append({'input': times, 'output': glb.add(values, 'f', 'VEC3', 5126), 'interpolation': 'STEP'})
                channels.append({'sampler': len(samplers) - 1, 'target': {'node': index, 'path': 'scale'}})
        animations.append({'name': clip, 'samplers': samplers, 'channels': channels})
    roots = [index for index, (_, parent, _) in enumerate(bones) if parent < 0]
    glb.write(path, {
        'asset': {'version': '2.0', 'generator': 'Sunrise Studio testbed entities'},
        'scene': 0, 'scenes': [{'nodes': roots + [len(bones)]}],
        'nodes': nodes, 'meshes': [{'name': path.stem, 'primitives': [primitive]}], 'skins': [skin],
        'animations': animations,
    })


X, Y, Z = (1, 0, 0), (0, 1, 0), (0, 0, 1)


def swing(axis, angle):
    """Out and back: 0, +angle, 0, -angle, 0 over the clip."""
    return [(0, (axis, 0), None), (0.25, (axis, angle), None), (0.5, (axis, 0), None), (0.75, (axis, -angle), None), (1, (axis, 0), None)]


def bob(height):
    return [(0, None, (0, 0, 0)), (0.5, None, (0, 0, height)), (1, None, (0, 0, 0))]


# A bone scaled this small hides its part; a clip has no visibility channel yet.
HIDDEN = 0.001


def shown(*spans):
    """Scale keys that hide a part except inside the (start, end) fractions given."""
    keys, last = [], 0.0
    for start, end in spans:
        if start > last:
            keys.append((last, None, None, HIDDEN))
        keys.append((start, None, None, 1.0))
        last = end
    if last < 1:
        keys.append((last, None, None, HIDDEN))
    keys.append((1, None, None, keys[-1][3]))
    return keys


def creature_clips(body, limbs, lunge):
    """The seven creature clips over a body bone and its limb bones; `lunge` is the attack direction."""
    alternate = {limb: swing(X, 0.5 if index % 2 == 0 else -0.5) for index, limb in enumerate(limbs)}
    fast = {limb: swing(X, 0.9 if index % 2 == 0 else -0.9) for index, limb in enumerate(limbs)}
    return {
        'idle': (2.0, {body: bob(0.05)}),
        'walk': (1.0, {body: bob(0.04), **alternate}),
        'run': (0.5, {body: bob(0.08), **fast}),
        'attack_contact': (0.6, {body: [(0, None, (0, 0, 0)), (0.4, None, tuple(0.5 * c for c in lunge)), (1, None, (0, 0, 0))]}),
        'attack_ranged': (0.8, {body: [(0, (X, 0), None), (0.3, (X, -0.35), None), (0.6, (X, 0.2), None), (1, (X, 0), None)]}),
        'hit': (0.3, {body: [(0, None, (0, 0, 0)), (0.3, None, tuple(-0.2 * c for c in lunge)), (1, None, (0, 0, 0))]}),
        'death': (1.0, {body: [(0, (Y, 0), None), (0.6, (Y, 1.4), None), (1, (Y, 1.5), None)]}),
    }


def npc(path):
    # The blink swaps two eye parts on the face, each on its own bone: open eyes and closed eyes.
    bones = [('root', -1, (0, 0, 0)), ('body', 0, (0, 0, 0.8)), ('head', 1, (0, 0, 1.5)),
             ('arm_l', 1, (-0.3, 0, 1.4)), ('arm_r', 1, (0.3, 0, 1.4)),
             ('eyes_open', 2, (0, -0.18, 1.72)), ('eyes_closed', 2, (0, -0.18, 1.72))]
    boxes = [(0, (-0.3, -0.3, 0), (0.3, 0.3, 0.05)), (1, (-0.25, -0.15, 0.05), (0.25, 0.15, 1.45)),
             (2, (-0.18, -0.18, 1.5), (0.18, 0.18, 1.86)),
             (3, (-0.42, -0.08, 0.8), (-0.28, 0.08, 1.42)), (4, (0.28, -0.08, 0.8), (0.42, 0.08, 1.42)),
             (5, (-0.14, -0.24, 1.66), (-0.04, -0.18, 1.80)), (5, (0.04, -0.24, 1.66), (0.14, -0.18, 1.80)),
             (6, (-0.15, -0.24, 1.715), (-0.03, -0.18, 1.735)), (6, (0.03, -0.24, 1.715), (0.15, -0.18, 1.735))]
    blink = {'eyes_open': shown((0, 0.45), (0.55, 1)), 'eyes_closed': shown((0.45, 0.55))}
    clips = {
        'idle': (2.0, {'body': bob(0.02), 'head': swing(Z, 0.25), **blink}),
        'walk': (1.0, {'body': bob(0.04), 'arm_l': swing(X, 0.6), 'arm_r': swing(X, -0.6), **blink}),
        'talk': (1.0, {'head': swing(X, 0.2), 'arm_r': [(0, (X, 0), None), (0.5, (X, -1.2), None), (1, (X, 0), None)], **blink}),
    }
    write_entity(path, bones, boxes, clips)


def quadruped(path):
    bones = [('root', -1, (0, 0, 0)), ('body', 0, (0, 0, 0.6)), ('head', 1, (0, 0.7, 0.75)),
             ('leg_fl', 1, (-0.25, 0.5, 0.55)), ('leg_fr', 1, (0.25, 0.5, 0.55)),
             ('leg_bl', 1, (-0.25, -0.5, 0.55)), ('leg_br', 1, (0.25, -0.5, 0.55))]
    boxes = [(0, (-0.1, -0.1, 0), (0.1, 0.1, 0.02)), (1, (-0.3, -0.65, 0.45), (0.3, 0.65, 0.85)),
             (2, (-0.2, 0.65, 0.6), (0.2, 1.05, 0.95)),
             (3, (-0.32, 0.42, 0), (-0.18, 0.58, 0.55)), (4, (0.18, 0.42, 0), (0.32, 0.58, 0.55)),
             (5, (-0.32, -0.58, 0), (-0.18, -0.42, 0.55)), (6, (0.18, -0.58, 0), (0.32, -0.42, 0.55))]
    write_entity(path, bones, boxes, creature_clips('body', ['leg_fl', 'leg_fr', 'leg_br', 'leg_bl'], Y))


def floater(path):
    bones = [('root', -1, (0, 0, 0)), ('body', 0, (0, 0, 1.2)), ('fin_l', 1, (-0.4, 0, 1.2)), ('fin_r', 1, (0.4, 0, 1.2))]
    boxes = [(0, (-0.1, -0.1, 0), (0.1, 0.1, 0.02)), (1, (-0.4, -0.4, 0.8), (0.4, 0.4, 1.6)),
             (2, (-1.0, -0.2, 1.15), (-0.4, 0.2, 1.25)), (3, (0.4, -0.2, 1.15), (1.0, 0.2, 1.25))]
    clips = creature_clips('body', [], Y)
    flap = {'fin_l': swing(Y, 0.5), 'fin_r': swing(Y, -0.5)}
    for name in ('idle', 'walk', 'run'):
        clips[name] = (clips[name][0], {**clips[name][1], **flap})
    write_entity(path, bones, boxes, clips)


def door(path, vanish=False):
    # Two panels hinged at the frame's outer edges; each swings 90 degrees outward, or hides when `vanish`.
    bones = [('root', -1, (0, 0, 0)), ('panel_l', 0, (-1.0, 0, 0)), ('panel_r', 0, (1.0, 0, 0))]
    boxes = [(0, (-1.15, -0.1, 0), (-1.0, 0.1, 2.2)), (0, (1.0, -0.1, 0), (1.15, 0.1, 2.2)), (0, (-1.15, -0.1, 2.2), (1.15, 0.1, 2.35)),
             (1, (-1.0, -0.04, 0), (0, 0.04, 2.2)), (2, (0, -0.04, 0), (1.0, 0.04, 2.2))]
    quarter = math.pi / 2
    clips = {
        'open': (1.5, {'panel_l': [(0, (Z, 0), None), (0.7, (Z, -quarter), None), (1, (Z, -quarter), None)],
                       'panel_r': [(0, (Z, 0), None), (0.7, (Z, quarter), None), (1, (Z, quarter), None)]}),
        'close': (1.5, {'panel_l': [(0, (Z, -quarter), None), (0.7, (Z, 0), None), (1, (Z, 0), None)],
                        'panel_r': [(0, (Z, quarter), None), (0.7, (Z, 0), None), (1, (Z, 0), None)]}),
    }
    if vanish:
        clips = {
            'open': (1.5, {'panel_l': shown((0, 0.3)), 'panel_r': shown((0, 0.6))}),
            'close': (1.5, {'panel_l': shown((0.3, 1)), 'panel_r': shown((0.6, 1))}),
        }
    write_entity(path, bones, boxes, clips)


def door_vanish(path):
    door(path, vanish=True)


# The `entities` list of bubble_a; positions are in bundle space.
ENTITIES = [
    {'name': 'npc', 'glb': 'npc.glb', 'position': [-4, 0, 0], 'rotation': [0, 0, 0, 1], 'scale': 1, 'plays': 'idle'},
    {'name': 'quadruped', 'glb': 'quadruped.glb', 'position': [-1.5, 3, 0], 'rotation': [0, 0, 0, 1], 'scale': 1, 'plays': 'walk'},
    {'name': 'floater', 'glb': 'floater.glb', 'position': [1.5, 3, 0], 'rotation': [0, 0, 0, 1], 'scale': 1, 'plays': 'idle'},
    {'name': 'door', 'glb': 'door.glb', 'position': [0, 7, 0], 'rotation': [0, 0, 0, 1], 'scale': 1, 'plays': 'open'},
    # The same door, but its panels hide one after the other instead of swinging.
    {'name': 'door_vanish', 'glb': 'door_vanish.glb', 'position': [4, 7, 0], 'rotation': [0, 0, 0, 1], 'scale': 1, 'plays': 'open'},
    # The same door as a device: a script opens it through the source activity's slot d_lure.
    # Its open action posts the bundle's own sound as the panels start, and an installed door sound as they stop.
    {'name': 'door_device', 'glb': 'door.glb', 'position': [-4, 7, 0], 'rotation': [0, 0, 0, 1], 'scale': 1,
     'device': {'slot': 'd_lure', 'position': 'open'},
     'events': [{'action': 'open', 'frame': 2, 'sound': 'door_open'}, {'action': 'open', 'frame': 30, 'sound': '0x80F5D870'}]},
]
WRITERS = {'npc.glb': npc, 'quadruped.glb': quadruped, 'floater.glb': floater, 'door.glb': door, 'door_vanish.glb': door_vanish}
# The `volumes` list of bubble_a: a stock trigger volume moved in front of the device door, and a
# second trigger whose volume becomes a new turned box.
VOLUMES = [{'trigger': 'pt_left_town', 'position': [-4, 5, -1]},
           {'trigger': 'pt_lure_approach', 'position': [4, 5, -1], 'size': [3, 3, 4], 'yaw': 30}]
# The `sounds` list of bundle.json: two wavs that vary the way an installed door sound does, heard
# out to 25 m.
SOUNDS = [{'name': 'door_open', 'wav': 'sounds/door_open.wav', 'like': '0x80F5D86D',
           'variations': ['sounds/door_open_2.wav'], 'max_distance': 25}]
SOUND_RATE = 48000


def door_open_wave(start=660):
    # A falling two-tone sweep from `start` Hz, 0.4 s, mono PCM16.
    samples = bytearray()
    count = int(SOUND_RATE * 0.4)
    for index in range(count):
        time = index / SOUND_RATE
        fade = (1 - index / count) ** 2
        value = math.sin(2 * math.pi * (start - 500 * time) * time) + 0.5 * math.sin(2 * math.pi * 110 * time)
        samples += struct.pack('<h', int(9000 * fade * value))
    return bytes(samples)


def write_sounds(bundle_folder):
    # The variation starts its sweep higher, so the two are told apart by ear.
    for name, start in ((SOUNDS[0]['wav'], 660), (SOUNDS[0]['variations'][0], 880)):
        path = Path(bundle_folder) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        data = door_open_wave(start)
        header = b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 1, 1, SOUND_RATE, SOUND_RATE * 2, 2, 16)
        path.write_bytes(header + b'data' + struct.pack('<I', len(data)) + data)


def write_all(bubble_folder):
    for glb in sorted({item['glb'] for item in ENTITIES}):
        WRITERS[glb](Path(bubble_folder) / glb)
    write_sounds(Path(bubble_folder).parent)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', type=Path, required=True, help='the bundle folder that holds bubble_a')
    out = parser.parse_args().out.resolve()
    write_all(out / 'bubble_a')
    path = out / 'bubble_a' / 'bubble.json'
    bubble = json.loads(path.read_text('ascii'))
    bubble['entities'] = ENTITIES
    bubble['volumes'] = VOLUMES
    path.write_bytes((json.dumps(bubble, indent=2) + '\n').encode('ascii'))
    # The sounds sit before the sky, as generate_testbed.py writes them.
    path = out / 'bundle.json'
    bundle = json.loads(path.read_text('ascii'))
    sky = bundle.pop('sky', None)
    bundle['sounds'] = SOUNDS
    if sky is not None:
        bundle['sky'] = sky
    path.write_bytes((json.dumps(bundle, indent=2) + '\n').encode('ascii'))
    print(f'testbed entities: wrote {len(WRITERS)} glb files and {len(ENTITIES)} entities in {out / "bubble_a"}')


if __name__ == '__main__':
    main()
