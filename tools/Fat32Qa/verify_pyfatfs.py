from pathlib import Path
from pyfatfs.PyFatFS import PyFatFS
import hashlib
import json
import struct

results = []
for image in sorted(Path(__file__).parent.glob('fat32-*.img')):
    with image.open('rb') as f:
        mbr = f.read(512)
        assert mbr[510:] == b'\x55\xaa'
        start = struct.unpack_from('<I', mbr, 454)[0]
        f.seek(start * 512)
        boot = f.read(512)
        assert boot[13] == 64
    payload = bytes(range(256)) * 5001 + b'nomagic data migration independent validation'
    expected = hashlib.sha256(payload).hexdigest()
    with PyFatFS(str(image), offset=start * 512, read_only=False) as fs:
        fs.makedirs('Nintendo/Contents/Carpeta larga de prueba', recreate=True)
        fs.writebytes('Nintendo/Contents/Carpeta larga de prueba/archivo grande.bin', payload)
        fs.writebytes('vacio.txt', b'')
        assert fs.readbytes('Nintendo/Contents/Carpeta larga de prueba/archivo grande.bin') == payload
    with PyFatFS(str(image), offset=start * 512, read_only=True) as fs:
        actual = fs.readbytes('Nintendo/Contents/Carpeta larga de prueba/archivo grande.bin')
        assert hashlib.sha256(actual).hexdigest() == expected
        assert fs.readbytes('vacio.txt') == b''
        names = list(fs.walk.files())
        assert len(names) == 2
    results.append(dict(image=image.name, bytes=image.stat().st_size, offset=start*512, files=names, payload_sha256=expected, status='PASS independent pyfatfs create/reopen/hash'))
print(json.dumps(results, indent=2))
(Path(__file__).parent / 'independent-verification.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
