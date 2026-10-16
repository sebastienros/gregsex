"""Fixed-input artifact/coverage freeze; no tests or engine-generated goldens."""
import json,pathlib,hashlib
ROOT=pathlib.Path(__file__).resolve().parents[3];DIR=ROOT/'test/corpora/ecmascript'
def ident(p):
 b=p.read_bytes();return dict(path=p.relative_to(ROOT).as_posix(),length=len(b),sha256=hashlib.sha256(b).hexdigest())
recipes=json.load(open(DIR/'recipes.json'))['recipes'];rows=[]
for n in range(1,21):
 cap=f'ES-{n:03}';selected=[r for r in recipes if cap in r['capabilities']]
 rows.append({'requirementId':cap,'edition':'ECMAScript2025-edition16','clauses':sorted({r['clause'] for r in selected}) or ['GregsEx ECMAScript contract resource/lifetime policy'],'admittedModes':['legacy','u','v'] if n not in [10,11,12,14] else {10:['u','v'],11:['v'],12:['v'],14:['legacy']}[n],
 'positiveCases':[r['id'] for r in selected if r['kind']=='positive'],'negativeCases':[r['id'] for r in selected if r['kind']=='negative'],'boundaryCases':[r['id'] for r in selected if r['kind']=='boundary'],
 'portableResources':[c['id'] for c in json.load(open(DIR/'resource-corpus.json'))['cases'] if cap in c['capabilities']],
 'productExecution':'requires actual parser/VM/host execution; oracle preparation does not generate GregsEx engine expectations','remaining':'ES019/020 source category recipes require actual GregsEx bounded operations; no JS instruction-counter/AOT/lifetime oracle.' if n>=19 else 'Full normative grammar/algorithm inventory and all required fixtures must bind at integration; finite corpus does not prove full-profile parity.'})
# Global flag normalized/invalid recipes have explicit failure and empty boundary.
rows[0]['boundaryCases']=['ES-R-flag-none','ES-R-flag-dgimsuy','ES-R-flag-dgimsvy']
rows[0]['supplementalCases']=['ES-FLAG-SURROGATE']
(DIR/'coverage.json').write_text(json.dumps({'schemaVersion':'ecma-coverage-1','requirements':rows,'unicodeExhaustiveAudit':'Original Test262 generated Unicode16 property assertions execute in full-suite; verified GregsEx Unicode lane full-membership audit retained at97741fe. This lane pins its complete input/output/license manifests, without claiming engine binding.','hostOperations':['Exec','Test','Match','MatchAll','Search','Replace','ReplaceAll','Split'],'requiredEngineChecks':['UTF16-only representation rejection','lower-bound interior-pair failure distinct from builtinLastIndex','independent workspace concurrency/lifetime','limits/cancellation/deadline','warm allocation','NativeAOT'],'claim':'Pinned oracle preparation only; no complete released-profile conformance claim.'},indent=2)+'\n')
paths=set()
for tree in ['tools/oracles/ecmascript','test/corpora/ecmascript','test/GregsEx.Tests/Conformance/EcmaScript']:
 for p in (ROOT/tree).rglob('*'):
  if p.is_file() and '/test262/' not in p.as_posix() and p.name!='artifacts.json' and '__pycache__' not in p.as_posix():paths.add(p)
for name in ['test/GregsEx.Tests/Contracts/EcmaScript/ContractVectors.json','test/GregsEx.Tests/Contracts/EcmaScript/EcmaFixture.cs','test/GregsEx.Tests/Contracts/EcmaScript/EcmaFixtures.schema.json','tools/unicode/ecmascript/generate.py','tools/unicode/ecmascript/inputs.sha256.json','tools/unicode/ecmascript/outputs.json']:
 paths.add(ROOT/name)
inputs=json.load(open(ROOT/'tools/unicode/ecmascript/inputs.sha256.json'))
for key,expected in inputs['files'].items():
 p=ROOT/'tools/unicode/ecmascript/inputs'/key;actual=ident(p)
 if actual['length']!=expected['length'] or actual['sha256']!=expected['sha256']:raise ValueError('Unicode input identity mismatch '+key)
 paths.add(p)
outputs=json.load(open(ROOT/'tools/unicode/ecmascript/outputs.json'))
for key,expected in outputs['outputs'].items():
 p=ROOT/key;actual=ident(p)
 if actual['length']!=expected['length'] or actual['sha256']!=expected['sha256']:raise ValueError('Unicode output identity mismatch '+key)
 paths.add(p)
(DIR/'artifacts.json').write_text(json.dumps({'schemaVersion':'ecma-artifacts-1','unicodeVerifiedRevision':'97741fe2943726c3bb2a0eb79859234c9ffb33d1','files':[ident(p) for p in sorted(paths)]},indent=2)+'\n')
print('Frozen',len(paths),'artifact identities and20requirement rows')
