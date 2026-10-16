"""Run each upstream recipe family independently; retain original outputs and diffs."""
from pathlib import Path
import os, subprocess, json, hashlib, shutil
ROOT=Path(__file__).resolve().parents[3]; HERE=Path(__file__).resolve().parent
SRC=HERE/'.artifacts/source'; BUILD=HERE/'.artifacts/build'; OUT=ROOT/'test/corpora/pcre2/oracle/upstream'
def main():
 OUT.mkdir(parents=True,exist_ok=True); results=[]
 for width in [8,16]:
  for family in list(range(1,28))+['heap']:
   work=HERE/f'.artifacts/upstream-{width}-{family}';work.mkdir(exist_ok=True)
   env=dict(os.environ,srcdir=str(SRC),pcre2test=str(HERE/'capture-native.sh'),GREGSEX_NATIVE_DRIVER=str(BUILD/'pcre2test'),GREGSEX_NATIVE_CAPTURE=str(work/'retained-native-output'),LC_ALL='C')
   argv=['/bin/sh',str(SRC/'RunTest'),f'-{width}','-nojit',str(family)]
   p=subprocess.run(argv,cwd=work,env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120)
   stem=f'{width}-{family}'; (OUT/f'{stem}.log').write_bytes(p.stdout)
   raw=work/'retained-native-output'
   if raw.exists(): shutil.copyfile(raw,OUT/f'{stem}.native')
   results.append({'width':width,'family':family,'argv':argv,'exit':p.returncode,
      'log':str((OUT/f'{stem}.log').relative_to(ROOT)),
      'nativeOutput':str((OUT/f'{stem}.native').relative_to(ROOT)) if raw.exists() else None,
      'nativeOutputSha256':hashlib.sha256(raw.read_bytes()).hexdigest() if raw.exists() else None,
      'note':'Diagnostic reference self-check only. Explicit skips are preconditions/exclusions, never semantic passes.'})
   print(stem,p.returncode,flush=True)
 (OUT/'runs.json').write_text(json.dumps(results,indent=2)+'\n')
if __name__=='__main__':main()
