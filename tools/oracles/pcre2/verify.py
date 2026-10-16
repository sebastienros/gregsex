"""Focused source/protocol/semantic/reproducibility verification, no product calls."""
from pathlib import Path
import collections,gzip,hashlib,json,subprocess,tarfile,tempfile
from cases import ROOT,HERE,header
OUT=ROOT/'test/corpora/pcre2/oracle';CORPUS=ROOT/'test/corpora/pcre2';BUILD=HERE/'.artifacts/build';SRC=HERE/'.artifacts/source/src'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
 output=ROOT/'artifacts/conformance/pcre2';output.mkdir(parents=True,exist_ok=True)
 revision=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
 sm=json.loads((ROOT/'tools/oracles/pcre2/source/source-manifest.json').read_text());assert sha(ROOT/'tools/oracles/pcre2/source/pcre2-10.46-source.tar.gz')==sm['archive_sha256']
 with tarfile.open(ROOT/'tools/oracles/pcre2/source/pcre2-10.46-source.tar.gz') as z:
  members={m.name.split('/',1)[1]:m for m in z.getmembers() if m.isfile()}
  assert len(members)==488
  for m in sm['files']:assert hashlib.sha256(z.extractfile(members[m['path']]).read()).hexdigest()==m['sha256']
 # Frozen contract expectations/schema stay byte-identical to the verified base.
 for p in ['test/GregsEx.Tests/Contracts/Pcre2/ContractVectors.json','test/GregsEx.Tests/Contracts/Pcre2/Pcre2Fixtures.schema.json']:
  original=subprocess.check_output(['git','show','ecf4fad5a419eee4a5f77b8d906daa31e036f642:'+p],cwd=ROOT);assert original==(ROOT/p).read_bytes()
 inputs=json.loads((CORPUS/'inputs.json').read_text())['cases'];obs=json.loads((CORPUS/'observations.json').read_text())['cases'];by={c['id']:c for c in inputs};outputs={o['id']:o for o in obs};assert len(by)==len(inputs)==len(outputs)==len(obs)==1050
 allids={r for c in inputs for r in c['requirements']};assert allids>={f'PC-{i:03}' for i in range(1,25)}|{f'PH-{i:03}' for i in range(1,13)}
 defs=json.loads((ROOT/'tools/oracles/pcre2/source/source-observations.json').read_text())['definitions'];options={d['name'] for d in defs if d['category'] in ['compile','common_compile_match','compile_extra']}
 for option in options:
  cohorts={(c['width'],bool(c['compileFlags']&0x80000)) for c in inputs if c.get('optionName')==option}
  # UTF and MATCH_INVALID_UTF enable UTF independently; IDs retain the original
  # raw/UTF cohorts instead of mistaking effective domain for an absent cross.
  for width in [8,16]:
   for domain in ['raw','utf']:assert any(c.get('optionName')==option and f'{width}-{domain}' in c['id'] for c in inputs)
 assert len(json.loads((OUT/'upstream-supplement.json').read_text())['records'])==115
 for case in obs:
  for e in case['events']:
   if e['event']=='match':
    rc=e['returnCode'];assert e['markDefined']==(rc>=0 or rc in [-1,-2])
    if -26<=rc<=-3:assert e['errorOffset'] is not None and e['startChar']==e['errorOffset']
  if case['id'].startswith('binary-mark'):
   m=next(e for e in case['events'] if e['event']=='match')
   assert m['markUnits']==([65,0,66] if m['returnCode'] in [1,-1] else None)
  if case['id'].startswith('binary-callout'):assert any(e['event']=='callout' and e['isString'] and e['stringUnits']==[0] for e in case['events'])
 events=collections.Counter(e['event'] for o in obs for e in o['events'])
 for c in inputs:
  oo=outputs[c['id']];ee=oo['events']
  if c['operation'] in ['Projection','ExcludedHost']:assert [e['event'] for e in ee]==['boundary'];continue
  comp=[e for e in ee if e['event']=='compile'];assert len(comp)==1
  if not comp[0]['accepted']:assert comp[0]['error']!=0 and comp[0]['sourceOffset'] is not None;continue
  assert all(any(e['event']==t for e in ee) for t in ['info','names'])
  for e in ee:
   if e['event']=='match':
    if e['returnCode']==-2:assert len(e['pairs'])==1 and e['callerVector'] is None
    if e['returnCode']>=0:assert 1<=len(e['pairs'])<=c['pairCapacity']
   if e['event']=='callout':
    assert all(k in e for k in ['version','number','isString','subjectUnits','subjectLength','captureTop','captureLast','startMatch','currentPosition','patternPosition','nextItemLength','stringOffset','stringUnits','flags','returnCode','markUnits','captures'])
    assert e['captures'][0] is None and len(e['captures'])==e['captureTop']
   if e['event']=='substituteCallout':assert all(k in e for k in ['version','count','outputStart','outputEnd','proposedOutput','captures','returnCode'])
   if e['event']=='caseCallout':assert all(k in e for k in ['mode','inputUnits','outputUnits','outputCapacity','requiredUnits','overlap'])
 def event(id,kind):return next(e for e in outputs[id]['events'] if e['event']==kind)
 assert event('PCRE-C028','match')['pairs']==[[2,0]]
 assert event('PCRE-C043','match')['returnCode']==0
 assert event('PCRE-C046','match')['pairs']==[[0,1],[None,None],[0,1]]
 assert event('PCRE-C044','match')['callerVector']==[[99,100]]
 assert event('PCRE-C052','match')['returnCode']==-1000
 assert event('PCRE-C055','match')['returnCode']==-47
 assert event('PCRE-C056','match')['returnCode']==-24 and event('PCRE-C056','match')['errorOffset']==0
 assert event('PCRE-C057','compile')['nativeOffset']==5 and event('PCRE-C057','compile')['sourceOffset']==3
 assert event('PCRE-C041','match')['returnCode']==1 and event('PCRE-C042','match')['returnCode']==-1
 assert [n['number'] for n in event('name-order-8','names')['entries']]==[2,1]
 assert [n['number'] for n in event('name-order-16','names')['entries']]==[1,2]
 assert any(e['event']=='match' and e['returnCode']==-36 for e in outputs['split-unit-1015-8']['events'])
 assert any(e['event']=='match' and e['returnCode']==-36 for e in outputs['split-unit-1030-16']['events'])
 reduction=json.loads((OUT/'C048-reduction.json').read_text())['invocations'];assert len(reduction)==6
 for r in reduction:assert (r['returnCode'],r['outputUnits'])==((-54,[]) if r['matchData']=='explicit' and r['pairCapacity']==1 else (1,[97,97]))
 for width in [8,16]:assert sha(CORPUS/f'default-tables-{width}.bin')=='40bfdd1a0c1e45c7c85c799dfefa098d30ea58e832d813d00f6dfc6bed167513'
 acc=json.loads((CORPUS/'upstream-accounting.json').read_text());assert acc['memberCount']==106 and len(acc['unicodeInputs'])==13
 recipesbytes=gzip.decompress((CORPUS/'upstream-recipes.jsonl.gz').read_bytes());assert hashlib.sha256(recipesbytes).hexdigest()==acc['uncompressedRecipesSha256']
 recipes=[json.loads(l) for l in recipesbytes.splitlines()];assert len(recipes)==acc['recipeCount'];assert len({r['id'] for r in recipes})==len(recipes)
 assert len(json.loads((CORPUS/'controls.json').read_text())['modifiers'])==154
 am=json.loads((OUT/'artifact-manifest.json').read_text());assert sha(ROOT/am['archive'])==am['sha256']
 with tarfile.open(ROOT/am['archive']) as z:
  members={m.name:m for m in z.getmembers()};assert len(members)==len(am['members'])
  for m in am['members']:assert hashlib.sha256(z.extractfile(members[m['path']]).read()).hexdigest()==m['sha256']
  for r in recipes:
   if 'observation' in r:
    rr=r['observation'];assert rr['traceMember'] in members;assert rr['outputMember'] in members
 # Reuse full completed native source runs; execute only a bounded focused replay
 # of source-defined witnesses, with the exact frozen native libraries and code.
 selected=['PCRE-C001','PCRE-C028','PCRE-C029','PCRE-C032','PCRE-C035','PCRE-C041','PCRE-C042','PCRE-C043','PCRE-C044','PCRE-C046','PCRE-C048','PCRE-C051','PCRE-C052','PCRE-C054','PCRE-C055','PCRE-C056','PCRE-C057']
 focused=[by[id] for id in selected];raw_by={}
 with tarfile.open(ROOT/am['archive']) as z:
  for width in [8,16]:
   for line in z.extractfile(f'observer-{width}.jsonl').read().splitlines():
    e=json.loads(line);raw_by.setdefault(e['id'],[]).append(e)
  # Focused replay needs only the exact retained static libraries/header.
  # Rehydrate these owned scratch files without rebuilding or rerunning intake.
  BUILD.mkdir(parents=True,exist_ok=True);SRC.mkdir(parents=True,exist_ok=True)
  for width in [8,16]:
   p=BUILD/f'libpcre2-{width}.a';data=z.extractfile(f'native-build/libpcre2-{width}.a').read()
   if p.exists():assert p.read_bytes()==data
   else:p.write_bytes(data)
  p=SRC/'pcre2.h';data=z.extractfile('native-build/pcre2.h').read()
  if p.exists():assert p.read_bytes()==data
  else:p.write_bytes(data)
 with tempfile.TemporaryDirectory(prefix='pcre2-focused-') as tmp:
  for width in [8,16]:
   work=Path(tmp)/str(width);work.mkdir();header(focused,width,work/'cases.h')
   argv=['clang','-O2','-Wall','-Wextra',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I'+str(SRC),'-I'+str(work),str(HERE/'observe.c'),str(BUILD/f'libpcre2-{width}.a'),'-o',str(work/'observe')]
   p=subprocess.run(argv,capture_output=True,timeout=60);assert p.returncode==0,p.stderr
   p=subprocess.run([work/'observe'],cwd=work,capture_output=True,timeout=60);assert p.returncode==0,p.stderr
   (output/f'focused-{width}.jsonl').write_bytes(p.stdout)
   actual={}
   for line in p.stdout.splitlines():e=json.loads(line);actual.setdefault(e['id'],[]).append(e)
   for id,ee in actual.items():
    expected=[dict(e) for e in outputs[id]['events']]
    for e in expected:e.pop('sourceOffset',None)
    assert ee==expected,id
 report={'testedRevision':revision,'sourceMembers':488,'testdataMembers':106,'unicodeInputs':13,'structuredCases':len(inputs),'inventoryIds':36,'nativeOptionIdentities':len(options),'referenceModifiers':154,'upstreamRecipes':len(recipes),'nativeSourceDispatches':acc['uniqueNativeSourceDispatches'],'nativeStructuredEvents':dict(events),'focusedNativeReplays':len(selected),'frozenFixtureBytesUnchanged':True,'artifactMembers':len(am['members']),'artifactSha256':am['sha256'],'status':'passed','limitations':['No GregsEx comparison or engine verification','Native physical heap-frame size diffs retained/excluded','C048 explicit-vector/NULL invocation discrepancy documented in test/corpora/pcre2/C048-erratum.md; original seed immutable','UTF malformed continuation after split-unit C is source undefined; excluded equality, raw observations retained','This oracle replay does not establish .NET NativeAOT compatibility','No allocation/performance/RID claims']}
 (output/'focused-checks.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
if __name__=='__main__':main()
