"""Fixed bounded semantic audit. Docker is a development oracle, never a product fallback."""
import gzip,hashlib,json,subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
OUT=ROOT/'test/corpora/posix/oracle'
IMAGE='sha256:5cb918ae6077d0d575f45bd12847517e6c60f89745230eb43401be7928fc043f'
def load(p):return json.loads((ROOT/p).read_text())
def native_request(c):
 reason=None
 if c['limits'] is not None:reason='Managed finite budget/cancellation has no libc equivalent.'
 elif c['operation']=='RegError':reason='Managed stable ASCII message; native wording is unspecified, independently probed for every native result.'
 elif c['locale'] not in ('C','POSIX'):reason='Named provider excludes other locales; native process remains C.'
 elif c['inputKind']!='Bytes':reason='Managed representation exclusion has no libc counterpart.'
 elif any(b==0 or b>255 for b in c['patternUnits']+c['inputUnits']):reason='Managed whole-span domain rule differs from C NUL-terminated interface.'
 elif c['startAt']!=0:reason='Generic original-subject lower-bound is not native regexec ABI; never slice or add NOTBOL to fabricate it.'
 elif c['permitted'][0]['status'] in ('DestinationTooSmall','OperationRejected'):reason='Generic complete-destination/NoSub exception is a managed adapter obligation.'
 if reason:return None,reason
 e=c['permitted'][0]
 if c['operation']=='RegExec':n=c['destinationLength']
 elif c['operation']=='FindCaptures':n=c['destinationLength']
 elif c['operation']=='Boolean':n=0
 else:n=1
 if n>64:raise ValueError('Helper nmatch cap')
 hx=lambda units:bytes(units).hex() or '-'
 op='Parse' if c['operation']=='Parse' else 'Exec'
 return f"{c['id']} {int(c['profileId']=='PosixEre2018-CByte')} {c['compileFlags']} {c['executionFlags']} {n} {op} {hx(c['patternUnits'])} {hx(c['inputUnits'])}\n",None
def main():
 cases=load('test/GregsEx.Tests/Contracts/Posix/ContractVectors.json')['cases']+load('test/corpora/posix/expanded.json')['cases']
 requests=[];source_only={}
 for c in cases:
  request,reason=native_request(c)
  if request:requests.append(request)
  else:source_only[c['id']]=reason
 payload=''.join(requests).encode()
 image=json.loads(subprocess.check_output(['docker','image','inspect',IMAGE],text=True))[0]
 identity=subprocess.check_output(['docker','run','--rm','--network','none',IMAGE,'cat','identity.txt'],text=True)
 (OUT/'native-identity.txt').write_text(identity)
 (OUT/'image-identity.json').write_text(json.dumps({k:image[k] for k in ('Id','RepoDigests','Architecture','Os')},indent=2)+'\n')
 for family in ('glibc','musl'):
  command=['docker','run','--rm','--network','none','--memory','512m','--pids-limit','32','-i',IMAGE,'/intake/oracle-'+family]
  result=subprocess.run(command,input=payload,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=180)
  if result.returncode:raise RuntimeError(f'{family} exit {result.returncode}: {result.stderr.decode()}')
  rows=[json.loads(line) for line in result.stdout.splitlines()]
  if len(rows)!=len(requests)+2 or rows[0]['kind']!='identity' or rows[1]['kind']!='locale':raise ValueError('Incomplete native batch')
  if [r['id'] for r in rows[2:]]!=[r.split()[0] for r in requests]:raise ValueError('Native result order changed')
  with open(OUT/(family+'-results.jsonl.gz'),'wb') as target:
   with gzip.GzipFile(fileobj=target,mode='wb',mtime=0,filename='') as zipped:zipped.write(result.stdout)
  print(f'{family}: {len(requests)} completed semantic requests; all source-only cases retained separately.')
 (OUT/'source-only.json').write_text(json.dumps(source_only,indent=2)+'\n')
if __name__=='__main__':main()
