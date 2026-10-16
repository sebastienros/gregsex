"""Fixed-input offline XML16/17 generator; no runtime Unicode APIs or CLI parser."""
from pathlib import Path
import hashlib, json, re
ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
ALPHABET = [(9,10),(13,13),(32,0xD7FF),(0xE000,0xFFFD),(0x10000,0x10FFFF)]

def normalize(ranges):
    result=[]
    for a,b in sorted(ranges):
        if result and a <= result[-1][1]+1: result[-1]=(result[-1][0],max(b,result[-1][1]))
        else: result.append((a,b))
    return result

def clip(ranges):
    return normalize([(max(a,c),min(b,d)) for a,b in ranges for c,d in ALPHABET if a<=d and c<=b])

def intervals(values):
    return normalize([(v,v) for v in values])

def generate(version, directory):
    out=ROOT / f'src/GregsEx/Flavors/XmlShared/GeneratedXml{"" if version==16 else version}Tables.g.cs'
    inputs=directory/'inputs'
    manifest=json.loads((directory/'inputs.json').read_text())
    assert {p.relative_to(inputs).as_posix() for p in inputs.rglob('*') if p.is_file()}==set(manifest['files'])
    for name,meta in manifest['files'].items():
        raw=(inputs/name).read_bytes()
        assert len(raw)==meta['bytes'] and hashlib.sha256(raw).hexdigest()==meta['sha256'], name
    assert f'Version {version}.0.0' in (inputs/'ReadMe.txt').read_text()
    categories={}; lower={}; upper={}; pending=None
    for line in (inputs/'UnicodeData.txt').read_text().splitlines():
        f=line.split(';'); cp=int(f[0],16); cat=f[2]
        if f[1].endswith(', First>'): pending=(cp,cat); continue
        if f[1].endswith(', Last>'):
            assert pending and pending[1]==cat
            categories.setdefault(cat,[]).append((pending[0],cp)); pending=None
        else: categories.setdefault(cat,[]).append((cp,cp))
        if f[12]: upper[cp]=(int(f[12],16),)
        if f[13]: lower[cp]=(int(f[13],16),)
    assert pending is None
    assigned=normalize([x for rs in categories.values() for x in rs]); cn=[]; at=0
    for a,b in assigned:
        if at<a: cn.append((at,a-1))
        at=b+1
    if at<=0x10FFFF: cn.append((at,0x10FFFF))
    categories['Cn']=cn
    for group in 'LMNPSZC': categories[group]=[r for name,rs in list(categories.items()) if len(name)==2 and name[0]==group for r in rs]
    categories={k:clip(v) for k,v in sorted(categories.items()) if k!='Cs'}
    for line in (inputs/'SpecialCasing.txt').read_text().splitlines():
        text=line.split('#')[0].strip()
        if not text: continue
        f=[v.strip() for v in text.split(';')]
        if f[4]: continue
        cp=int(f[0],16); lower[cp]=tuple(int(x,16) for x in f[1].split()); upper[cp]=tuple(int(x,16) for x in f[3].split())
    expansions=sorted({seq for table in (lower,upper) for seq in table.values() if len(seq)!=1})
    expansion_ids={seq:0x110000+i for i,seq in enumerate(expansions)}
    def key(table,cp):
        seq=table.get(cp,(cp,)); return seq[0] if len(seq)==1 else expansion_ids[seq]
    changed={cp for cp in set(lower)|set(upper) if key(lower,cp)!=cp or key(upper,cp)!=cp}
    partners={key(table,cp) for table in (lower,upper) for cp in changed if key(table,cp)<=0x10FFFF}
    case=[(cp,key(lower,cp),key(upper,cp)) for cp in sorted(changed|partners) if any(a<=cp<=b for a,b in ALPHABET)]
    blocks={}
    for line in (inputs/'Blocks.txt').read_text().splitlines():
        text=line.split('#')[0].strip()
        if not text: continue
        raw,name=text.split(';'); a,b=(int(v,16) for v in raw.strip().split('..'))
        normalized=''.join(c for c in name.strip() if c!='_' and not c.isspace())
        assert normalized not in blocks
        blocks[normalized]=clip([(a,b)])
    blocks.update(Greek=clip([(0x370,0x3FF)]),CombiningMarksforSymbols=clip([(0x20D0,0x20FF)]),PrivateUse=clip([(0xE000,0xF8FF),(0xF0000,0xFFFFD),(0x100000,0x10FFFD)]))
    # Parse the actual pinned XML productions instead of inventing an input file.
    xml=(inputs/'XML-1.0-5e.html').read_text()
    def production(name):
        row=re.search(r'<a name="NT-'+name+r'".*?</tr>',xml,re.S)
        # The lazy prefix can reach earlier rows; constrain to the RHS cell.
        rhs=re.search(r'::=.*?</td><td><code>(.*?)</code>',row.group(0),re.S)[1]
        return re.sub('<[^>]*>','',rhs).replace('&quot;','"').strip()
    def xml_ranges(text):
        out=[]
        for token in text.split('|'):
            token=token.strip()
            m=re.fullmatch(r'\[#x([0-9A-Fa-f]+)-#x([0-9A-Fa-f]+)\]',token)
            if m: out.append(tuple(int(v,16) for v in m.groups())); continue
            m=re.fullmatch(r'#x([0-9A-Fa-f]+)',token)
            if m: out.append((int(m[1],16),)*2); continue
            m=re.fullmatch(r'"(.)"',token)
            if m: out.append((ord(m[1]),)*2); continue
            if token=='[A-Z]': out.append((65,90)); continue
            if token=='[a-z]': out.append((97,122)); continue
            if token=='[0-9]': out.append((48,57)); continue
            if token=='NameStartChar': continue
            raise ValueError((token,text))
        return normalize(out)
    assert xml_ranges(production('Char'))==ALPHABET
    names=xml_ranges(production('NameStartChar')); namechars=normalize(names+xml_ranges(production('NameChar')))
    excluded=normalize(categories['P']+categories['Z']+categories['C']); word=[]
    for a,b in ALPHABET:
        cursor=a
        for c,d in excluded:
            if d<cursor or c>b: continue
            if c>cursor: word.append((cursor,c-1))
            cursor=max(cursor,d+1)
        if cursor<=b: word.append((cursor,b))
    predefined={'Whitespace':[(9,10),(13,13),(32,32)],'Digit':categories['Nd'],'Word':normalize(word),'NameStart':names,'NameChar':namechars}
    tables={'alphabet':ALPHABET}|{'category:'+k:v for k,v in categories.items()}|{'block:'+k:v for k,v in sorted(blocks.items())}|{'predefined:'+k:v for k,v in predefined.items()}
    flat=[]; slices={}
    for name,rs in tables.items():
        slices[name]=(len(flat)//2,len(rs)); flat.extend(x for r in rs for x in r)
    parts=[f'// Generated by tools/unicode/xml/generate.py. Unicode{version}/XML1.0 Fifth Edition.\nusing GregsEx.Contracts.Xml;\nnamespace GregsEx.Flavors.XmlShared;\ninternal static class GeneratedXml{"" if version==16 else version}Tables\n{{']
    def ints(name,values):
        parts.append(f'    internal static ReadOnlySpan<int> {name} => [')
        for i in range(0,len(values),12): parts.append('        '+', '.join(str(x) for x in values[i:i+12])+',')
        parts.append('    ];')
    ints('Intervals',flat); ints('CaseMappings',[x for row in case for x in row])
    def lookup(method,prefix,keys):
        parts.append(f'    internal static bool {method}(ReadOnlySpan<char> name, out XmlSetSlice slice)\n    {{')
        for name in keys:
            a,b=slices[prefix+name]; parts.append(f'        if (name.SequenceEqual("{name}")) {{ slice = new({a}, {b}); return true; }}')
        parts.append('        slice = default; return false;\n    }')
    lookup('Category','category:',categories); lookup('Block','block:',sorted(blocks))
    a,b=slices['alphabet']; parts.append(f'    internal static XmlSetSlice Alphabet => new({a}, {b});')
    parts.append('    internal static XmlSetSlice Predefined(XmlPredefinedClass kind) => kind switch\n    {')
    for name in predefined:
        a,b=slices['predefined:'+name]; parts.append(f'        XmlPredefinedClass.{name} => new({a}, {b}),')
    parts.append('        _ => throw new ArgumentOutOfRangeException(nameof(kind))\n    };')
    image=4*(len(flat)+3*len(case)); literals=sum(32+2*len(k) for k in categories|blocks)
    parts.append(f'    internal const long ImageBytes = {image};\n    internal const long LiteralBytes = {literals};\n}}\n')
    out.write_text('\n'.join(parts))
    # Audit-readable emitted dataset, not a second runtime source.
    dataset={'format':'xml-unicode-emitted-1','tables':tables,'case':case,'expansions':[list(x) for x in expansions]}
    (directory/'emitted.json').write_text(json.dumps(dataset,separators=(',',':'))+'\n')
    paths=[out,directory/'emitted.json']
    metadata={'format':'xml-unicode-generation-1','data_id':f'Unicode-{version}.0.0+XML-1.0-Fifth-Edition','generator_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'inputs_manifest_sha256':hashlib.sha256((directory/'inputs.json').read_bytes()).hexdigest(),'counts':{'categories':len(categories),'blocks':len(blocks),'case_mappings':len(case),'expansions':len(expansions),'intervals':len(flat)//2,'static_image_bytes':image,'literal_bytes':literals},'outputs':{str(p.relative_to(ROOT)):{'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in paths}}
    (directory/'outputs.json').write_text(json.dumps(metadata,indent=2)+'\n')
    print(json.dumps({'data_id':metadata['data_id'],**metadata['counts']}))
if __name__=='__main__':
    for version,directory in [(16,HERE),(17,HERE/'17.0.0')]: generate(version,directory)
