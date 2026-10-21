"""Run the read-only World Studio oracle under python -I on synthetic C# fixtures."""
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(sys.argv[1]).resolve()))
import activity_definitions as definitions
import activity_client as client
import standalone_activity
import localized_strings
from bubble_allocation import name_hash

out = Path(sys.argv[2])
read = lambda name: (out / (name + '.input.bin')).read_bytes()
if '--installed' in sys.argv:
    request = json.loads((out / 'request.json').read_text())
    addition = definitions.Addition(request['template'], request['hash'], request['name'])
    inv, _ = definitions.append(read('definitions'), [addition])
    cli, _ = client.append(read('clients'), [row.definition_hash for row in definitions.decode(read('definitions'))], [addition])
    for name, raw in dict(definitions=inv, clients=cli).items():
        assert raw == (out / (name + '.actual.bin')).read_bytes(), name + ' installed parity differs'
    print('Python oracle: both installed-table appends match byte for byte.')
    sys.exit(0)
addition = definitions.Addition(0x12345678, 0x87654321, 'new_activity')
inv, _ = definitions.append(read('definitions'), [addition])
cli, _ = client.append(read('clients'), [row.definition_hash for row in definitions.decode(read('definitions'))], [addition])
scenario, _ = standalone_activity.rebind_scenario(read('scenario'), 'template', 'new_activity', 'map_copy')
slice_data = standalone_activity.rebind_slice(read('slice'), 'map_copy', 0x80A00005)
container, data = localized_strings.build({name_hash('map_copy'): 'New World é'})
container = localized_strings.encode_container([name_hash('map_copy')], [0x80A00001] * 13)
results = dict(definitions=inv, clients=cli, scenario=scenario, slice=slice_data,
               **{'text-container': container, 'text-data': data})
for name, raw in results.items():
    (out / (name + '.expected.bin')).write_bytes(raw)
    assert raw == (out / (name + '.actual.bin')).read_bytes(), name + ' parity differs'
(out / 'registration-parity.json').write_text(json.dumps({key: hashlib.sha256(raw).hexdigest() for key, raw in results.items()}, indent=2) + '\n')
print('Python oracle: six complete synthetic payloads match byte for byte.')
