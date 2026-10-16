"""Read only the retained PCRE2 release after validating its full source manifest."""
from pathlib import Path
import hashlib
import json
import re
import tarfile

ROOT = Path(__file__).resolve().parents[3]
REPORT = ROOT / 'test/corpora/pcre2/data'
PRODUCT = ROOT / 'src/GregsEx/Flavors/Pcre2/Tables'
TEST = ROOT / 'test/GregsEx.Tests/Flavors/Pcre2/Tables'
PIN = ROOT / 'tools/oracles/pcre2/source'


def intake():
    manifest = json.loads((PIN / 'source-manifest.json').read_text())
    assert manifest['commit'] == 'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff'
    archive = PIN / 'pcre2-10.46-source.tar.gz'
    assert archive.stat().st_size == manifest['archive_bytes'] == 3268715
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == manifest['archive_sha256'] == 'ffde0c6ead457c4f8edcd8f9ce490bbabd30adabdfd179ecaddd320946cf3872'
    data = {}
    with tarfile.open(archive) as tar:
        for member in manifest['files']:
            value = tar.extractfile('pcre2-pcre2-10.46/' + member['path']).read()
            assert len(value) == member['bytes']
            assert hashlib.sha256(value).hexdigest() == member['sha256'], member['path']
            data[member['path']] = value
    assert len(data) == 488
    return manifest, data


def clean(text):
    return re.sub(r'/\*.*?\*/', '', text, flags=re.S)


def array(text, name):
    matches = list(re.finditer(r'\b' + re.escape(name) + r'\)\[\]\s*=\s*\{(.*?)\};', text, re.S))
    assert matches, name
    body = clean(matches[-1][1])
    return [int(x, 0) for x in re.findall(r'-?0x[0-9a-fA-F]+|-?\d+', body.replace('NOTACHAR', '0xffffffff').replace('u', ''))]


def enums(header):
    result = {}
    for body in re.findall(r'enum\s*\{(.*?)\};', header, re.S):
        for index, name in enumerate(re.findall(r'\bucp_\w+', clean(body))):
            result[name] = index
    return result


def alias_fixture(data):
    """Independent source fixture retains native PT IDs and checks C string offsets."""
    text = data['src/pcre2_ucptables.c'].decode()
    string_table = {}
    for name, body in re.findall(r'^#define STRING_(\w+)0 (.*?)$', text, re.M):
        string_table[name] = ''.join('&' if token == 'AMPERSAND' else token for token in re.findall(r'STR_(\w+)', body))
    ordered = re.findall(r'^  STRING_(\w+)0[;]?', text, re.M)
    names, offset = {}, 0
    for name in ordered:
        names[offset] = string_table[name]
        offset += len(string_table[name])+1
    ids = enums(data['src/pcre2_ucp.h'].decode())
    native_types = {name:int(value) for name,value in re.findall(r'^#define (PT_\w+)\s+(\d+)', data['src/pcre2_internal.h'].decode(), re.M)}
    lines = []
    for offset, kind, value in re.findall(r'\{\s*(\d+),\s*(PT_\w+),\s*(\w+)\s*\}', text):
        lines.append(f'{names[int(offset)]}|{native_types[kind]}|{ids[value] if value.startswith("ucp_") else int(value)}')
    assert len(lines) == 510
    return '// Source-derived native alias fixture; regenerate with tools/oracles/pcre2-data/generate.py.\nnamespace GregsEx.Tests.Flavors.Pcre2.Tables;\ninternal static class Pcre2SourceAliases\n{\n    internal const string Rows = """\n'+'\n'.join(lines)+'\n""";\n}\n'


if __name__ == '__main__':
    manifest, data = intake()
    selected = [f for f in manifest['files'] if f['path'].startswith('maint/Unicode.tables/') or f['path'] in (
        'maint/GenerateCommon.py', 'maint/GenerateUcd.py', 'maint/GenerateUcpHeader.py', 'maint/GenerateUcpTables.py',
        'src/pcre2_ucd.c', 'src/pcre2_ucp.h', 'src/pcre2_ucptables.c', 'src/pcre2_compile.c',
        'src/pcre2_substitute.c', 'src/pcre2_internal.h', 'src/pcre2_tables.c', 'src/pcre2_script_run.c',
        'src/pcre2_chartables.c.dist', 'LICENCE.md')]
    assert sum(f['path'].startswith('maint/Unicode.tables/') for f in selected) == 13
    lock = {k: manifest[k] for k in ('release', 'commit', 'tag_object', 'archive_bytes', 'archive_sha256', 'retrieval_url')}
    lock['archive_path'] = 'tools/oracles/pcre2/source/pcre2-10.46-source.tar.gz'
    lock['full_manifest_path'] = 'tools/oracles/pcre2/source/source-manifest.json'
    lock['verified_members'] = len(data)
    lock['files'] = selected
    REPORT.mkdir(parents=True, exist_ok=True)
    (REPORT / 'source-intake.json').write_text(json.dumps(lock, indent=2) + '\n')
    print('Verified archive, all488 member hashes,13UCD inputs and generator/algorithm/license sources.')
