"""World Studio oracle for Studio's crossing compiler: synthetic fixture bytes only, no package or game access.

Usage: python -I -B parity.py <World Studio folder> <case folder> <case count>
Each case holds the installed originals, Studio's outputs (out-*.bin) and spec.json. The original World Studio
modules recompute every output; the script fails on the first byte that differs. Scenarios named in spec.json follow
the definition through scenario_model.rebind_masks; studio_only outputs are Studio's alone (a scenario naming part of
the map, which rebind_masks does not take).
"""
import json
import math
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(sys.argv[1]).resolve()))
import connection_export
import generated_crossing
import inherited_zones
import map_definition
import map_visibility
import scenario_model
from native_connections import CellEdit, Connection, write_connections
from native_visibility import Payload

root, count = Path(sys.argv[2]), int(sys.argv[3])
report = {}

rows = json.loads((root / 'distances.json').read_text())
mismatch = [r for r in rows if struct.unpack('<q', struct.pack('<d', math.dist(r['p'], r['q'])))[0] & 0xFFFFFFFFFFFFFFFF
            != int(r['bits'], 16)]
assert not mismatch, f'{len(mismatch)} of {len(rows)} distances differ from math.dist, first {mismatch[0]}'
report['distances'] = f'{len(rows)} bit-identical to math.dist'


def shape(box):
    return dict(type='box', center=list(box['center']), half_extents=list(box['half']), rotation_xyzw=[0, 0, 0, 1])


def first_diff(a, b):
    return next((i for i, (x, y) in enumerate(zip(a, b)) if x != y), min(len(a), len(b)))


for index in range(count):
    case = root / f'case-{index}'
    spec = json.loads((case / 'spec.json').read_text())
    read = lambda tag: (case / f'{tag}.bin').read_bytes()
    expected = {}
    definition = read(spec['definition'])
    original = map_definition.decode(definition)
    by_name = {f'{b.name_hash:08X}': b for b in original.bubbles}
    connections = sorted(spec['connections'], key=lambda c: c['Id'])
    links = {}
    for c in connections:
        decoded = map_definition.decode(definition)
        a, b = (decoded.bubbles[by_name[c[k]].root_slot] for k in ('source', 'destination'))
        shared = sorted(set(a.links_one) & set(b.links_one))
        if shared:
            links[c['Id']] = decoded.keys[shared[0]].value
            continue
        _, name, key = connection_export.link_identity(spec['space'], c['source'], c['destination'])
        definition = map_definition.append_connection(definition, a.root_slot, b.root_slot, name, key)
        links[c['Id']] = key
    final = map_definition.decode(definition)
    expected[spec['definition']] = definition
    for bubble in spec['bubbles']:
        before, after = original.bubbles[bubble['slot']], final.bubbles[bubble['slot']]
        if before.mask != after.mask:
            expected[bubble['tag']] = map_visibility.rebind_bubble(read(bubble['tag']), before, after)
    for tag in spec.get('scenarios', []):
        expected[tag] = scenario_model.rebind_masks(read(tag), original, final)
    names = sorted({c[k] for c in connections for k in ('source', 'destination')})
    for name in names:
        slot = by_name[name].root_slot
        own = final.bubbles[slot]
        self_key = final.keys[own.global_index].value
        uses = [c for c in connections if name in (c['source'], c['destination'])]
        layers = [l for l in spec['layers'] if l['Slot'] == slot]

        def exits(c):
            mine = c['source_exit'] if c['source'] == name else c['destination_exit']
            other = c['destination_exit'] if c['source'] == name else c['source_exit']
            return shape(mine), shape(other)

        def typed(c):
            other = c['destination'] if c['source'] == name else c['source']
            return Connection(links[c['Id']], final.keys[final.bubbles[by_name[other].root_slot].global_index].value)

        if uses[0]['profile'] == 'Retail':
            payload = Payload(read(layers[0]['payload']))
            mine, other = exits(uses[0])
            built = generated_crossing.compile_retail_candidate(final, self_key, typed(uses[0]), payload.outer.bounds, mine, other)
            for layer in layers:
                expected[layer['payload']], expected[layer['cells']] = built.partition, built.cells
            continue
        for layer in layers:
            raw = read(layer['cells'])
            if uses[0]['policy'] == 'Deactivate':
                identity = {key.value: key.value for key in final.keys}
                raw, _ = inherited_zones.apply(raw, final, final, 'side', [('side', slot)], 'deactivate', identity, {'side': self_key})
            payload = Payload(read(layer['payload']))
            typed_rows, edits = [], []
            for c in uses:
                typed_rows.append(typed(c))
                mine, other = exits(c)
                own_cells, _ = connection_export.select_cells(payload, connection_export.lattice(mine)['points'])
                landing, _ = connection_export.select_cells(payload, connection_export.lattice(other)['points'])
                edits += [CellEdit(cell, len(typed_rows) - 1, 'immediate') for cell in own_cells]
                edits += [CellEdit(cell, len(typed_rows) - 1, 'timed', generated_crossing.LANDING_RAMP, generated_crossing.LANDING_STATE)
                          for cell in sorted(set(landing) - set(own_cells))]
            expected[layer['cells']] = write_connections(raw, final, typed_rows, sorted(edits, key=lambda e: e.cell), self_key)
    original_bytes = {tag: read(tag) for tag in expected}
    expected = {tag: data for tag, data in expected.items() if data != original_bytes[tag]}
    studio = sorted(set(spec['outputs']) - set(spec.get('studio_only', [])))
    assert sorted(expected) == studio, f'case {index}: Python wrote {sorted(expected)}, Studio {studio}'
    summary = {}
    for tag, data in sorted(expected.items()):
        ours = (case / f'out-{tag}.bin').read_bytes()
        assert ours == bytes(data), f'case {index} {tag}: {len(ours)} bytes differ from Python {len(data)} at byte {first_diff(ours, data)}'
        summary[tag] = f'{len(ours)} bytes identical'
    report[f'case-{index}'] = dict(connections=[f"{c['Id']} {c['profile']} {c['policy']}" for c in connections], outputs=summary)

(root / 'parity.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
