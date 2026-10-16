"""Repair only missing raw-output retention; preserve completed source traces."""
from pathlib import Path
import os,json,subprocess,hashlib
from cases import ROOT,HERE
OUT=ROOT/'test/corpora/pcre2/oracle';SRC=HERE/'.artifacts/source';BUILD=HERE/'.artifacts/build'
def main():
 runs=json.loads((OUT/'indexed/runs.json').read_text());repairs=[]
 for r in runs:
  if not r['trace'] or r['output']:continue
  width,family=r['width'],r['family'];work=HERE/f'.artifacts/capture-{width}-{family}';work.mkdir(exist_ok=True);native=OUT/f'indexed/{width}-{family}.native'
  env=dict(os.environ,srcdir=str(SRC),pcre2test=str(HERE/'capture-native.sh'),GREGSEX_NATIVE_DRIVER=str(BUILD/'pcre2test-indexed'),GREGSEX_NATIVE_CAPTURE=str(native),LC_ALL='C')
  # No trace environment: earlier full structured native observations stay intact.
  argv=['/bin/sh',str(SRC/'RunTest'),f'-{width}','-nojit',str(family)]
  p=subprocess.run(argv,cwd=work,env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120)
  (OUT/f'indexed/{width}-{family}-capture.log').write_bytes(p.stdout)
  assert p.returncode==r['exit'],(r,p.stdout)
  assert native.exists(),r
  data=native.read_bytes();trace=[json.loads(l) for l in (ROOT/r['trace']).read_text().splitlines()]
  assert max(e['outputStart'] for e in trace)<=len(data)
  r['output']=str(native.relative_to(ROOT));r['rawOutputRetentionRepair']='Native CLI unchanged; wrapper copies before upstream cleanup; prior trace not rerun/replaced.'
  repairs.append({'width':width,'family':family,'argv':argv,'exit':p.returncode,'output':r['output'],'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest(),'nativeExactSourceSelfCheck':'passed; copied before RunTest cleanup'})
 # Explicit original-vs-instrumented output equality is proven transitively by
 # both unmodified and instrumented RunTest exact-golden selfchecks. A direct
 # file comparison was unavailable before cleanup; never label it as executed.
 (OUT/'indexed/runs.json').write_text(json.dumps(runs,indent=2)+'\n')
 (OUT/'raw-retention-repair.json').write_text(json.dumps({'repairs':repairs,'note':'Only missing raw-output retention repaired. Earlier source builds/native structured matrix/decoder traces remain unchanged. RunTest source equality is the executed check; no original file-comparison claim.'},indent=2)+'\n')
 print('Preserved',len(repairs),'missing raw output files; no builds/matrix/trace rerun.')
if __name__=='__main__':main()
