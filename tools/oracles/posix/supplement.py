"""Fixed incremental audit; preserve all valid earlier native rows and run only missing recipes."""
import gzip,json,subprocess
from common import ROOT,load,observed
from run import IMAGE,native_request
cases=load('test/GregsEx.Tests/Contracts/Posix/ContractVectors.json')['cases']+load('test/corpora/posix/expanded.json')['cases']
for family in ('glibc','musl'):
 identity,table,old=observed(family)
 missing=[c for c in cases if c['id'] not in old and native_request(c)[0] is not None]
 if not missing:print(f'{family}: no missing native requests; no execution.');continue
 payload=''.join(native_request(c)[0] for c in missing).encode()
 result=subprocess.run(['docker','run','--rm','--network','none','--memory','512m','--pids-limit','32','-i',IMAGE,'/intake/oracle-'+family],input=payload,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=30)
 if result.returncode:raise RuntimeError(f'{family} supplementexit {result.returncode}')
 rows=[json.loads(line) for line in result.stdout.splitlines()]
 assert rows[0]==identity and rows[1]==table
 assert [r['id'] for r in rows[2:]]==[c['id'] for c in missing]
 new={r['id']:r for r in rows[2:]};combined={**old,**new}
 # Re-serialization is presentation only; exact old JSON result structures remain identical.
 data=''.join(json.dumps(r,separators=(',',':'))+'\n' for r in [identity,table]+[combined[c['id']] for c in cases if c['id'] in combined]).encode()
 target=ROOT/f'test/corpora/posix/oracle/{family}-results.jsonl.gz'
 with open(target,'wb') as f:
  with gzip.GzipFile(fileobj=f,mode='wb',mtime=0,filename='') as zipped:zipped.write(data)
 after=observed(family)[2]
 assert all(after[key]==value for key,value in old.items())
 print(f'{family}: {len(missing)} new requests complete; every {len(old)} earlier result preserved exactly.')
