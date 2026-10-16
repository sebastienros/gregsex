"""Reproducible fixed corpus generation. Expected values come from recipes/UCD only."""
from common import *
import re
cases=[]; witnesses=[]
def add(name,cap,p,s,want,role='boundary',clause='G',source=XSD,kind='Utf16',operation='Membership',
        status=None,error=None,provenance='Specification',extra=None):
    native=units(s) if kind=='Utf16' else list(s.encode('utf-8'))
    outcome=status or ('Match' if want else 'NoMatch')
    c=dict(id='XSD-'+name,profileId='Xsd11',capabilities=cap if isinstance(cap,list) else [cap],
      provenance=dict(kind=provenance,uri=source,clause=clause,license=MIT if provenance!='Imported' else 'MIT; Copyright2016-2026 SISSA; retained elementpath-LICENSE',oracleId=ORACLE_ID if provenance=='Imported' else None),
      patternKind='Utf16',patternUnits=units(p),inputKind=kind,inputUnits=native,absentInput=False,flags='',
      operation=operation,startAt=0,destinationLength=None,replacementUnits=None,limits=None,
      permitted=[expected(outcome,len(native),error)])
    if extra: c.update(extra)
    cases.append(c); witnesses.append(dict(id=c['id'],role=role,clause=clause))
# Independently specified grammar and whole-value witnesses (all productions64-98 mapped below).
for n,p,s,v,r in [('empty-empty','','',True,'positive'),('empty-nonempty','','a',False,'negative'),
 ('literal-hit','a','a',True,'positive'),('literal-prefix','a','ba',False,'negative'),
 ('literal-terminal-lf','a','a\n',False,'boundary'),('alt-empty','a|','',True,'boundary'),
 ('concat','ab','ab',True,'positive'),('branch','a|bc','bc',True,'positive'),
 ('branch-tail','a|bc','bca',False,'negative'),('group','(a|b)c','bc',True,'positive')]:
 add(n,['XS-001','XS-002','XS-012'],p,s,v,r,'G.1-G.3')
for q,accepted,rejected in [('?', ['','a'], ['aa']),('*',['','a','aaa'],['b']),('+',['a','aaa'],['']),
 ('{0}',[''],['a']),('{2}',['aa'],['a','aaa']),('{2,}',['aa','aaa'],['a']),
 ('{2,3}',['aa','aaa'],['a','aaaa']),('{02,03}',['aa','aaa'],['a'])]:
 for i,s in enumerate(accepted): add('quant-'+str(len(cases)),'XS-002','a'+q,s,True,'positive' if i==0 else 'boundary','G.2 productions67-71')
 for s in rejected: add('quant-'+str(len(cases)),'XS-002','a'+q,s,False,'negative','G.2 productions67-71')
for p,s,v,r in [('^a$','^a$',True,'positive'),('^a$','a',False,'negative'),('^+','^^',True,'boundary'),('$','$',True,'boundary')]:
 add('anchors-'+str(len(cases)),'XS-003',p,s,v,r,'G.3')
classes=[('[a-z-[aeiou]]+','bcdf',True),('[a-z-[aeiou]]+','be',False),
 ('[a-z-[aeiou-[e]]]','e',True),('[a-z-[aeiou-[e]]]','a',False),
 ('[^a-[b]]','c',True),('[^a-[b]]','a',False),('[^a-[b]]','b',False),
 ('[a-[a]]','a',False),('[a-[a]]','',False),('[a-[a]]*','',True),
 ('[a-b-c]','-',True),('[a-b-c]','b',True),('[a-b-c]','d',False),
 ('[-ab]','-',True),('[ab-]','-',True),('[a^]','^',True),('[^a]','a',False),
 ('[\\^-z]','a',True),('[^a]','\U0001f600',True),('[😀-😁]','😁',True),
 ('[😀-😁]','😂',False),('[a-a]','a',True),('[a-a]','b',False),
 ('[a\\-c]','-',True),('[a\\-c]','b',False),('[\\t-\\n]','\n',True)]
for p,s,v in classes: add('class-'+str(len(cases)),'XS-004',p,s,v,'positive' if v else 'negative','G.4.1 productions75-82')
add('class-boundary','XS-004','[a-a]','a',True,'boundary','G.4.1 range equality')
# Every admitted single escape, complements and singleton behavior.
for esc,ch in [('n','\n'),('r','\r'),('t','\t')]+[(c,c) for c in '|.-^?*+{}()[]\\']:
 for s,v in [(ch,True),('aa',False),('',False)]: add('escape-'+str(len(cases)),'XS-010','\\'+esc,s,v,'positive' if v else 'boundary','G.4.2.1 production84')
for p,ss in [('.', [('a',True),('\n',False),('\r',False),('\U0010ffff',True)]),
 ('\\s', [(c,True) for c in '\t\n\r ']+[('\u00a0',False)]),
 ('\\S',[('a',True),(' ',False),('',False)]),('\\d',[('1',True),('\u0661',True),('a',False)]),
 ('\\D',[('a',True),('1',False),('',False)]),('\\w',[('a',True),('_',False),('\U0001f600',True),('\u0378',False)]),
 ('\\W',[('_',True),('a',False),('\u0378',True),('',False)])]:
 for s,v in ss: add('shortcut-'+str(len(cases)),'XS-005',p,s,v,'positive' if v else 'negative','G.4.2.5 production97-98')
# NameStart/NameChar bounds from XML Fifth Edition, no ambient Unicode.
start=[(58,58),(65,90),(95,95),(97,122),(0xc0,0xd6),(0xd8,0xf6),(0xf8,0x2ff),
 (0x370,0x37d),(0x37f,0x1fff),(0x200c,0x200d),(0x2070,0x218f),(0x2c00,0x2fef),
 (0x3001,0xd7ff),(0xf900,0xfdcf),(0xfdf0,0xfffd),(0x10000,0xeffff)]
continuation=start+[(45,46),(48,57),(0xb7,0xb7),(0x300,0x36f),(0x203f,0x2040)]
for esc,rs in [('i',start),('c',continuation)]:
 cps=sorted({c for a,b in rs for c in [a-1,a,b,b+1] if 0<=c<=0x10ffff and xmlchar(c)})
 for c in cps:
  inside=any(a<=c<=b for a,b in rs)
  for e,v in [(esc,inside),(esc.upper(),not inside)]:
   add('name-'+e+'-'+str(c),'XS-007','\\'+e,chr(c),v,'positive' if v else 'negative','XML productions4/4a; G.4.2.5',XML)
 for s,v,r in [('',False,'boundary'),('aa',False,'boundary')]: add('name-single-'+str(len(cases)),'XS-007','\\'+esc,s,v,r)
# Parse UCD First/Last and Cn independently, verify byte pins.
assert sha(DATA/'inputs/UnicodeData.txt')=='ff58e5823bd095166564a006e47d111130813dcf8bf234ef79fa51a870edb48f'
assert sha(DATA/'inputs/Blocks.txt')=='f3907b395d410f1b97342292ca6bc83dd12eb4b205f2a0c48efdef99e517d7b0'
category=['Cn']*0x110000; first=None
for line in (DATA/'inputs/UnicodeData.txt').read_text().splitlines():
 f=line.split(';'); cp=int(f[0],16)
 if f[1].endswith(', First>'): first=(cp,f[2])
 elif f[1].endswith(', Last>'):
  assert first and first[1]==f[2]
  category[first[0]:cp+1]=[f[2]]*(cp-first[0]+1); first=None
 else: category[cp]=f[2]
assert first is None
cats=sorted(set(category)-{'Cs'})+list('LMNPSZC')
for cat in cats:
 inside=lambda cp: category[cp].startswith(cat) if len(cat)==1 else category[cp]==cat
 positive=[cp for cp in range(0x110000) if xmlchar(cp) and inside(cp)]
 for cp in sorted({positive[0],positive[-1],next(c for c in range(0x20,0x110000) if xmlchar(c) and not inside(c))}):
  for esc in ['p','P']:
   v=inside(cp) if esc=='p' else not inside(cp)
   add('cat-'+cat+'-'+esc+'-'+str(cp),'XS-006','\\'+esc+'{'+cat+'}',chr(cp),v,'positive' if v else 'negative','G.4.2.2 productions85-95; Unicode16 UnicodeData.txt')
 # Explicit empty and multiscalar boundary for every category.
 add('cat-'+cat+'-empty','XS-006','\\p{'+cat+'}','',False,'boundary','G.4.2.2')
# Newly assigned16, implicit Cn, First/Last and supplementary witnesses.
for cp in [0x1c89,0x1cc00,0x10d40,0x16100,0x2ebf0,0x3400,0x4dbf,0x10ffff,0x378,0xe000,0xf0000,0x100000]:
 for cat in cats:
  v=category[cp].startswith(cat) if len(cat)==1 else category[cp]==cat
  add('data-'+str(cp)+'-'+cat,'XS-006','\\p{'+cat+'}',chr(cp),v,'boundary','Unicode16 UnicodeData.txt; G.4.2.2')
blocks=[]
for line in (DATA/'inputs/Blocks.txt').read_text().splitlines():
 line=line.split('#')[0].strip()
 if not line: continue
 r,n=line.split(';'); a,b=[int(c,16) for c in r.strip().split('..')]
 blocks.append((re.sub(r'[\s_]','',n.strip()),[(a,b)]))
blocks += [('Greek',[(0x370,0x3ff)]),('CombiningMarksforSymbols',[(0x20d0,0x20ff)]),
 ('PrivateUse',[(0xe000,0xf8ff),(0xf0000,0xffffd),(0x100000,0x10fffd)])]
for name,rs in blocks:
 for cp in sorted({cp for a,b in rs for cp in [a-1,a,b,b+1] if 0<=cp<=0x10ffff and xmlchar(cp)}|{0x20}):
  for esc in ['p','P']:
   v=any(a<=cp<=b for a,b in rs); v=v if esc=='p' else not v
   add('block-'+name+'-'+esc+'-'+str(cp),'XS-006','\\'+esc+'{Is'+name+'}',chr(cp),v,'positive' if v else 'negative','G.4.2.3 production96; Unicode16 Blocks.txt')
for name in ['Unknown','NoBlock','basiclatin','BasicLatinX']:
 for esc in ['p','P']:
  for s,v,r in [('a',True,'positive'),('\U0010ffff',True,'boundary'),('',False,'negative'),('ab',False,'negative')]:
   add('unknown-'+str(len(cases)),'XS-008','\\'+esc+'{Is'+name+'}',s,v,r,'G.4.2.4 unknown-block default')
# Illegal grammar; locations deliberately exact reduced offending source span.
invalid=[('[]',0,2),('[^]',0,3),('[z-a]',1,3),('[a-\\d]',2,3),('[a-z-[b]c]',8,1),
 ('[a&&[b]]',4,1),('[a-[b]-[c]]',7,1),('a{3,2}',1,5),('a{,2}',1,4),('a{1,2,3}',1,7),
 ('*',0,1),('a**',2,1),('a+?',2,1),('a++',2,1),('a{1}?',4,1),('(?:a)',1,2),('(?=a)',1,2),
 ('(?<n>a)',1,3),('(a)\\1',3,2),('\\x41',0,2),('\\u0041',0,2),('\\0',0,2),('\\a',0,2),
 ('\\b',0,2),('\\',0,1),('(',0,1),('a)',1,1),('\\p{Cs}',3,2),('\\p{Letter}',3,6),
 ('\\p{Is}',3,2),('\\p{IsBasic_Latin}',10,1),('\\p{lu}',3,2),('\\p{Lx}',3,2),('\\p{IsBasic Latin}',10,1),('[\\]',1,1),
 ('[a-\\]',3,1),('\\pL',0,2)]
for i,(p,off,l) in enumerate(invalid):
 add('invalid-'+str(i),['XS-010','XS-002','XS-004','XS-006'],p,'a',None,'negative','G productions64-98',
     operation='Parse',status='InvalidSyntax',error=failure('InvalidSyntax',offset=off,length=l))
# Contract seed retained exactly, independently authored/ratified before workers.
seed=load(ROOT/'test/GregsEx.Tests/Contracts/Xml/ContractVectors.json')
for c in seed['cases']:
 if c['profileId']=='Xsd11':
  c=dict(c); c['id']='SEED-'+c['id']; cases.append(c); witnesses.append(dict(id=c['id'],role='boundary',clause=c['provenance']['clause']))
add('native-success-utf16','XS-009','.', '😀',True,'positive','XML production2/nativeUTF16',XML)
add('native-success-utf8','XS-009','.', '😀',True,'positive','XML production2/nativeUTF8',XML,kind='Utf8')
# Native domain/representation probes, not a schema-wrapper oracle's domain.
for kind in ['Utf16','Utf8']:
 for cp in [0,1,8,11,12,14,31,0xfffe,0xffff]:
  add('domain-'+kind+'-'+str(cp),'XS-009','a', 'a'+chr(cp),None,'negative','XML production2; full-subject validation',XML,
   kind,status='InvalidInput',error=failure('InvalidInputDomain','Input',1,1,kind))
 for cp in [9,10,13,32,0xd7ff,0xe000,0xfffd,0x10000,0xeffff,0xf0000,0x10ffff]:
  add('domain-'+kind+'-'+str(cp),'XS-009','[\\s\\S]',chr(cp),True,'boundary','XML production2',XML,kind)
 for inp in ([[0xd800],[97,0xdc00],[0xdbff]] if kind=='Utf16' else [[0x80],[97,0xc0,0xaf],[0xed,0xa0,0x80],[0xf4,0x90,0x80,0x80],[0xf0,0x9f]]):
  i=1 if inp[0]==97 else 0
  add('malformed-'+kind+'-'+str(len(cases)),'XS-009','a','',None,'boundary','strict scalar encoding',XML,kind,
    status='InvalidInput',error=failure('InvalidEncoding','Input',i,1,kind),extra={'inputUnits':inp})
 add('raw-'+kind,'XS-009','a','a',None,'negative','XML representation matrix',XML,kind,
  status='UnsupportedRepresentation',error=failure('UnsupportedRepresentation',length=1),extra={'inputKind':'Bytes','inputUnits':[97]})
add('finite-work-success','XS-011','a','a',True,'positive','GregsEx finite default work policy')
add('finite-work-boundary','XS-011','','',True,'boundary','GregsEx finite default work policy')
# Limits/lifecycle recipes retain portable frozen fields; actual engine checks are integration-owned.
for n,p,s,limits,status in [('compile-limit','a{1000}','',{'maxCompileWork':1},'LimitExceeded'),
 ('work-limit','(a|aa)*b','a'*100,{'maxWork':1},'LimitExceeded'),
 ('cancel-empty','','',{'cancelled':True},'Cancelled')]:
 all_limits=dict(maxWork=None,maxCompileWork=None,frameCapacity=None,maxResults=None,maxOutputUnits=None,maxHostMemoryBytes=None,cancelled=False); all_limits.update(limits)
 add(n,'XS-011',p,s,None,'negative','GregsEx finite resource policy',status=status,
 error=failure('CompileWorkLimitExceeded' if n=='compile-limit' else 'Cancelled' if n=='cancel-empty' else 'WorkLimitExceeded',
   'Pattern' if n=='compile-limit' else 'Input',length=0),extra={'limits':all_limits,'operation':'Parse' if n=='compile-limit' else 'Membership'})
for p,s,v in [('(a)','a',True),('(a)','b',False),('()','',True)]:
 add('group0-'+str(len(cases)),'XS-012',p,s,v,'positive' if v else 'negative','G.1/G.3; GregsEx group0',operation='FindCaptures',extra={'destinationLength':1})

for cat in cats:
 cp=next(c for c in range(0x110000) if xmlchar(c) and category[c]==cat) if len(cat)==2 else next(c for c in range(0x110000) if xmlchar(c) and category[c].startswith(cat))
 for pattern,v in [('\\w',category[cp][0] not in 'PZC'),('[\\w]',category[cp][0] not in 'PZC'),('\\W',category[cp][0] in 'PZC'),('[\\W]',category[cp][0] in 'PZC')]:
  add('word-category-'+str(len(cases)),'XS-005',pattern,chr(cp),v,'boundary','G.4.2.5; ratifiedD2; Unicode16')
for p in ['[\\s]','[\\S]']:
 for cp in [9,10,13,32,0xa0,0x2028]:
  v=cp in (9,10,13,32); v=v if p=='[\\s]' else not v
  add('space-class-'+str(len(cases)),'XS-005',p,chr(cp),v,'boundary','G.4.2.5')
for i,p in enumerate(['\x00','\ud800','a\ufffe']):
 off=1 if p.startswith('a') else 0
 add('pattern-domain-'+str(i),'XS-009',p,'a',None,'negative','XML pattern scalar domain',XML,operation='Parse',
  status='InvalidSyntax',error=failure('InvalidPatternEncoding',offset=off,length=1))

# Independent MIT upstream cases, retain exact method and test source identity.
imports=[('test_occurrences_qualifiers','#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?',
 [('#F3D',True),('#F3DA30',True),('#F3D\n',False),('#F3',False),('#F3D ',False),('F3D',False),('',False)]),
 ('test_or_operator','0|1',[('0',True),('1',True),('1\n',False),('',False),('2',False),('01',False),('1\n ',False)]),
 ('test_or_operator',r'\d+[%]|\d*\.\d+[%]',[('99%',True),('99.9%',True),('.90%',True),('%',False),('90.%',False)]),
 ('test_or_operator','([ -~]|\n|\r|\t)*',[('ciao\t-~ ',True),('\r\r',True),('\n -.abc',True),('à',False),('\t\n à',False)])]
for method,p,ss in imports:
 for s,v in ss: add('import-'+str(len(cases)),['XS-001','XS-002','XS-004','XS-010'],p,s,v,'positive' if v else 'negative',method,
  'https://github.com/sissaschool/elementpath/blob/300f8bc21e1323294c756d8977a35cfa606e8f30/tests/test_regex.py',provenance='Imported')
assert len({c['id'] for c in cases})==len(cases)
save(DATA/'corpus.json',dict(contractVersion='xml-fixtures-1',dataId='Unicode-16.0.0+XML-1.0-5e',cases=cases))
production_caps={**{i:'XS-002' for i in range(64,73)},73:'XS-003',74:'XS-004',**{i:'XS-004' for i in range(75,83)},83:'XS-005',84:'XS-010',**{i:'XS-006' for i in range(85,97)},97:'XS-007',98:'XS-005'}
production_witnesses={str(i):dict(capability=cap,caseIds=[c['id'] for c in cases if cap in c['capabilities']]) for i,cap in production_caps.items()}
save(DATA/'coverage.json',dict(format='xsd-coverage-1',capabilities={f'XS-{i:03}':[w for c,w in zip(cases,witnesses) if f'XS-{i:03}' in c['capabilities']] for i in range(1,13)},
 productionWitnesses=production_witnesses,productionGroups={'64-66':'G regex/branch/piece','67-72':'G quantifier and atom','73-82':'G character/class expression, range, subtraction',
 '83-88':'G class/single/category escapes','89-95':'G exact category codes (Cs excluded by contract)','96':'G block escape','97-98':'G dot and multichar escapes'},
 categoryNames=cats,blockNames=[b[0] for b in blocks],sourceOnlyObligations=['native invalid input/representation','source diagnostic spans','limits/cancellation/workspace admission','GregsEx group0/lifecycle'],
 downstream='Product tests execute this corpus; this adapter runs the independent processor only'))
print(f'Generated {len(cases)} exact native fixtures; {len(cats)} category spellings; {len(blocks)} block names/aliases.')

# Diagnostic spans are source witnesses, never offsets inferred from processor messages.
annotations=[]
for c in cases:
 for e in c['permitted']:
  err=e['error']
  if err is None: continue
  mode='FrozenExactContract' if c['id'].startswith('SEED-') else 'FirstOffendingNativeOffset' if err['location']=='Input' and err['code'] in ['InvalidEncoding','InvalidInputDomain'] else 'SourceArgumentWitness'
  native=c['patternUnits'] if err['location']=='Pattern' else c['inputUnits']
  annotations.append(dict(id=c['id'],coordinateAuthority=mode,offsetUnit=err['offsetUnit'],sourceWitness=err,implicatedArgumentUnits=native,
   oracleDerivedCoordinate=False,productCoordinateValidation='Required: compare frozen seed exactly; native failures require first offending offset. Other new witnesses identify implicated source spans, not a specification-mandated parser cursor/length; integration must add exact product-coordinate tests.'))
save(DATA/'diagnostic-provenance.json',dict(format='xsd-diagnostic-provenance-1',cases=annotations))
