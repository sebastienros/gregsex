"""Fixed-input Unicode16/17 and Emoji16/17 generator. No network, host Unicode or CLI parser.
Run from any directory with python3 tools/unicode/ecmascript/generate.py.
All inputs must match inputs.sha256.json before any output is written.
"""
from pathlib import Path
import json, hashlib, zipfile
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
OUT=REPO/'src/GregsEx/Flavors/EcmaScript/Unicode'
TEST=REPO/'test/GregsEx.Tests/Flavors/EcmaScript/Unicode'
MAX=0x10ffff

def digest(b): return hashlib.sha256(b).hexdigest()
def span(s):
 a=s.split('..');return (int(a[0],16),int(a[-1],16))
def merge(xs):
 out=[]
 for a,b in sorted(xs):
  assert 0<=a<=b<=MAX
  if out and a<=out[-1][1]+1: out[-1]=(out[-1][0],max(b,out[-1][1]))
  else: out.append((a,b))
 return out

def subtract(xs,ys):
 out=[];j=0
 for a,b in xs:
  while j<len(ys) and ys[j][1]<a:j+=1
  k=j
  while k<len(ys) and ys[k][0]<=b:
   c,d=ys[k]
   if a<c:out.append((a,c-1))
   a=max(a,d+1)
   if a>b:break
   k+=1
  if a<=b:out.append((a,b))
 return out

def cs(s):return json.dumps(s,ensure_ascii=True)
def blob(name,values):
 # Primitive constant spans lower to static image data under .NET10; no arrays.
 result=f'    internal static ReadOnlySpan<int> {name} => [\n'
 for i in range(0,len(values),16):result+='        '+','.join(str(x) for x in values[i:i+16])+',\n'
 return result+'    ];\n'

# ECMA tables have rowspan canonical cells: following one-cell rows add aliases.
def rows(admitted,kind):
 d={};current=None
 for row in admitted[kind]:
  if len(row)==2:current=row[1][0];d[current]=list(row[0])
  else:
   assert current is not None
   d[current].extend(row[0])
 return d
def generate(version,folder):
 INPUT=folder/'inputs'
 def read(n):return (INPUT/n).read_text(encoding='utf-8')
 def lines(n):
  for line in read(n).splitlines():
   line=line.split('#')[0].strip()
   if line:yield [s.strip() for s in line.split(';')]
 def properties(n):
  d={}
  for row in lines(n):d.setdefault(row[1],[]).append(span(row[0]))
  return {k:merge(v) for k,v in d.items()}
 manifest=json.loads((folder/'inputs.sha256.json').read_text())
 actual={p.relative_to(INPUT).as_posix():{'length':len(p.read_bytes()),'sha256':digest(p.read_bytes())} for p in sorted(INPUT.rglob('*')) if p.is_file()}
 assert actual==manifest['files'], 'input digest/length/file inventory mismatch'
 if 'archive' in manifest['ucd_delivery']:
  delivery=manifest['ucd_delivery'];archive=folder/delivery['archive']
  raw=archive.read_bytes()
  assert len(raw)==delivery['length'] and digest(raw)==delivery['sha256']
  with zipfile.ZipFile(archive) as source:
   for name in actual:
    if name.startswith('ucd/'):assert source.read(name[4:])==(INPUT/name).read_bytes(),name
 assert f'Version {version}.0.0' in read('ucd/ReadMe.txt')
 assert all(f'# Version: {version}.0' in read(n) for n in ['emoji-sequences.txt','emoji-zwj-sequences.txt'])
 # Both datasets use the exact same ECMA-262 edition16 admission whitelist.
 assert (INPUT/'ecma-property-tables.json').read_bytes()==(ROOT/'inputs/ecma-property-tables.json').read_bytes()
 assert (INPUT/'LICENSE.txt').read_bytes()==(ROOT/'inputs/LICENSE.txt').read_bytes()
 admitted=json.loads(read('ecma-property-tables.json'))
 binary_aliases=rows(admitted,'binary');nonbinary_aliases=rows(admitted,'nonbinary')
 string_names=[r[0][0] for r in admitted['string']]
 assert len(binary_aliases)==53 and len(string_names)==7
 pa={r[1]:r for r in lines('ucd/PropertyAliases.txt')}
 for name,aliases in binary_aliases.items():
  if name not in ['Any','ASCII','Assigned']:assert set(aliases)<=set(pa[name]),name
 alias_gc={};alias_sc={};short_to_sc={}
 for r in lines('ucd/PropertyValueAliases.txt'):
  if r[0] in ['gc','sc']:
   canonical=r[2]
   target=alias_gc if r[0]=='gc' else alias_sc
   for alias in r[1:]:target[alias]=canonical
   if r[0]=='sc':short_to_sc[r[1]]=canonical
 allprops={}
 gc=properties('ucd/extracted/DerivedGeneralCategory.txt')
 for short,canonical in sorted({a:c for a,c in alias_gc.items() if len(a)<=2}.items()):
  if short in gc:intervals=gc[short]
  elif short=='LC':intervals=merge(gc['Lu']+gc['Ll']+gc['Lt'])
  else:intervals=merge([v for k,xs in gc.items() if k.startswith(short) for v in xs])
  assert intervals,short
  allprops[('GeneralCategory',canonical)]=intervals
 scripts=properties('ucd/Scripts.txt')
 scripts['Unknown']=subtract([(0,MAX)],merge([v for xs in scripts.values() for v in xs]))
 for canonical in set(alias_sc.values()):allprops[('Script',canonical)]=scripts.get(canonical,[])
 scx=properties('ucd/ScriptExtensions.txt')
 overrides=merge([v for xs in scx.values() for v in xs])
 for canonical in set(alias_sc.values()):
  xs=subtract(scripts.get(canonical,[]),overrides)
  xs+= [v for names,rs in scx.items() if canonical in [short_to_sc[n] for n in names.split()] for v in rs]
  allprops[('ScriptExtensions',canonical)]=merge(xs)
 bs={}
 for n in ['PropList.txt','DerivedCoreProperties.txt','DerivedNormalizationProps.txt','extracted/DerivedBinaryProperties.txt','emoji/emoji-data.txt']:
  for k,v in properties('ucd/'+n).items():bs.setdefault(k,[]).extend(v)
 bs['Any']=[(0,MAX)];bs['ASCII']=[(0,127)];bs['Assigned']=subtract([(0,MAX)],gc['Cn'])
 for name in binary_aliases:
  assert name in bs,name
  allprops[('Binary',name)]=merge(bs[name])
 stringprops={n:set() for n in string_names}
 for n in ['emoji-sequences.txt','emoji-zwj-sequences.txt']:
  for r in lines(n):
   assert r[1] in stringprops,r[1]
   if '..' in r[0]:
    a,b=span(r[0]);stringprops[r[1]].update((i,) for i in range(a,b+1))
   else:stringprops[r[1]].add(tuple(int(c,16) for c in r[0].split()))
 stringprops['RGI_Emoji']=set().union(*stringprops.values())
 strings={};slices=[];codepoints=[]
 for name,seqs in sorted(stringprops.items()):
  # Length-one members belong in interval part, finite strings length>1 sorted longest first.
  allprops[('String',name)]=merge([(s[0],s[0]) for s in seqs if len(s)==1])
  ordered=sorted((s for s in seqs if len(s)>1),key=lambda s:(-len(s),s))
  off=len(slices)//2
  for s in ordered:slices.extend([len(codepoints),len(s)]);codepoints.extend(s)
  strings[name]=(off,len(ordered))
 uf={}
 for r in lines('ucd/CaseFolding.txt'):
  if r[1] in ['C','S']:
   vals=r[2].split();assert len(vals)==1
   uf[int(r[0],16)]=int(vals[0],16)
 upper={}
 for r in lines('ucd/UnicodeData.txt'):
  cp=int(r[0],16)
  if cp<=0xffff and r[12]:upper[cp]=tuple(int(c,16) for c in r[12].split())
 for r in lines('ucd/SpecialCasing.txt'):
  cp=int(r[0],16)
  if cp<=0xffff and not r[4]:upper[cp]=tuple(int(c,16) for c in r[3].split())
 legacy={cp:seq[0] for cp,seq in upper.items() if len(seq)==1 and seq[0]<=0xffff and cp!=seq[0] and not(cp>=128 and seq[0]<128)}
 assert uf[0x212a]==ord('k') and 0x212a not in legacy and 0xdf not in legacy and 0x130 not in uf
 assert all(uf.get(v,v)==v for v in uf.values())
 intervals=[];directory={}
 for key,xs in sorted(allprops.items()):
  directory[key]=(len(intervals)//2,len(xs))
  for a,b in xs:intervals.extend([a,b])
 table_class=f'GeneratedUnicode{"" if version==16 else version}Tables'
 inventory_class=f'Unicode{"" if version==16 else version}Inventory'
 data_id=f'Unicode-{version}.0.0+Emoji-{version}.0'
 header=f'// Generated by tools/unicode/ecmascript/generate.py; Unicode{version}.0.0 / Emoji{version}.0.\n// Unicode License V3: see Unicode-LICENSE.txt. Do not edit.\nnamespace GregsEx.Flavors.EcmaScript.Unicode;\n\ninternal static class {table_class}\n{{\n'
 code=header+blob('Intervals',intervals)+blob('StringSlices',slices)+blob('StringCodePoints',codepoints)+blob('UnicodeFold',[v for kv in sorted(uf.items()) if kv[0]!=kv[1] for v in kv])+blob('LegacyUppercase',[v for kv in sorted(legacy.items()) for v in kv])
 staticbytes=4*(len(intervals)+len(slices)+len(codepoints)+2*len(uf)+2*len(legacy))
 # Includes conservative string literal storage; alias/name strings are compiler metadata, not dynamically constructed.
 names=set(alias_gc)|set(alias_sc)|set(binary_aliases)|set(string_names)|set(nonbinary_aliases)
 for v in list(binary_aliases.values())+list(nonbinary_aliases.values()):names.update(v)
 names.update([data_id,"ID_Start","ID_Continue"])
 names.update(alias_gc.values());names.update(alias_sc.values())
 literalbytes=sum(32+2*len(n) for n in names)
 code+=f'    internal const long StaticImageBytes = {staticbytes};\n    internal const long LiteralBytes = {literalbytes};\n'
 code+='    internal static (int Offset, int Count) IntervalSlice(GregsEx.Contracts.EcmaScript.EcmaProperty p) => (p.Kind, p.CanonicalName) switch\n    {\n'
 for (kind,name),(off,count) in sorted(directory.items()):code+=f'        (GregsEx.Contracts.EcmaScript.EcmaPropertyKind.{kind}, {cs(name)}) => ({off}, {count}),\n'
 code+='        _ => throw new ArgumentException("Unknown Unicode property", nameof(p))\n    };\n'
 code+='    internal static (int Offset, int Count) StringSlice(string name) => name switch\n    {\n'
 for name,(off,count) in sorted(strings.items()):code+=f'        {cs(name)} => ({off}, {count}),\n'
 code+='        _ => throw new ArgumentException("Unknown string property", nameof(name))\n    };\n'
 # Span comparison fixed ordinal linear finite vocabulary baseline. No hash, host Unicode or copies.
 for method,mapping in [('CategoryAlias',alias_gc),('ScriptAlias',alias_sc),('BinaryAlias',{a:c for c,als in binary_aliases.items() for a in als}),('StringAlias',{n:n for n in string_names})]:
  code+=f'    internal static string? {method}(ReadOnlySpan<char> value)\n    {{\n'
  for alias,canonical in sorted(mapping.items()):code+=f'        if (value.SequenceEqual({cs(alias)}))\n        {{\n            return {cs(canonical)};\n        }}\n\n'
  code+='        return null;\n    }\n'
 code+='}\n'
 OUT.mkdir(parents=True,exist_ok=True);TEST.mkdir(parents=True,exist_ok=True)
 (OUT/f'{table_class}.cs').write_text(code)
 # License bytes are identical for both retained versions; do not rewrite runtime notices for17.
 if version==16:(OUT/'Unicode-LICENSE.txt').write_bytes((INPUT/'LICENSE.txt').read_bytes())
 # Runtime verification fixtures originate from pinned sources, including all exact aliases.
 vectors=[]
 for kind,mapping in [('GeneralCategory',alias_gc),('Script',alias_sc),('ScriptExtensions',alias_sc),('Binary',{a:c for c,als in binary_aliases.items() for a in als}),('String',{n:n for n in string_names})]:
  for a,c in sorted(mapping.items()):vectors.append((kind,a,c))
 test=f'// Generated source inventory; not a JS engine oracle.\nnamespace GregsEx.Tests.Flavors.EcmaScript.Unicode;\ninternal static class {inventory_class}\n{{\n    internal static (string Kind,string Alias,string Canonical)[] Aliases => [\n'
 for k,a,c in vectors:test+=f'        ({cs(k)},{cs(a)},{cs(c)}),\n'
 test+='    ];\n    internal static (string Kind,string Name,int First,int Last,int Count)[] Boundaries => [\n'
 for (kind,name),xs in sorted(allprops.items()):
  test+=f'        ({cs(kind)},{cs(name)},{xs[0][0] if xs else -1},{xs[-1][1] if xs else -1},{len(xs)}),\n'
 test+='    ];\n}\n'
 (TEST/f'{inventory_class}.g.cs').write_text(test)
 paths=[OUT/f'{table_class}.cs',TEST/f'{inventory_class}.g.cs']
 if version==16:paths.append(OUT/'Unicode-LICENSE.txt')
 outputs={p.relative_to(REPO).as_posix():{'length':len(p.read_bytes()),'sha256':digest(p.read_bytes())} for p in paths}
 summary={'data_id':data_id,'generator_sha256':digest(Path(__file__).read_bytes()),'input_manifest_sha256':digest((folder/'inputs.sha256.json').read_bytes()),'outputs':outputs,'counts':{'properties':len(allprops),'binary_properties':len(binary_aliases),'category_values':len(set(alias_gc.values())),'script_values':len(set(alias_sc.values())),'aliases':len(vectors),'intervals':len(intervals)//2,'finite_strings':len(slices)//2,'string_codepoints':len(codepoints),'unicode_fold':len(uf),'legacy_uppercase':len(legacy),'static_image_bytes':staticbytes,'literal_bytes':literalbytes,'string_properties':{n:len(s) for n,s in sorted(stringprops.items())}}}
 (folder/'outputs.json').write_text(json.dumps(summary,indent=2,sort_keys=True)+'\n')
 print(json.dumps({'data_id':data_id,**summary['counts']},sort_keys=True))

if __name__=='__main__':
 for version,folder in [(16,ROOT),(17,ROOT/'17.0.0')]:generate(version,folder)
