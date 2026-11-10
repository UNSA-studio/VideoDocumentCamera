"""Audit hand-written Media Foundation identifiers against Microsoft's SDK headers.
Use --update to refresh the checked-in factual manifest from the official headers.
Normal invocation is offline and suitable for CI. No project files are modified.
"""
import argparse, hashlib, json, re, urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / 'tests' / 'Diagnostics.Tests' / 'microsoft-sdk-identifiers.json'
URL = 'https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/'
HEADERS = ['mfapi.h', 'mfobjects.h', 'mfreadwrite.h', 'mfidl.h']

def fetch(name):
    data = urllib.request.urlopen(URL + name, timeout=25).read()
    return name, data.decode('utf-8', errors='replace'), hashlib.sha256(data).hexdigest()

def identifier_map(headers):
    result = {}
    for _, text, _ in headers:
        for match in re.finditer(r'(?:DEFINE_GUID|EXTERN_GUID)\(\s*(\w+)\s*,(.*?)\);', text, re.S):
            values = [int(x, 16) for x in re.findall(r'0x([0-9a-fA-F]+)', match[2])]
            if len(values) == 11:
                a,b,c,*tail=values
                result[match[1]] = f'{a:08x}-{b:04x}-{c:04x}-' + ''.join(f'{n:02x}' for n in tail[:2]) + '-' + ''.join(f'{n:02x}' for n in tail[2:])
        for m in re.finditer(r'MIDL_INTERFACE\("([0-9a-fA-F-]+)"\)\s*(\w+)\s*:\s*public', text):
            result[m[2]] = m[1].lower()
    return result

parser = argparse.ArgumentParser()
parser.add_argument('--update', action='store_true')
args=parser.parse_args()
if args.update:
    headers = list(ThreadPoolExecutor(max_workers=4).map(fetch, HEADERS))
    all_ids = identifier_map(headers)
    required = set()
    for file in ['RecordingService.cs', 'CameraService.cs']:
        text = (ROOT/'src/VideoPresenter.App/Services'/file).read_text(encoding='utf-8-sig')
        required.update(m[1] for m in re.finditer(r'Guid\s+(\w+)\s*=\s*new\("([0-9a-fA-F-]+)"\)', text) if m[1] in all_ids)
        required.update(m[2] for m in re.finditer(r'Guid\("([0-9a-fA-F-]+)"\).*?\binterface\s+(\w+)', text, re.S) if m[2] in all_ids)
    manifest={'sources':{name:{'url':URL+name, 'sha256':sha} for name,_,sha in headers},
              'identifiers':{key:all_ids[key] for key in sorted(required)}}
    MANIFEST.write_text(json.dumps(manifest, indent=2)+'\n')
else:
    manifest=json.loads(MANIFEST.read_text(encoding='utf-8-sig'))
identifiers=manifest['identifiers']
errors=[]
checked=0
for file in ['RecordingService.cs', 'CameraService.cs']:
    text=(ROOT/'src/VideoPresenter.App/Services'/file).read_text(encoding='utf-8-sig')
    declared=[(m[1],m[2]) for m in re.finditer(r'Guid\s+(\w+)\s*=\s*new\("([0-9a-fA-F-]+)"\)',text)]
    declared += [(m[2],m[1]) for m in re.finditer(r'Guid\("([0-9a-fA-F-]+)"\).*?\binterface\s+(\w+)',text,re.S)]
    for name,value in declared:
        if name not in identifiers:
            continue
        checked+=1
        if value.lower()!=identifiers[name]:
            errors.append(f'{file}: {name}: code={value}; Microsoft SDK={identifiers[name]}')
for error in errors: print('FAIL',error)
print(f'Checked {checked} declared identifiers; mismatches={len(errors)}. This does not verify COM vtables or execution.')
raise SystemExit(bool(errors))