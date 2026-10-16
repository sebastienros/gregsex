"""Independent fixed-input audits of both emitted C# datasets against originals.
Does not import the generator. Source sets are enumerated independently; no host
Unicode APIs or .NET/runtime categories enter expected membership.
"""
from pathlib import Path
import json,re,hashlib,zipfile
ROOT=Path(__file__).resolve().parent;REPO=ROOT.parents[2]

def audit(version,folder):
 I=folder/'inputs'
 table_class=f'GeneratedUnicode{"" if version==16 else version}Tables'
 inventory_class=f'Unicode{"" if version==16 else version}Inventory'
 text=(REPO/f'src/GregsEx/Flavors/EcmaScript/Unicode/{table_class}.cs').read_text()
 manifest=json.loads((folder/'inputs.sha256.json').read_text())
 actual={p.relative_to(I).as_posix():{'length':len(p.read_bytes()),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in I.rglob('*') if p.is_file()}
 assert actual==manifest['files'],'source inventory/hash/length mismatch'
 if 'archive' in manifest['ucd_delivery']:
  delivery=manifest['ucd_delivery'];archive=folder/delivery['archive'];raw=archive.read_bytes()
  assert len(raw)==delivery['length'] and hashlib.sha256(raw).hexdigest()==delivery['sha256']
  with zipfile.ZipFile(archive) as source:
   for name in actual:
    if name.startswith('ucd/'):assert source.read(name[4:])==(I/name).read_bytes(),name
 assert f'Version {version}.0.0' in (I/'ucd/ReadMe.txt').read_text()
 assert all(f'# Version: {version}.0' in (I/n).read_text() for n in ['emoji-sequences.txt','emoji-zwj-sequences.txt'])
 assert (I/'ecma-property-tables.json').read_bytes()==(ROOT/'inputs/ecma-property-tables.json').read_bytes()
 assert (I/'LICENSE.txt').read_bytes()==(ROOT/'inputs/LICENSE.txt').read_bytes()
 assert f'internal static class {table_class}\n' in text
 def rows(name):
  for line in (I/name).read_text().splitlines():
   line=line.partition('#')[0].strip()
   if line:yield [v.strip() for v in line.split(';')]
 def codes(field):
  if '..' in field:
   a,b=field.split('..');return range(int(a,16),int(b,16)+1)
  return [int(field,16)]
 def ints(name):
  s=re.search(r'ReadOnlySpan<int> '+name+r' => \[(.*?)\];',text,re.S).group(1)
  return [int(x) for x in re.findall(r'-?\d+',s)]
 iv=ints('Intervals');cp=ints('StringCodePoints');ss=ints('StringSlices')
 entries=re.findall(r'EcmaPropertyKind\.(\w+),\s*"([^"]+)"\) => \((\d+),\s*(\d+)\)',text)
 directory={(k,n):(int(o),int(c)) for k,n,o,c in entries}
 assert len(entries)==len(directory) and len(iv)%2==len(ss)%2==0
 cursor=0
 for off,count in directory.values():
  assert off==cursor and 0<=count and 2*(off+count)<=len(iv)
  prior=-2
  for j in range(off,off+count):
   a,b=iv[2*j:2*j+2];assert 0<=a<=b<=0x10ffff and a>prior+1;prior=b
  cursor+=count
 assert cursor*2==len(iv)
 def emitted(k,n):
  o,c=directory[k,n];s=set()
  for j in range(o,o+c):s.update(range(iv[2*j],iv[2*j+1]+1))
  return s
 aliases={};scripts={}
 for row in rows('ucd/PropertyValueAliases.txt'):
  if row[0] in ['sc','gc']:
   for a in row[1:]:aliases[row[0],a]=row[2]
   if row[0]=='sc':scripts[row[2]]=set()
 # UnicodeData range records independently cover categories (including surrogates).
 groups={};simple={};pending=None
 for r in rows('ucd/UnicodeData.txt'):
  p=int(r[0],16);cat=aliases['gc',r[2]]
  if r[1].endswith(', First>'):assert pending is None;pending=p;continue
  start=pending if r[1].endswith(', Last>') else p;pending=None
  assert start is not None
  groups.setdefault(cat,set()).update(range(start,p+1))
  if r[12]:simple[p]=[int(r[12],16)]
 assert pending is None
 assigned=set().union(*groups.values());groups['Unassigned']=set(range(0x110000))-assigned
 gc_names=set()
 for short,canonical in [(a,c) for (k,a),c in aliases.items() if k=='gc' and len(a)<=2]:
  if len(short)==1:
   cats=[c for (k,a),c in aliases.items() if k=='gc' and len(a)==2 and a.startswith(short) and a!='LC']
   expected=set().union(*(groups[c] for c in cats))
  elif short=='LC':expected=groups['Uppercase_Letter']|groups['Lowercase_Letter']|groups['Titlecase_Letter']
  else:expected=groups[canonical]
  assert emitted('GeneralCategory',canonical)==expected,('gc',canonical)
  gc_names.add(canonical)
 for r in rows('ucd/Scripts.txt'):scripts[r[1]].update(codes(r[0]))
 scripts['Unknown']=set(range(0x110000))-set().union(*scripts.values())
 ext={n:set(s) for n,s in scripts.items()}
 for r in rows('ucd/ScriptExtensions.txt'):
  points=set(codes(r[0]))
  for s in ext.values():s.difference_update(points)
  for a in r[1].split():ext[aliases['sc',a]].update(points)
 for name,s in scripts.items():assert emitted('Script',name)==s,('script',name)
 for name,s in ext.items():assert emitted('ScriptExtensions',name)==s,('scx',name)
 # Independently recover exact edition16 admission and aliases from retained cells.
 admission=json.loads((I/'ecma-property-tables.json').read_text())
 admitted_aliases={}
 for kind in ['binary','nonbinary']:
  current=None;mapping={}
  for cells in admission[kind]:
   if len(cells)==2:current=cells[1][0]
   assert current is not None
   for alias in cells[0]:mapping[alias]=current
  admitted_aliases[kind]=mapping
 binary_names=set(admitted_aliases['binary'].values())
 string_names={cells[0][0] for cells in admission['string']}
 assert len(binary_names)==53 and len(string_names)==7
 assert set(directory)=={('GeneralCategory',n) for n in gc_names}|{(k,n) for k in ['Script','ScriptExtensions'] for n in scripts}|{('Binary',n) for n in binary_names}|{('String',n) for n in string_names}
 # Use direct expanded source membership, not interval merge/subtract routines.
 binary={}
 for file in ['PropList.txt','DerivedCoreProperties.txt','DerivedNormalizationProps.txt','extracted/DerivedBinaryProperties.txt','emoji/emoji-data.txt']:
  for r in rows('ucd/'+file):
   if r[1] in binary_names:binary.setdefault(r[1],set()).update(codes(r[0]))
 binary.update(ASCII=set(range(128)),Any=set(range(0x110000)),Assigned=assigned)
 assert set(binary)==binary_names
 for name,s in binary.items():assert emitted('Binary',name)==s,('binary',name)
 source_strings={n:set() for n in string_names}
 for file in ['emoji-sequences.txt','emoji-zwj-sequences.txt']:
  for r in rows(file):
   if '..' in r[0]:source_strings[r[1]].update((c,) for c in codes(r[0]))
   else:source_strings[r[1]].add(tuple(int(v,16) for v in r[0].split()))
 source_strings['RGI_Emoji']=set().union(*source_strings.values())
 string_entries=re.findall(r'"([^"]+)" => \((\d+),\s*(\d+)\)',text)
 sdir={n:(int(o),int(c)) for n,o,c in string_entries}
 assert len(string_entries)==len(sdir) and set(sdir)==string_names
 string_cursor=point_cursor=0
 for n,(o,c) in sdir.items():
  assert o==string_cursor and 2*(o+c)<=len(ss)
  actual={(v,) for v in emitted('String',n)};ordered=[]
  for j in range(o,o+c):
   a,l=ss[2*j:2*j+2]
   assert a==point_cursor and l>1 and a+l<=len(cp)
   seq=tuple(cp[a:a+l]);assert all(0<=v<=0x10ffff for v in seq)
   ordered.append(seq);actual.add(seq);point_cursor+=l
  assert len(ordered)==len(set(ordered)) and ordered==sorted(ordered,key=lambda s:(-len(s),s))
  assert actual==source_strings[n],('strings',n)
  string_cursor+=c
 assert string_cursor*2==len(ss) and point_cursor==len(cp)
 fold={}
 for r in rows('ucd/CaseFolding.txt'):
  if r[1] in ['C','S']:fold[int(r[0],16)]=int(r[2],16)
 raw=ints('UnicodeFold');assert raw==[v for item in sorted(fold.items()) for v in item]
 for r in rows('ucd/SpecialCasing.txt'):
  if not r[4]:simple[int(r[0],16)]=[int(x,16) for x in r[3].split()]
 legacy={c:s[0] for c,s in simple.items() if c<=65535 and len(s)==1 and s[0]<=65535 and s[0]!=c and not(c>=128 and s[0]<128)}
 raw=ints('LegacyUppercase');assert raw==[v for item in sorted(legacy.items()) for v in item]
 mappings={'CategoryAlias':{a:c for (k,a),c in aliases.items() if k=='gc'},'ScriptAlias':{a:c for (k,a),c in aliases.items() if k=='sc'},'BinaryAlias':admitted_aliases['binary'],'StringAlias':{n:n for n in string_names}}
 vectors=[]
 for method,expected in mappings.items():
  body=text.split(f'{method}(ReadOnlySpan<char> value)')[1].split('\n    }\n')[0]
  pairs=re.findall(r'if \(value.SequenceEqual\("([^"]+)"\)\)\s*\{\s*return "([^"]+)";\s*\}',body)
  assert len(pairs)==len(expected) and dict(pairs)==expected,method
  kinds=['Script','ScriptExtensions'] if method=='ScriptAlias' else [{'CategoryAlias':'GeneralCategory','BinaryAlias':'Binary','StringAlias':'String'}[method]]
  for kind in kinds:vectors.extend((kind,a,c) for a,c in expected.items())
 inventory=(REPO/f'test/GregsEx.Tests/Flavors/EcmaScript/Unicode/{inventory_class}.g.cs').read_text()
 assert f'internal static class {inventory_class}\n' in inventory
 inventory_aliases=re.findall(r'\("([^"]+)","([^"]+)","([^"]+)"\)',inventory)
 assert sorted(inventory_aliases)==sorted(vectors)
 boundaries=re.findall(r'\("([^"]+)","([^"]+)",(-?\d+),(-?\d+),(\d+)\)',inventory)
 assert len(boundaries)==len(directory)
 for kind,name,first,last,count in boundaries:
  members=emitted(kind,name)
  assert (int(first),int(last),int(count))==(min(members) if members else -1,max(members) if members else -1,directory[kind,name][1])
 image=4*(len(iv)+len(ss)+len(cp)+2*len(fold)+2*len(legacy))
 literal_names=set().union(*(set(m)|set(m.values()) for m in mappings.values()))
 literal_names.update(admitted_aliases['nonbinary']);literal_names.update(admitted_aliases['nonbinary'].values())
 literal_names.update([f'Unicode-{version}.0.0+Emoji-{version}.0','ID_Start','ID_Continue'])
 literal_bytes=sum(32+2*len(n) for n in literal_names)
 assert int(re.search(r'StaticImageBytes = (\d+);',text)[1])==image
 assert int(re.search(r'LiteralBytes = (\d+);',text)[1])==literal_bytes
 metadata=json.loads((folder/'outputs.json').read_text())
 assert metadata['data_id']==f'Unicode-{version}.0.0+Emoji-{version}.0'
 assert metadata['generator_sha256']==hashlib.sha256((ROOT/'generate.py').read_bytes()).hexdigest()
 assert metadata['input_manifest_sha256']==hashlib.sha256((folder/'inputs.sha256.json').read_bytes()).hexdigest()
 expected_counts={'properties':len(directory),'binary_properties':len(binary),'category_values':len(gc_names),'script_values':len(scripts),'aliases':len(vectors),'intervals':len(iv)//2,'finite_strings':len(ss)//2,'string_codepoints':len(cp),'unicode_fold':len(fold),'legacy_uppercase':len(legacy),'static_image_bytes':image,'literal_bytes':literal_bytes,'string_properties':{n:len(s) for n,s in source_strings.items()}}
 assert metadata['counts']==expected_counts
 paths={f'src/GregsEx/Flavors/EcmaScript/Unicode/{table_class}.cs',f'test/GregsEx.Tests/Flavors/EcmaScript/Unicode/{inventory_class}.g.cs'}
 if version==16:paths.add('src/GregsEx/Flavors/EcmaScript/Unicode/Unicode-LICENSE.txt')
 assert set(metadata['outputs'])==paths
 for name,item in metadata['outputs'].items():
  raw=(REPO/name).read_bytes()
  assert len(raw)==item['length'] and hashlib.sha256(raw).hexdigest()==item['sha256'],name
 print(json.dumps({'data_id':f'Unicode-{version}.0.0+Emoji-{version}.0','audit':'passed','properties':len(directory),'binary':len(binary),'script_values':len(scripts),'string_properties':{n:len(s) for n,s in sorted(source_strings.items())},'unicode_fold':len(fold),'legacy_uppercase':len(legacy),'static_image_bytes':image,'literal_bytes':literal_bytes,'generated_sha256':hashlib.sha256(text.encode()).hexdigest()},sort_keys=True))

if __name__=='__main__':
 for version,folder in [(16,ROOT),(17,ROOT/'17.0.0')]:audit(version,folder)
