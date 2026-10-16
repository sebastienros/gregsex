"""Read-only audit replay: fail closed on changed/unreviewed/incomplete artifacts."""
import gzip,hashlib,json,re,tarfile
from collections import Counter
from common import ROOT,load,artifact,observed,comparison
from run import native_request
m=load('test/corpora/posix/manifest.json')
assert m['format']=='posix-conformance-manifest-1'
for a in m['artifacts']:
 assert not a['path'].startswith('/') and '..' not in a['path'].split('/'),a
 assert artifact(a['path'])==a, a['path']
for a in load('test/corpora/posix/sources/contract-lock.json')['files']:
 assert artifact(a['path'])==a,a['path']
cases=sum((load(l['corpus'])['cases'] for l in m['lanes']),[])
assert len(cases)==m['fixtureCount']==7265
assert len({c['id'] for c in cases})==len(cases)
index=load('test/corpora/posix/index.json')['cases']
assert len(index)==len(cases)
disagreements=load('test/corpora/posix/oracle/disagreements.json')
diffs={(d['id'],d['family']):d for d in disagreements['cases']}
assert len(diffs)==len(disagreements['cases'])==90
source=load('test/corpora/posix/oracle/source-only.json')
assert len(source)==m['native']['sourceOnlyCount']==21
requests=[]
for n,(c,e) in enumerate(zip(cases,index)):
 assert e['id']==c['id'] and e['ordinal']==n and e['diagnosticPolicy']=='exact'
 request,reason=native_request(c)
 if request:requests.append(request)
 else:assert source[c['id']]==reason
 for f in ('glibc','musl'):
  rule=e['oracles'][f]
  if rule['comparison']=='reviewed-difference':
   d=diffs[(c['id'],f)]
   assert d['rule']==rule['reason'] and d['rule'] in disagreements['rules']
   assert d['expected']==c['permitted'] and d['patternUnits']==c['patternUnits'] and d['inputUnits']==c['inputUnits']
  elif rule['comparison']=='source-only':assert c['id'] in source and rule['reason']==source[c['id']]
  else:assert rule==dict(comparison='agreement',reason=None)
with gzip.open(ROOT/'test/corpora/posix/oracle/native-requests.txt.gz','rt') as f:assert f.read()==''.join(requests)
assert len(requests)==m['native']['requestCount']==7244
for f in ('glibc','musl'):
 identity,table,rows=observed(f)
 assert identity['locale']=='C' and identity['oracle']==('musl-1.2.5' if f=='musl' else 'glibc-debian-bookworm')
 assert len(rows)==len(requests) and set(rows)=={r.split()[0] for r in requests}
 assert table['rows']==[row[:3] for row in load('test/GregsEx.Tests/Contracts/Posix/LocaleTable.json')['rows']]
 for c,e in zip(cases,index):
  rule=e['oracles'][f]
  if rule['comparison']=='source-only':assert c['id'] not in rows;continue
  r=rows[c['id']]
  assert r['regerrorRequired']==r['regerrorShortRequired'] and r['regerrorTerminated'] and r['regerrorTailUntouched']
  if rule['comparison']=='agreement':assert comparison(c,r),(f,c['id'])
  else:assert not comparison(c,r) and diffs[(c['id'],f)]['observed']==r
 print(f,dict(Counter(e['oracles'][f]['comparison'] for e in index)))
# Prove helper source lock agrees with actual compiler identity, not merely another manifest copy.
intake=(ROOT/'test/corpora/posix/oracle/native-identity.txt').read_text()
assert artifact('tools/oracles/posix/native/oracle.c')['sha256']+'  oracle.c' in intake
assert '2.36-9+deb12u14' in intake and 'a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4  musl-1.2.5.tar.gz' in intake
with tarfile.open(ROOT/'tools/oracles/posix/native/source-audit.tar.gz') as tar:
 names=tar.getnames()
 assert 'glibc-source/COPYING.LIB' in names and 'musl-1.2.5/COPYRIGHT' in names
 for name in names:assert not name.startswith('/') and '..' not in name.split('/')
 assert b'result = strncmp' in tar.extractfile('musl-1.2.5/src/regex/regexec.c').read()
 assert b'collating symbols and equivalence classes are not supported' in tar.extractfile('musl-1.2.5/src/regex/regcomp.c').read()
print('7265 exact recipes, both actual native families, all 256 locale rows,90 explained differences,21 native-source-only cases and immutable contract lock verified. No product result claimed.')
