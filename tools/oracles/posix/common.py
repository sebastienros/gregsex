import gzip,hashlib,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
def load(path):return json.loads((ROOT/path).read_text())
def artifact(path):
 data=(ROOT/path).read_bytes()
 return dict(path=path,bytes=len(data),sha256=hashlib.sha256(data).hexdigest())
def observed(family):
 with gzip.open(ROOT/f'test/corpora/posix/oracle/{family}-results.jsonl.gz','rt') as f:rows=[json.loads(line) for line in f]
 return rows[0],rows[1],{r['id']:r for r in rows[2:]}
def comparison(c,r):
 """Exact observable comparison. Native failure buffer contents are unspecified."""
 for e in c['permitted']:
  if e['status'] in ('InvalidSyntax','UnsupportedFeature'):
   if r['status']=='CompileError' and r['errorCode']==e['error']['posixCode']:return True
   continue
  if r['compile']!='None' or r['status']!=e['status']:continue
  if e['groupCount'] is not None and r['groupCount']!=e['groupCount']:continue
  if e['status']=='Match':
   if e['destinationUnchanged']:
    if any(x!=[-777,-888] for x in r['matches']):continue
   elif c['operation']=='RegExec':
    if r['matches']!=[[x['start'],x['end']] for x in e['nativeMatches']]:continue
   elif c['operation'] in ('Find','FindCaptures'):
    m=e['matches'][0]
    expected=[[m['range']['index'],m['range']['index']+m['range']['length']]] if c['operation']=='Find' else [[x['index'],x['index']+x['length']] if x['participated'] else [-1,-1] for x in m['captures']]
    if r['matches']!=expected:continue
  return True
 return False
