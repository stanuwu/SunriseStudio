"""Shader programs: the add-on's lifted nodes give the values of one node per program instruction.

Blender evaluates both builds as geometry nodes on the same random inputs; every output lane and sample coordinate
must match. Arguments: the add-on's bundle.py, then bundle manifests.
"""

import json
from pathlib import Path
import runpy
import sys

import bpy
import numpy as np

POINTS = 256
# Float tolerance for reordered sums and fused multiply-adds.
RELATIVE, ABSOLUTE = 1e-3, 1e-4
# Program operations that one Math node computes.
LITERAL = {'add': 'ADD', 'mul': 'MULTIPLY', 'div': 'DIVIDE', 'mad': 'MULTIPLY_ADD', 'min': 'MINIMUM', 'max': 'MAXIMUM',
           'abs': 'ABSOLUTE', 'sqrt': 'SQRT', 'rsq': 'INVERSE_SQRT', 'frc': 'FRACT', 'round_ni': 'FLOOR',
           'round_ne': 'ROUND', 'round_pi': 'CEIL', 'round_z': 'TRUNC', 'sin': 'SINE', 'cos': 'COSINE',
           'lt': 'LESS_THAN'}


def programs(manifests):
    """Each distinct material and vertex motion program in the bundles, with a name."""
    seen = {}
    for manifest in manifests:
        document = json.loads(manifest.read_text(encoding='utf-8'))
        for material in document.get('Materials') or []:
            preview = material.get('Preview') or {}
            for variant in [preview, *(preview.get('BlendVariants') or {}).values()]:
                program = (variant or {}).get('Program')
                if program:
                    name = material.get('Name') or f'{int(material.get("TagHash") or 0):08X}'
                    seen.setdefault(json.dumps(program, sort_keys=True), (name, program))
        paths = {mesh['Path'] for mesh in document.get('Meshes') or [] if mesh.get('Path')}
        for path in sorted(paths):
            motion = json.loads((manifest.parent / path).read_text(encoding='utf-8')).get('VertexMotion') or {}
            if motion.get('Profile') == 'VertexProgram':
                seen.setdefault(json.dumps(motion['Program'], sort_keys=True), ('motion ' + path, motion['Program']))
    return list(seen.values())


def ancestors(nodes, index, operation):
    """Program nodes of one operation that index reads, directly or not."""
    found, pending, seen = set(), [index], set()
    while pending:
        value = pending.pop()
        if value in seen:
            continue
        seen.add(value)
        if nodes[value]['Operation'] == operation:
            found.add(value)
        pending.extend(nodes[value]['Inputs'])
    return found


def check(program, rng):
    """Builds and evaluates both versions of one program; returns (literal nodes, lifted nodes, errors, worst)."""
    nodes = program['Nodes']
    group = bpy.data.node_groups.new('Program check', 'GeometryNodeTree')
    group.interface.new_socket(name='Geometry', in_out='INPUT', socket_type='NodeSocketGeometry')
    group.interface.new_socket(name='Geometry', in_out='OUTPUT', socket_type='NodeSocketGeometry')
    geometry = group.nodes.new('NodeGroupInput').outputs[0]
    target = group.nodes.new('NodeGroupOutput')
    counts = {'literal': 0, 'lifted': 0}
    inputs, attributes = {}, {}

    def connect(value, socket):
        if isinstance(value, (int, float, tuple, list)):
            socket.default_value = value
        else:
            group.links.new(value, socket)

    def attribute(name, values):
        if name not in attributes:
            source = group.nodes.new('GeometryNodeInputNamedAttribute')
            source.data_type = 'FLOAT'
            source.inputs['Name'].default_value = name
            attributes[name] = source.outputs['Attribute']
            inputs[name] = values
        return attributes[name]

    def leaf(index):
        item = nodes[index]
        if item['Operation'] == 'constant':
            return float(item['Value'])
        low, high = (0., 1.) if item['Operation'] == 'sample' else (-2., 2.)
        return attribute(f'L{index}', rng.uniform(low, high, POINTS))

    def math_node(operation, *values):
        counts['literal'] += 1
        value = group.nodes.new('ShaderNodeMath')
        value.operation = operation
        for source, socket in zip(values, value.inputs):
            connect(source, socket)
        return value.outputs[0]

    # One node per instruction, as the program reads.
    literal = {}
    for index, item in enumerate(nodes):
        operation, values = item['Operation'], [literal.get(value) for value in item['Inputs']]
        if operation in LIFT.LEAVES:
            literal[index] = leaf(index)
        elif operation in LITERAL:
            literal[index] = math_node(LITERAL[operation], *values)
        elif operation == 'neg':
            literal[index] = math_node('MULTIPLY', values[0], -1)
        elif operation == 'exp':
            literal[index] = math_node('POWER', 2, values[0])
        elif operation == 'log':
            literal[index] = math_node('MULTIPLY_ADD', math_node('LESS_THAN', values[0], LIFT.LOG_FLOOR), LIFT.LOG_ZERO,
                                       math_node('LOGARITHM', values[0], 2))
        elif operation in ('eq', 'ne'):
            same = math_node('COMPARE', values[0], values[1], 0)
            literal[index] = math_node('SUBTRACT', 1, same) if operation == 'ne' else same
        elif operation == 'ge':
            literal[index] = math_node('SUBTRACT', 1, math_node('LESS_THAN', values[0], values[1]))
        elif operation == 'movc':
            condition = values[0] if nodes[item['Inputs'][0]]['Operation'] in ('lt', 'ge', 'eq', 'ne') \
                else math_node('GREATER_THAN', math_node('ABSOLUTE', values[0]), 0)
            literal[index] = math_node('MULTIPLY_ADD', condition, math_node('SUBTRACT', values[1], values[2]), values[2])
        else:
            raise ValueError('no literal build for ' + operation)

    # The add-on's lifted build; a vector input is its lanes combined, unused lanes read noise.
    def lifted_node(kind, label):
        counts['lifted'] += 1
        value = group.nodes.new(kind)
        value.label = label
        return value

    def leaf_vector(lanes):
        combine = group.nodes.new('ShaderNodeCombineXYZ')
        for lane in range(3):
            connect(leaf(lanes[lane]) if lane < len(lanes) else attribute('noise', rng.uniform(-50, 50, POINTS)),
                    combine.inputs[lane])
        return combine.outputs[0]

    lift = LIFT.Lift(program)
    builder = addon['_ProgramNodes'](lift, lifted_node, connect, leaf, leaf_vector)
    compared = []
    for role, indices in program['Outputs'].items():
        vector = lift.as_vector(indices) if len(indices) == 3 else None
        for lane, index in enumerate(indices):
            compared.append((role + 'xyzw'[lane], index, vector, lane))
    samples = {}
    for item in nodes:
        if item['Operation'] == 'sample':
            samples.setdefault((item['Slot'], item.get('SamplerSlot'), *item['Inputs']), item['Inputs'][:2])
    for key, coordinates in samples.items():
        vector = lift.as_vector(coordinates)
        for lane, index in enumerate(coordinates):
            compared.append((f't{key[0]} uv' + 'uv'[lane], index, vector, lane))

    stores = []

    def store(name, value, kind='FLOAT'):
        nonlocal geometry
        node = group.nodes.new('GeometryNodeStoreNamedAttribute')
        node.data_type, node.domain = kind, 'POINT'
        node.inputs['Name'].default_value = name
        connect(geometry, node.inputs['Geometry'])
        connect(value, node.inputs['Value'])
        geometry = node.outputs['Geometry']
        stores.append(name)

    sockets = {}
    for number, (_, index, vector, lane) in enumerate(compared):
        store(f'R{number}', literal[index])
        if vector is None:
            store(f'N{number}', builder.build(lift.scalar(index)))
        elif vector not in sockets:
            sockets[vector] = f'V{number}'
            store(sockets[vector], builder.build(vector), 'FLOAT_VECTOR')
    lifted = counts['lifted']
    # Direct3D log of a negative is NaN, so an output that reads one has no native answer there.
    logs = {index for index, item in enumerate(nodes) if item['Operation'] == 'log'}
    for index in logs:
        store(f'P{index}', math_node('LESS_THAN', literal[nodes[index]['Inputs'][0]], 0))
    connect(geometry, target.inputs[0])

    mesh = bpy.data.meshes.new('Program points')
    mesh.from_pydata([(0, 0, 0)] * POINTS, [], [])
    for name, values in inputs.items():
        mesh.attributes.new(name, 'FLOAT', 'POINT').data.foreach_set('value', values.astype(np.float32))
    obj = bpy.data.objects.new('Program points', mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.modifiers.new('Program', 'NODES').node_group = group
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get()).data

    def read(name, width=1):
        values = np.empty(POINTS * width, dtype=np.float32)
        evaluated.attributes[name].data.foreach_get('vector' if width == 3 else 'value', values)
        return values.reshape(POINTS, width) if width == 3 else values

    errors, worst = [], 0.
    for number, (name, index, vector, lane) in enumerate(compared):
        expected = read(f'R{number}').astype(np.float64)
        actual = (read(sockets[vector], 3)[:, lane] if vector is not None else read(f'N{number}')).astype(np.float64)
        poisoned = np.zeros(POINTS, bool)
        for value in ancestors(nodes, index, 'log'):
            poisoned |= read(f'P{value}') > 0
        valid = np.isfinite(expected) & (np.abs(expected) < 1e6) & ~poisoned
        difference = np.abs(actual - expected)
        bad = valid & ~(difference <= ABSOLUTE + RELATIVE * np.abs(expected))
        if valid.any():
            worst = max(worst, float((difference[valid] / (ABSOLUTE + RELATIVE * np.abs(expected[valid]))).max()))
        if bad.any():
            point = int(np.argmax(bad))
            errors.append(f'{name}: literal {expected[point]:.6g}, lifted {actual[point]:.6g} at {bad.sum()}/{POINTS} points')
    bpy.data.objects.remove(obj)
    bpy.data.meshes.remove(mesh)
    bpy.data.node_groups.remove(group)
    return counts['literal'], lifted, errors, worst


arguments = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
addon = runpy.run_path(str(Path(arguments[0]).absolute()), run_name='sunrise_bundle_addon')
LIFT = addon['shader_lift']
found = programs([Path(value).absolute() for value in arguments[1:]])
assert found, 'The bundles have no shader programs.'
# The add-on refuses a program with an operation it does not know, such as one from a newer export.
KNOWN = LIFT.LEAVES | set(LITERAL) | {'neg', 'exp', 'log', 'eq', 'ne', 'ge', 'movc'}
known = [(name, program) for name, program in found if all(item['Operation'] in KNOWN for item in program['Nodes'])]
print(f'Shader programs with an unknown operation, skipped: {len(found) - len(known)}')
found = known
generator = np.random.default_rng(7)
failures, literal_total, lifted_total, worst_total = [], 0, 0, 0.
for name, program in found:
    try:
        literal_count, lifted_count, errors, worst = check(program, generator)
    except Exception as error:
        failures.append(f'{name}: {error!r}')
        continue
    literal_total, lifted_total, worst_total = literal_total + literal_count, lifted_total + lifted_count, max(worst_total, worst)
    failures.extend(f'{name}: {error}' for error in errors[:3])
print(f'Shader programs: {len(found)}, literal nodes {literal_total}, lifted nodes {lifted_total}, '
      f'worst difference {worst_total:.3f} of the tolerance')
assert not failures, '\n'.join(failures[:40])
