"""Fixed native observation workflow, without command-line argument parsing."""
from pathlib import Path
import json,subprocess,hashlib,shutil
from cases import ROOT,HERE,generate,header
OUT=ROOT/'test/corpora/pcre2/oracle';BUILD=HERE/'.artifacts/build';SRC=HERE/'.artifacts/source/src'
def main():
 cases=generate();(ROOT/'test/corpora/pcre2/inputs.json').write_text(json.dumps({'schemaVersion':'pcre2-native-inputs-1','sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','cases':cases},indent=2)+'\n')
 events={};identities=[]
 for width in [8,16]:
  work=BUILD/f'observer-{width}';work.mkdir(exist_ok=True);header(cases,width,work/'cases.h')
  argv=[shutil.which('clang'),'-O2','-Wall','-Wextra',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I'+str(SRC),'-I'+str(work),str(HERE/'observe.c'),str(BUILD/f'libpcre2-{width}.a'),'-o',str(work/'observe')]
  p=subprocess.run(argv,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120);(OUT/f'observer-build-{width}.log').write_bytes(p.stdout);assert p.returncode==0,p.stdout.decode()
  p=subprocess.run([work/'observe'],cwd=BUILD,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120);(OUT/f'observer-{width}.jsonl').write_bytes(p.stdout);(OUT/f'observer-{width}.stderr').write_bytes(p.stderr);assert p.returncode==0,p.stderr
  for line in p.stdout.splitlines():
   e=json.loads(line);events.setdefault(e['id'],[]).append(e)
  identities.append({'width':width,'argv':argv,'executableSha256':hashlib.sha256((work/'observe').read_bytes()).hexdigest(),'headerSha256':hashlib.sha256((work/'cases.h').read_bytes()).hexdigest(),'outputSha256':hashlib.sha256(p.stdout).hexdigest(),'tablesSha256':hashlib.sha256((BUILD/f'tables-{width}.bin').read_bytes()).hexdigest()})
  shutil.copyfile(BUILD/f'tables-{width}.bin',ROOT/f'test/corpora/pcre2/default-tables-{width}.bin')
 corpus=[]
 for c in cases:
  assert c['id'] in events
  ee=events[c['id']]
  for e in ee:
   if e['event']=='compile' and 'sourceMap' in c and not e['accepted']:e['sourceOffset']=c['sourceMap'][e['nativeOffset']]
  corpus.append({'id':c['id'],'inputId':c['id'],'events':ee,'provenance':{'kind':'ExecutedPinnedNativeInterpreter','sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','buildIdentity':'test/corpora/pcre2/oracle/build-identity.json','observerIdentity':'test/corpora/pcre2/oracle/observer-identities.json'}})
 (ROOT/'test/corpora/pcre2/observations.json').write_text(json.dumps({'schemaVersion':'pcre2-native-observations-1','cases':corpus},indent=2)+'\n');(OUT/'observer-identities.json').write_text(json.dumps(identities,indent=2)+'\n');print(len(corpus),'complete native event sequences')
if __name__=='__main__':main()
