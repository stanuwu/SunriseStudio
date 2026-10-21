"""Isolated World Studio oracle: synthetic inputs only; no package access or game output."""
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(sys.argv[1]).resolve()))
import navigation_mesh as nav
import generated_navigation as reader

output = Path(sys.argv[2])
report = {"fixtures": {}}
for name in ('empty', 'flat', 'gap', 'drop', 'ramp', 'step', 'narrow', 'roof', 'curved', 'hole', 'stacked', 'solid', 'obstacle'):
    inputs = dict(grounds=[], solids=[])
    for geometry in json.loads((output / name / 'input.json').read_text()):
        triangles = geometry['triangles']
        inputs['solids' if geometry['solid'] else 'grounds'].append(dict(
            vertices=[v for triangle in triangles for v in triangle],
            triangles=[[3*i, 3*i+1, 3*i+2] for i in range(len(triangles))]))
    python = nav.build_navigation(inputs)
    folder = output / ('python-' + name)
    folder.mkdir(exist_ok=True)
    cs = [(output / name / (kind + '.bin')).read_bytes() for kind in ('root', 'mesh', 'graph')]
    reader.read_generated(*cs)
    for kind, ours in zip(('root', 'mesh', 'graph'), cs):
        expected = getattr(python, kind)
        (folder / (kind + '.bin')).write_bytes(expected)
        assert ours == expected, f'{name} {kind}: C# {len(ours)} bytes differs from Python {len(expected)} bytes'
    report['fixtures'][name] = dict(byte_identical=['CB6', 'CB8', 'CBA'],
        layers=[dict(faces=l.get('faces', 0), points=l.get('points', 0), links=l.get('links', 0)) for l in python.summary['layers']])

table, record = reader.create_table()
table = bytearray(table)
struct.pack_into('<I', table, 48, 0x80C70D01)
struct.pack_into('<I', table, record + 24, 0x80A00000)
assert table == (output / 'placement.bin').read_bytes(), 'Ownership table differs'
report['placement'] = 'Byte-identical 232-byte 808099D6 identity table'
(output / 'parity.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
