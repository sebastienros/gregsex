"""Frozen output check and complete disagreement join, no product matcher."""
from common import *
from collections import Counter
import os
corpus=load(DATA/'corpus.json'); output=load(pathlib.Path(os.environ.get('GREGSEX_XSD_RESULT',str(DATA/'processor-results.json'))))
assert output['corpusSha256']==sha(DATA/'corpus.json') and output['processorUnicode']=='16.0.0'
assert output['xsd11AssertionQualification'] and output['oracleId']==ORACLE_ID
cases={c['id']:c for c in corpus['cases']}; rows={r['id']:r for r in output['results']}
assert len(rows)==len(output['results'])==len(cases) and rows.keys()==cases.keys()
classified=load(DATA/'disagreements.json')['cases']; dispositions={d['id']:d for d in classified}
actual_disagreements=[]; counts=Counter()
for id,c in cases.items():
 r=rows[id]; counts[r['outcome']]+=1; e=c['permitted'][0]
 if r['outcome']=='not-applicable':
  assert e['status'] not in ['Match','NoMatch','InvalidSyntax','ParseSuccess'] or c['patternKind']=='Bytes' or c['limits'] is not None or e.get('error',{}).get('code')=='InvalidPatternEncoding'
  continue
 matches= (r['outcome']=='invalid-pattern' and e['status']=='InvalidSyntax') or \
  (r['outcome']=='valid-pattern' and e['status']=='ParseSuccess') or \
  (r['outcome']=='membership' and e['status'] in ['Match','NoMatch'] and r['boolean']==e['boolean'])
 if not matches:
  actual_disagreements.append(id)
  assert id in dispositions, (id,e,r)
  d=dispositions[id]; assert d['oracleOutcome']==r and d['sourceExpected']==e and d['resolution'] and d['class']
assert set(actual_disagreements)==dispositions.keys(), 'stale/duplicate or missing disagreement'
coverage=load(DATA/'coverage.json')
for cap, witnesses in coverage['capabilities'].items():
 assert witnesses and all(w['id'] in cases for w in witnesses)
 assert {'positive','negative','boundary'}<=set(w['role'] for w in witnesses), (cap,'missing role')
assert set(coverage['productionWitnesses'])=={str(i) for i in range(64,99)}
for row in coverage['productionWitnesses'].values():
 assert row['caseIds'] and all(id in cases and row['capability'] in cases[id]['capabilities'] for id in row['caseIds'])
assert len(coverage['categoryNames'])==36 and len(coverage['blockNames'])==341
print(f'Verified {len(cases)} fixture/output identities; {dict(counts)}; {len(actual_disagreements)} explicitly disposed disagreements, zero unexplained. Product execution not claimed.')
