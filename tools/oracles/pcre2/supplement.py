"""Only missing UTF-error offsets/binary-NUL marks; no full corpus/build rerun."""
from pathlib import Path
import json,subprocess,hashlib,shutil
from cases import ROOT,HERE,base,header,native
OUT=ROOT/'test/corpora/pcre2/oracle';CORPUS=ROOT/'test/corpora/pcre2';BUILD=HERE/'.artifacts/build';SRC=HERE/'.artifacts/source/src'
def main():
 inputs=json.loads((CORPUS/'inputs.json').read_text());observations=json.loads((CORPUS/'observations.json').read_text());by={c['id']:c for c in inputs['cases']}
 selected=[]
 for o in observations['cases']:
  if any(e['event']=='match' and -26<=e['returnCode']<=-3 for e in o['events']):selected.append(by[o['id']])
 new=[]
 for width in [8,16]:
  for utf in [False,True]:
   domain=f'{width}-'+('utf' if utf else 'raw');pattern='(*NO_START_OPT)(*MARK:A\x00B)(?C1)a'
   for subject,returns in [('a',[0]),('b',[0]),('a',[-777])]:
    new.append(base(f'binary-mark-{domain}-{len(new)}',pattern,subject,width,utf,callbackReturns=returns,requirements=['PC-017','PC-018','PC-020','PH-004','PH-007']))
   new.append(base(f'binary-callout-{domain}','(?C"\x00")a','a',width,utf,requirements=['PC-020','PH-007']))
 selected+=new;events={};identities=[]
 for width in [8,16]:
  work=BUILD/f'supplement-{width}';work.mkdir(exist_ok=True);header(selected,width,work/'cases.h')
  argv=['clang','-O2','-Wall','-Wextra',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I'+str(SRC),'-I'+str(work),str(HERE/'observe.c'),str(BUILD/f'libpcre2-{width}.a'),'-o',str(work/'observe')]
  p=subprocess.run(argv,capture_output=True,timeout=60);assert p.returncode==0,p.stderr
  p=subprocess.run([work/'observe'],cwd=work,capture_output=True,timeout=60);assert p.returncode==0,p.stderr;(OUT/f'supplement-{width}.jsonl').write_bytes(p.stdout)
  for line in p.stdout.splitlines():e=json.loads(line);events.setdefault(e['id'],[]).append(e)
  identities.append({'width':width,'argv':argv,'exeSha256':hashlib.sha256((work/'observe').read_bytes()).hexdigest(),'headerSha256':hashlib.sha256((work/'cases.h').read_bytes()).hexdigest(),'outputSha256':hashlib.sha256(p.stdout).hexdigest()})
 # Existing unchanged events survive; normalize only source-defined information.
 for o in observations['cases']:
  if o['id'] in events:
   o['events']=events[o['id']];o['provenance']['supplement']='test/corpora/pcre2/oracle/supplement-identities.json'
  for e in o['events']:
   if e['event']=='match':
    rc=e['returnCode'];e['markDefined']=rc>=0 or rc in [-1,-2]
    if not e['markDefined']:e['markUnits']=None
    e.setdefault('errorOffset',None)
 for c in new:
  c['pairCapacityRole']='explicit-native-match-data-and-publication-capacity';c['nativeDisposition']='DefinedNativeInvocation';inputs['cases'].append(c)
  observations['cases'].append({'id':c['id'],'inputId':c['id'],'events':events[c['id']],'nativeDisposition':'DefinedNativeInvocation','provenance':{'kind':'ExecutedPinnedNativeInterpreter','sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','buildIdentity':'test/corpora/pcre2/oracle/build-identity.json','observerIdentity':'test/corpora/pcre2/oracle/supplement-identities.json'}})
 for o in observations['cases']:
  c=next(c for c in inputs['cases'] if c['id']==o['id'])
  for e in o['events']:
   if e['event']=='compile':e['sourceOffset']=None if e['accepted'] else (c.get('sourceMap') or list(range(len(c['patternUnits'])+1)))[e['nativeOffset']]
 (CORPUS/'inputs.json').write_text(json.dumps(inputs,indent=2)+'\n');(CORPUS/'observations.json').write_text(json.dumps(observations,indent=2)+'\n');(OUT/'supplement-identities.json').write_text(json.dumps({'source':'doc/pcre2api.3:3343–3347,3370–3380','selectedExisting':len(selected)-len(new),'newCases':len(new),'identities':identities,'note':'Only missing source-defined UTF-error offsets and length-prefixed binary mark/callout observations; no full matrix/build/trace rerun.'},indent=2)+'\n');print(len(selected)-len(new),'UTF-error cases supplemented;',len(new),'binarymark/callout cases added')
if __name__=='__main__':main()
