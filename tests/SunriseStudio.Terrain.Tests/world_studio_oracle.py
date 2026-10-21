"""Parity oracle: compiles one ground spec with World Studio's Python and prints the bytes as JSON.

Usage: python -I world_studio_oracle.py <tools/world-studio directory> <spec.json>
The spec is {columns, rows, spacingTicks, position, heights (row-major, low Y first), dyemap (hex or null),
firstIndex, strokes, heightmapRange}. Nothing is read from a game installation.
"""
import json
import struct
import sys

sys.path.insert(0, sys.argv[1])

import generated_native_terrain as nt  # noqa: E402
import generated_terrain_collision as tc  # noqa: E402
from generated_compound import compile_shapes  # noqa: E402
from ui_backend import ground_ops  # noqa: E402
from verify_havok import Reference  # noqa: E402


def packfile(raw):
    """Data section bytes, local fixups, global fixups and imports of a three-section packfile."""
    ref = Reference(raw)
    start, local, glob, virtual, exports, imports, end = ref.sections[2]
    names = {}
    at = start + imports
    while at + 4 <= start + end:
        offset = struct.unpack_from('<I', raw, at)[0]
        if offset == 0xFFFFFFFF:
            break
        name_end = raw.index(b'\0', at + 4)
        names[offset] = raw[at + 4:name_end].decode('ascii')
        at = (name_end + 1 + 3) & ~3
    links = sorted([s[1], t[1]] for s, t in ref.links.items() if s[0] == 2 and t[0] == 2)
    return dict(data=raw[start:start + local].hex(), local=links, imports={str(k): v for k, v in names.items()})


def main():
    spec = json.load(open(sys.argv[2], encoding='utf-8'))
    columns, rows, ticks = spec['columns'], spec['rows'], spec['spacingTicks']
    heights = spec['heights']
    row = dict(id='oracle', material='retail-oracle', position=spec['position'],
               grid=dict(width=(columns - 1) * ticks / 64, depth=(rows - 1) * ticks / 64,
                         heights=[heights[r * columns:(r + 1) * columns] for r in range(rows)]))
    record = dict(blend_map=dict(texture=spec['dyemap'])) if spec.get('dyemap') else {}
    columns, rows_, points, patches = nt.pieces(row)
    normal_rows = nt.normals(columns, rows_, points)
    ao = nt.occlusion(columns, rows_, points)
    out = dict(points=points, patches=[])
    index = spec['firstIndex']
    for cell, tiles in sorted(patches.items(), key=lambda item: (item[0][1], item[0][0])):
        patch = nt.encode_patch(columns, points, normal_rows, ao, cell, tiles, record, index)
        out['patches'].append(dict(index=index, raw=patch['raw'].hex(), bounds=patch['bounds'].hex(),
                                   positions=patch['positions'].hex(), attributes=patch['attributes'].hex(),
                                   indices=patch['indices'].hex(), alternate=patch['alternate_indices'].hex(),
                                   occlusion=patch['occlusion'].hex(), techniques=patch['techniques'],
                                   dyemaps=[at for at, d in patch['dyemaps'] if d != 'FFFFFFFF']))
        index += 1
    table, resources = nt.create_table(len(out['patches']))
    for (_, at), patch in zip(resources, out['patches']):
        struct.pack_into('<HHI', table, at + 0x10, patch['index'], 0, nt.KEY_TAG)
    out['table'] = bytes(table).hex()
    payloads = []
    out['payloads'], out['carriers'] = [], []
    for cell, tile, vertices, triangles in tc.payloads(row):
        raw, _ = tc.cook_payload(vertices, triangles)
        payloads.append(raw)
        out['payloads'].append(packfile(raw))
        out['carriers'].append(tc.create_carrier(raw)[0].hex())
    compound, _ = compile_shapes(payloads)
    out['compound'] = packfile(tc.retail_imports(compound, 0))
    out['wrapper'] = tc.create_wrapper(1, [], len(payloads), auxiliary=0)[0].hex()
    png, meta = ground_ops.encode(row, 'png')
    out['heightmap'] = dict(raw=ground_ops.encode(row, 'r16')[0].hex(), meta=meta, png=png.hex(),
                            decoded=ground_ops.decode_png(png)[0])
    strokes = []
    for stroke in spec.get('strokes', []):
        result = ground_ops.brush(row, stroke['mode'], stroke['points'], stroke['radius'], stroke['strength'],
                                  stroke.get('curve', 'smooth'), stroke.get('hardness', 0.), stroke.get('target'))
        strokes.append([h for line in result for h in line])
    out['strokes'] = strokes
    if spec.get('heightmapRange'):
        low, high = spec['heightmapRange']
        values, width, height, _ = ground_ops.decode_png(png)
        applied = ground_ops.apply_heightmap(row, values, width, height, low, high)
        out['applied'] = [h for line in applied for h in line]
    json.dump(out, sys.stdout)


main()
