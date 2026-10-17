"""Fixed no-argument generator: project hash-verified actual PCRE2 tables, never host Unicode."""
from source import intake, clean, array, enums, alias_fixture, PRODUCT, REPORT, ROOT, TEST
import bisect
import hashlib
import json
import re
import struct


def projection():
    manifest, data = intake()
    # Require a durable source-before-product intake, and detect a changed pin.
    lock = json.loads((REPORT / 'source-intake.json').read_text())
    assert lock['archive_sha256'] == manifest['archive_sha256']
    ucd = data['src/pcre2_ucd.c'].decode()
    ids = enums(data['src/pcre2_ucp.h'].decode())
    raw_records = list(re.finditer(r'ucd_records\)\[\]\s*=\s*\{(.*?)\};', ucd, re.S))[-1][1]
    native_records = [tuple(map(int, re.findall(r'-?\d+', r))) for r in re.findall(r'\{([^{}]+)\}', clean(raw_records))]
    assert len(native_records) == 1543 and all(len(r) == 7 for r in native_records)
    stage1, stage2 = array(ucd, 'ucd_stage1'), array(ucd, 'ucd_stage2')
    native_cases, native_scripts, booleans = [array(ucd, 'ucd_' + n) for n in ('caseless_sets', 'script_sets', 'boolprop_sets')]
    digit_ends = array(ucd, 'ucd_digit_sets')
    assert digit_ends[0] == len(digit_ends) - 1
    digit_ends = digit_ends[1:]
    cases, case_map = [], {0: (0, 0)}
    for offset, value in enumerate(native_cases):
        if value == 0xffffffff:
            continue
        if offset == 0 or native_cases[offset-1] == 0xffffffff:
            end = native_cases.index(0xffffffff, offset)
            case_map[offset] = (len(cases), end-offset)
        cases.append(value)
    turkish = int(re.search(r'ucd_turkish_dotted_i_caseset\)\s*=\s*(\d+)', ucd)[1])
    # Normalize the source's compact4-word extensions to all171 script IDs.
    scripts, script_map = [], {}
    def scriptset(record):
        script, _, _, _, _, packed, _ = record
        offset = packed & 0x3ff
        key = (script, offset)
        if key not in script_map:
            words = native_scripts[offset:offset+4] + [0, 0]
            if offset == 0:
                words[script//32] |= 1 << (script%32)
            script_map[key] = len(scripts)
            scripts.extend(words)
        return script_map[key]
    body = clean(data['src/pcre2_chartables.c.dist'].decode().split('PRIV(default_tables)[] = {')[1].split('};')[0])
    low = [int(n, 0) for n in re.findall(r'0x[\da-fA-F]+|\d+', body)]
    assert len(low) == 1088
    output, records, record_map, index = [], [], {}, []
    reference_hash = hashlib.sha256()
    for cp in range(0x110000):
        native = native_records[stage2[stage1[cp//128]*128+cp%128]]
        script, category, gb, co, delta, packed, bo = native
        cs, cc = case_map[co]
        so = scriptset(native)
        digitset = bisect.bisect_left(digit_ends, cp)+1 if category == 13 else 0
        r = (category, script, packed >> 11, gb, delta, cs, cc, so, 6, bo, 2, digitset)
        if r not in record_map:
            record_map[r] = len(records)
            records.append(r)
        index.append(record_map[r])
        lower, upper = cp, cp
        if cp < 128:
            lower = cp if low[512+4*32+cp//8] & (1 << (cp%8)) else low[256+cp]
            upper = cp if low[512+3*32+cp//8] & (1 << (cp%8)) else low[256+cp]
        elif 5 <= category <= 9:
            lower = cp if category == 5 else cp+delta
            upper = cp if category == 9 else cp+delta
        # PCRE2 default title-first uses the same upper transformation on its first character.
        if lower != cp or upper != cp:
            output.append((cp, lower, upper, upper))
        # Canonical exhaustive stream independent of compression and offsets.
        values = (category, script, packed >> 11, gb, delta, cc, *cases[cs:cs+cc],
                  *scripts[so:so+6], *booleans[bo:bo+2], digitset, lower, upper, upper)
        reference_hash.update(struct.pack('<' + 'i'*len(values), *[x if x < 0x80000000 else x-0x100000000 for x in values]))
    blocks, s1, s2 = {}, [], []
    for cp in range(0, 0x110000, 128):
        b = tuple(index[cp:cp+128])
        if b not in blocks:
            blocks[b] = len(s2)//128
            s2.extend(b)
        s1.append(blocks[b])
    assert max(s1) < 65536 and max(s2) < 65536
    # Parse the source's actual alias entries, not all aliases from a different profile.
    utt = data['src/pcre2_ucptables.c'].decode()
    names = re.findall(r'#define STRING_(\w+)0 ', utt)
    entries = re.findall(r'\{\s*(\d+),\s*(PT_\w+),\s*(\w+)\s*\}', utt)
    assert len(names) == len(entries) == 510
    kinds = {'PT_GC': 'MajorCategory', 'PT_PC': 'ParticularCategory', 'PT_SC': 'Script',
             'PT_SCX': 'ScriptExtensions', 'PT_BOOL': 'Binary', 'PT_BIDICL': 'BidiClass'}
    special = {'PT_LAMP': 0, 'PT_ALNUM': 5, 'PT_SPACE': 6, 'PT_PXSPACE': 7, 'PT_WORD': 8, 'PT_UCNC': 10, 'PT_ANY': 13}
    aliases = [(n.replace('_AMPERSAND','&'), kinds.get(kind,'Special'), ids[value] if kind in kinds else special[kind])
               for n, (_,kind,value) in zip(names, entries, strict=True)]
    assert aliases == sorted(aliases)
    # Source grapheme adjacency masks, preserving the non-table contextual rules for the Unicode owner.
    table_c = data['src/pcre2_tables.c'].decode()
    gb_body = clean(re.search(r'ucp_gbtable\)\[\]\s*=\s*\{(.*?)\};', table_c, re.S)[1])
    masks = []
    for expr in gb_body.split(','):
        if not expr.strip(): continue
        expr = expr.replace('ESZ', '(1<<3)|(1<<5)|(1<<13)')
        expr = re.sub(r'ucp_\w+', lambda m:str(ids[m[0]]), expr).replace('1u','1')
        assert re.fullmatch(r'[\d\s()<>|]+',expr)
        masks.append(eval(re.sub(r'\s+', '', expr), {'__builtins__':{}}, {}))
    assert len(masks) == 15
    products = {}
    def primitive(name, typ, values):
        rows = [', '.join(str(x)+('u' if typ == 'uint' else '') for x in values[i:i+24]) for i in range(0,len(values),24)]
        return f'    internal static ReadOnlySpan<{typ}> {name} => [\n        ' + ',\n        '.join(rows) + '\n    ];\n'
    header = '#nullable enable\n// Generated by tools/oracles/pcre2-data/generate.py; pinned PCRE2-10.46 b2bd4254.\n// Source copyright and BSD terms: PCRE2-DATA-NOTICE.txt.\nusing GregsEx.Contracts.Pcre2;\nnamespace GregsEx.Flavors.Pcre2.Tables;\n\ninternal static partial class Pcre2GeneratedData\n{\n'
    products['Pcre2PrimitiveTables.g.cs'] = header + ''.join(primitive(n,t,v) for n,t,v in (
        ('Stage1','ushort',s1),('Stage2','ushort',s2),('CaseSets','int',cases),('ScriptSets','uint',scripts),
        ('BooleanSets','uint',booleans),('DigitSetEnds','int',digit_ends),('GraphemeNoBreakMasks','uint',masks))) + '}\n'
    def records_file(name, typ, rows):
        return header + f'    private static readonly {typ}[] s_{name} = [\n' + ''.join('        new('+', '.join(map(str,r))+'),\n' for r in rows) + f'    ];\n    internal static ReadOnlySpan<{typ}> {name} => s_{name};\n' + '}\n'
    products['Pcre2Records.g.cs'] = records_file('Records','Pcre2UnicodeRecord', records)
    products['Pcre2OutputCases.g.cs'] = records_file('OutputCases','Pcre2SimpleCase',output)
    products['Pcre2Aliases.g.cs'] = header + '    private static readonly Pcre2Alias[] s_aliases = [\n' + ''.join(
        f'        new("{n}", new(Pcre2PropertyKind.{k}, {i})),\n' for n,k,i in aliases) + '    ];\n    internal static ReadOnlySpan<Pcre2Alias> Aliases => s_aliases;\n}\n'
    notice = ('GregsEx projection of actual PCRE2 10.46 Unicode16.0.0 data/aliases/algorithms.\n'
              'Source commit: ' + manifest['commit'] + '\nArchive SHA256: '+manifest['archive_sha256']+'\n'
              'No native PCRE2 engine is distributed. Default substitution case_force quirks are retained.\n\n')
    # Preserve the generated module's own copyright/BSD boilerplate, not just
    # the decorative first comment or the library's different copyright years.
    notice += ucd.split('#ifndef PCRE2_PCRE2TEST',1)[0] + '\n\n' + data['LICENCE.md'].decode()
    products['PCRE2-DATA-NOTICE.txt'] = notice
    # Retain Unicode notices from actual inputs and the pinned UnicodeData license shipped previously.
    notices = '\n\n'.join(data[p].decode().splitlines()[0] + '\n' + '\n'.join(data[p].decode().splitlines()[:45])
                           for p in sorted(data) if p.startswith('maint/Unicode.tables/') and p.endswith('.txt') and not p.endswith('UnicodeData.txt'))
    products['Unicode-DATA-NOTICE.txt'] = 'Notices from the13 actual release Unicode input members (UnicodeData.txt has no header).\n\n' + notices + '\n'
    products['Unicode-LICENSE.txt'] = (ROOT / 'src/GregsEx/Flavors/EcmaScript/Unicode/Unicode-LICENSE.txt').read_text()
    products['Pcre2Identity.g.cs'] = header + f'    internal const string ExhaustiveDigest = "{reference_hash.hexdigest()}";\n' + ''.join(
        f'    internal const int Turkish{n}Offset = {case_map[o][0]};\n' for n,o in [('DottedI',turkish),('DotlessI',turkish+3)]) + '}\n'
    summary = {'source_archive_sha256':manifest['archive_sha256'], 'unicode_version':'16.0.0',
               'native_records':len(native_records), 'records':len(records), 'stage1':len(s1),'stage2':len(s2),
               'case_values':len(cases), 'script_words':len(scripts),'boolean_words':len(booleans),
               'digit_sets':len(digit_ends),'output_cases':len(output),'aliases':len(aliases),
               'exhaustive_sha256':reference_hash.hexdigest(),
               'products':{n:hashlib.sha256(s.encode()).hexdigest() for n,s in products.items()},
               'alias_fixture_sha256':hashlib.sha256(alias_fixture(data).encode()).hexdigest()}
    return products, summary


if __name__ == '__main__':
    products, summary = projection()
    for name, content in products.items():
        (PRODUCT / name).write_text(content)
    (TEST / 'Pcre2SourceAliases.g.cs').write_text(alias_fixture(intake()[1]))
    (REPORT / 'projection-manifest.json').write_text(json.dumps(summary, indent=2)+'\n')
    print(json.dumps({k:v for k,v in summary.items() if k!='products'},indent=2))
