"""Replay only upstream records missing defined UTF-error offsets/binary marks."""
from pathlib import Path
import json,subprocess,hashlib
from cases import ROOT,HERE,base,header
OUT=ROOT/'test/corpora/pcre2/oracle';BUILD=HERE/'.artifacts/build';SRC=HERE/'.artifacts/source/src'
def main():
 jobs=[];locations={};runs=json.loads((OUT/'indexed/runs.json').read_text())
 for run in runs:
  if not run['trace']:continue
  path=ROOT/run['trace'];trace=[json.loads(l) for l in path.read_text().splitlines()];comp=None;match=None;accepted=False
  for index,e in enumerate(trace):
   if e['kind']=='compileInput':comp=e;accepted=False
   if e['kind']=='compileResult':accepted=e['accepted']
   if e['kind']=='matchInput':match=e
   if e['kind']!='matchResult' or comp is None or match is None or not accepted:continue
   pattern=''.join(chr(x) for x in comp['units']);utferror=-26<=e['returnCode']<=-3
   binarymark='(*MARK:' in pattern and ('\x00' in pattern or ('\\x00' in pattern and comp['options']&0x400000))
   if not utferror and not binarymark:continue
   id=f'UP-SUP-{len(jobs)}';c=base(id,comp['units'],match['units'],run['width'],False,compileFlags=comp['options'],extraFlags=comp['extraOptions'],newline=comp['newline'],bsr=comp['bsr'],maxVariableLookbehind=comp['maxVariableLookbehind'],matchFlags=match['options'],startAt=match['startAt'],pairCapacity=max(match['pairCapacity'],1))
   jobs.append(c);locations[id]={'trace':run['trace'],'eventIndex':index,'inputEnd':e['inputEnd'],'utfError':utferror,'binaryMark':bool(binarymark),'originalReturn':e['returnCode']}
 results={};identities=[]
 for width in [8,16]:
  work=BUILD/f'upstream-supplement-{width}';work.mkdir(exist_ok=True);header(jobs,width,work/'cases.h')
  argv=['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I'+str(SRC),'-I'+str(work),str(HERE/'observe.c'),str(BUILD/f'libpcre2-{width}.a'),'-o',str(work/'observe')]
  p=subprocess.run(argv,capture_output=True,timeout=60);assert p.returncode==0,p.stderr
  p=subprocess.run([work/'observe'],cwd=work,capture_output=True,timeout=60);assert p.returncode==0,p.stderr;(OUT/f'upstream-supplement-{width}.jsonl').write_bytes(p.stdout)
  for line in p.stdout.splitlines():
   e=json.loads(line)
   if e['event']=='match':results[e['id']]=e
  identities.append({'width':width,'argv':argv,'exeSha256':hashlib.sha256((work/'observe').read_bytes()).hexdigest(),'headerSha256':hashlib.sha256((work/'cases.h').read_bytes()).hexdigest(),'outputSha256':hashlib.sha256(p.stdout).hexdigest()})
 annotations=[];patched={}
 for id,loc in locations.items():
  e=results[id];assert e['returnCode']==loc['originalReturn'],(id,loc,e)
  path=ROOT/loc['trace'];trace=patched.setdefault(loc['trace'],[json.loads(l) for l in path.read_text().splitlines()]);target=trace[loc['eventIndex']]
  target['errorOffset']=e['errorOffset'];target['startChar']=e['startChar'];target['markDefined']=e['markDefined'];target['markUnits']=e['markUnits'];target['supplement']='test/corpora/pcre2/oracle/upstream-supplement.json'
  annotations.append(dict(id=id,**loc,observedErrorOffset=e['errorOffset'],observedMarkUnits=e['markUnits']))
 # Preserve the original raw trace bytes; new complete trace is a supplement,
 # not silent overwriting/deleting of earlier source evidence.
 for name,trace in patched.items():
  path=ROOT/name;backup=path.with_suffix('.pre-supplement.jsonl');backup.write_bytes(path.read_bytes());path.write_text(''.join(json.dumps(e,separators=(',',':'))+'\n' for e in trace))
 (OUT/'upstream-supplement.json').write_text(json.dumps({'nativeSource':'doc/pcre2api.3:3343–3347,3370–3380','records':annotations,'identities':identities,'scope':'Only source-defined getter observations; original raw trace retained; no full family/matrix/build rerun.'},indent=2)+'\n');print(len(jobs),'targeted upstream getter observations supplemented')
if __name__=='__main__':main()
