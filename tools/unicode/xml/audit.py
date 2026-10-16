"""Independent full-source audit. Does not import the generator or host Unicode."""
from pathlib import Path
import hashlib,json,re
HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[2]

def audit(version,directory):
    inputs=directory/'inputs'
    manifest=json.loads((directory/'inputs.json').read_text())
    assert {p.relative_to(inputs).as_posix() for p in inputs.rglob('*') if p.is_file()}==set(manifest['files'])
    for name,item in manifest['files'].items():
        raw=(inputs/name).read_bytes()
        assert len(raw)==item['bytes'] and hashlib.sha256(raw).hexdigest()==item['sha256']
    assert f'Version {version}.0.0' in (inputs/'ReadMe.txt').read_text()
    emitted=json.loads((directory/'emitted.json').read_text())
    cs=(ROOT/f'src/GregsEx/Flavors/XmlShared/GeneratedXml{"" if version==16 else version}Tables.g.cs').read_text()
    assert f'internal static class GeneratedXml{"" if version==16 else version}Tables\n' in cs
    # Verify the real shipped C# data and lookup slices, not just a sidecar hash.
    flat=[int(x) for x in re.search(r'Intervals => \[(.*?)\];',cs,re.S)[1].replace(',',' ').split()]
    case=[int(x) for x in re.search(r'CaseMappings => \[(.*?)\];',cs,re.S)[1].replace(',',' ').split()]
    assert len(flat)%2==0 and len(case)%3==0
    cat_body=cs.split('bool Category(')[1].split('bool Block(')[0]
    block_body=cs.split('bool Block(')[1].split('XmlSetSlice Alphabet')[0]
    for prefix,body in [('category:',cat_body),('block:',block_body)]:
        lookups=re.findall(r'name.SequenceEqual\("([^"]+)"\).*?new\((\d+), (\d+)\)',body)
        assert len(lookups)==len({name for name,_,_ in lookups})
        assert {prefix+n for n,_,_ in lookups}=={k for k in emitted['tables'] if k.startswith(prefix)}
        for name,start,count in lookups:
            actual=[flat[i:i+2] for i in range(2*int(start),2*(int(start)+int(count)),2)]
            assert actual==emitted['tables'][prefix+name]
    assert [case[i:i+3] for i in range(0,len(case),3)]==emitted['case']
    extra=[('alphabet',re.search(r'Alphabet => new\((\d+), (\d+)\)',cs))]
    extra += [('predefined:'+n,re.search(r'XmlPredefinedClass\.'+n+r' => new\((\d+), (\d+)\)',cs)) for n in ['Whitespace','Digit','Word','NameStart','NameChar']]
    for name,m in extra:
        start,count=map(int,m.groups()); assert [flat[i:i+2] for i in range(2*start,2*(start+count),2)]==emitted['tables'][name]
    size=0x110000
    legal=bytearray(size)
    for a,b in [(9,10),(13,13),(32,0xD7FF),(0xE000,0xFFFD),(0x10000,0x10FFFF)]: legal[a:b+1]=b'\1'*(b-a+1)
    cats=['Cn']*size; lower={}; upper={}; start=None; ranges=0
    for line in (inputs/'UnicodeData.txt').read_text().splitlines():
        f=line.split(';'); cp=int(f[0],16)
        if ', First>' in f[1]: start=cp
        elif ', Last>' in f[1]:
            assert start is not None
            cats[start:cp+1]=[f[2]]*(cp-start+1); start=None; ranges+=1
        else: cats[cp]=f[2]
        lower[cp]=[int(f[13],16)] if f[13] else [cp]
        upper[cp]=[int(f[12],16)] if f[12] else [cp]
    for line in (inputs/'SpecialCasing.txt').read_text().splitlines():
        f=line.split('#')[0].split(';')
        if len(f)<5 or f[4].strip(): continue
        cp=int(f[0].strip(),16)
        lower[cp]=[int(x,16) for x in f[1].split()]; upper[cp]=[int(x,16) for x in f[3].split()]
    def bits(intervals):
        result=bytearray(size); prior=-2
        for a,b in intervals:
            assert 0<=a<=b<size and a>prior+1
            result[a:b+1]=b'\1'*(b-a+1); prior=b
        assert not any(v and not legal[i] for i,v in enumerate(result))
        return result
    assert bits(emitted['tables']['alphabet'])==legal
    expected_names={c for c in cats if c!='Cs'}|set('LMNPSZC')
    assert {k[9:] for k in emitted['tables'] if k.startswith('category:')}==expected_names
    for name in expected_names:
        expected=bytearray(legal[i] and (c==name if len(name)==2 else c[0]==name) for i,c in enumerate(cats))
        assert bits(emitted['tables']['category:'+name])==expected, name
    expected_blocks={}
    for line in (inputs/'Blocks.txt').read_text().splitlines():
        text=line.split('#')[0].strip()
        if not text: continue
        rng,name=text.split(';'); a,b=[int(x,16) for x in rng.strip().split('..')]
        name=re.sub(r'[\s_]','',name)
        expected_blocks[name]=[(a,b)]
    expected_blocks['Greek']=[(0x370,0x3FF)]
    expected_blocks['CombiningMarksforSymbols']=[(0x20D0,0x20FF)]
    expected_blocks['PrivateUse']=[(0xE000,0xF8FF),(0xF0000,0xFFFFD),(0x100000,0x10FFFD)]
    assert {k[6:] for k in emitted['tables'] if k.startswith('block:')}==set(expected_blocks)
    for name,rs in expected_blocks.items():
        expected=bytearray(size)
        for a,b in rs: expected[a:b+1]=legal[a:b+1]
        assert bits(emitted['tables']['block:'+name])==expected,name
    expansions=sorted({tuple(v) for table in (lower,upper) for v in table.values() if len(v)>1})
    assert emitted['expansions']==[list(x) for x in expansions]
    keys={seq:size+i for i,seq in enumerate(expansions)}
    def sequence_key(table,cp):
        seq=table.get(cp,[cp]); return seq[0] if len(seq)==1 else keys[tuple(seq)]
    rows={row[0]:row[1:] for row in emitted['case']}
    inverse_low={}; inverse_up={}
    for cp in range(size):
        if not legal[cp]: continue
        lo=sequence_key(lower,cp); up=sequence_key(upper,cp)
        assert rows.get(cp,[cp,cp])==[lo,up],hex(cp)
        inverse_low.setdefault(lo,[]).append(cp); inverse_up.setdefault(up,[]).append(cp)
    # A sparse row is mandatory for every participant in a variant relation,
    # including identity partners; omitted rows have no nontrivial relation.
    related=set()
    for table in (inverse_low,inverse_up):
        for group in table.values():
            if len(group)>1: related.update(group)
    assert related<=set(rows)
    names=[(58,58),(65,90),(95,95),(97,122),(0xC0,0xD6),(0xD8,0xF6),(0xF8,0x2FF),(0x370,0x37D),(0x37F,0x1FFF),(0x200C,0x200D),(0x2070,0x218F),(0x2C00,0x2FEF),(0x3001,0xD7FF),(0xF900,0xFDCF),(0xFDF0,0xFFFD),(0x10000,0xEFFFF)]
    # Independent XML5 productions [2],[4],[4a] literals; source is pinned.
    expected=bits(names); assert bits(emitted['tables']['predefined:NameStart'])==expected
    for a,b in [(45,46),(48,57),(0xB7,0xB7),(0x300,0x36F),(0x203F,0x2040)]: expected[a:b+1]=b'\1'*(b-a+1)
    assert bits(emitted['tables']['predefined:NameChar'])==expected
    for name,predicate in [('Whitespace',lambda cp,c:cp in (9,10,13,32)),('Digit',lambda cp,c:c=='Nd'),('Word',lambda cp,c:c[0] not in 'PZC')]:
        assert bits(emitted['tables']['predefined:'+name])==bytearray(legal[i] and predicate(i,c) for i,c in enumerate(cats))
    image=4*(len(flat)+len(case)); literals=sum(32+2*len(k) for k in expected_names|set(expected_blocks))
    assert int(re.search(r'ImageBytes = (\d+);',cs)[1])==image
    assert int(re.search(r'LiteralBytes = (\d+);',cs)[1])==literals
    assert len(rows)==len(emitted['case']) and emitted['case']==sorted(emitted['case'])
    assert sum(len(rs) for rs in emitted['tables'].values())*2==len(flat)
    assert set(emitted['tables'])=={'alphabet'}|{'category:'+n for n in expected_names}|{'block:'+n for n in expected_blocks}|{'predefined:'+n for n in ['Whitespace','Digit','Word','NameStart','NameChar']}
    metadata=json.loads((directory/'outputs.json').read_text())
    assert metadata['data_id']==f'Unicode-{version}.0.0+XML-1.0-Fifth-Edition'
    assert metadata['generator_sha256']==hashlib.sha256((HERE/'generate.py').read_bytes()).hexdigest()
    assert metadata['inputs_manifest_sha256']==hashlib.sha256((directory/'inputs.json').read_bytes()).hexdigest()
    assert metadata['counts']=={'categories':len(expected_names),'blocks':len(expected_blocks),'case_mappings':len(rows),'expansions':len(expansions),'intervals':len(flat)//2,'static_image_bytes':image,'literal_bytes':literals}
    paths={f'src/GregsEx/Flavors/XmlShared/GeneratedXml{"" if version==16 else version}Tables.g.cs',str((directory/'emitted.json').relative_to(ROOT))}
    assert set(metadata['outputs'])==paths
    for name,item in metadata['outputs'].items():
        raw=(ROOT/name).read_bytes()
        assert len(raw)==item['bytes'] and hashlib.sha256(raw).hexdigest()==item['sha256'],name
    result={'data_id':f'Unicode-{version}.0.0+XML-1.0-Fifth-Edition','scalars_examined':sum(legal),'algorithmic_ranges':ranges,'categories':len(expected_names),'blocks':len(expected_blocks),'case_rows':len(rows),'expansions':len(expansions),'relation_participants':len(related),'static_image_bytes':image,'literal_bytes':literals,'outcome':'passed'}
    print(json.dumps(result,sort_keys=True))
if __name__=='__main__':
    for version,directory in [(16,HERE),(17,HERE/'17.0.0')]: audit(version,directory)
