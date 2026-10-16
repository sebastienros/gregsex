"""Fixed bounded correctness workflow; does not rebuild/replay upstream or read product results."""
from pathlib import Path
import gzip,hashlib,importlib.util,json,random,subprocess,tarfile
ROOT=Path(__file__).resolve().parents[3]
OUT=ROOT/'test/corpora/pcre2/parity'; BUILD=Path('/private/tmp/gregsex-m5-parity-fuzz')
BUILD.mkdir(parents=True,exist_ok=True)
def sha(data):return hashlib.sha256(data).hexdigest()
archive=ROOT/'test/corpora/pcre2/oracle/native-evidence.tar.gz'
manifest=json.loads((ROOT/'test/corpora/pcre2/oracle/artifact-manifest.json').read_text())
assert sha(archive.read_bytes())==manifest['sha256']
identities=[]
with tarfile.open(archive) as tar:
 for name in ['native-build/libpcre2-8.a','native-build/libpcre2-16.a','native-build/pcre2.h']:
  data=tar.extractfile(name).read(); expected=next(m for m in manifest['members'] if m['path']==name)
  assert sha(data)==expected['sha256']
  (BUILD/Path(name).name).write_bytes(data);identities.append(expected)
spec=importlib.util.spec_from_file_location('pcre2_numeric_inputs',ROOT/'tools/oracles/pcre2/cases.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
seed=0x5D002; rng=random.Random(seed); inputs=[]
atoms=['a','b','[ab]','[^b]','\\w','\\d','(a|b)','(a|ab)','(a?)','(?<=a)b','(a)\\1','a\\Kb','(*MARK:m)a']
for index in range(64):
 pattern=rng.choice(atoms)+rng.choice(['','?','*','+','{0,2}'])+rng.choice(['','a','b','$'])
 if index%3==0:pattern='^'+pattern
 subject=''.join(rng.choice('abxy12\r\n') for _ in range(rng.randrange(9)))
 for width,utf in [(8,False),(8,True),(16,False),(16,True)]:
  case=module.base(f'FUZZ-{seed:x}-{index:02}-{width}-'+('utf' if utf else 'raw'),pattern,subject,width,utf,
   startAt=rng.randrange(len(subject)+1),optimization=[index%4!=0]*3,workLimit=100000,depthLimit=1024,heapBytes=67108864,
   provenance={'kind':'DeterministicBoundedGrammarInput','source':'doc/pcre2pattern.3','license':'BSD-3-Clause WITH PCRE2-exception','seed':seed,'grammarVersion':'pcre2-parity-grammar-1','note':'Generated before any product observation; limits are correctness protection, not native/managed counter equality.'})
  case['nativeDisposition']='DefinedNativeInvocation';inputs.append(case)
assert len(inputs)==256
source=(ROOT/'tools/oracles/pcre2/observe.c').read_bytes()
(BUILD/'observe.c').write_bytes(source)
commands=[];events=[]
for width in [8,16]:
 work=BUILD/str(width);work.mkdir(exist_ok=True)
 module.header(inputs,width,work/'cases.h');(work/'observe.c').write_bytes(source)
 cmd=['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I',str(BUILD),str(work/'observe.c'),str(BUILD/f'libpcre2-{width}.a'),'-o',str(work/'observe')]
 compile=subprocess.run(cmd,capture_output=True,timeout=60);assert compile.returncode==0,compile.stderr
 run=subprocess.run([str(work/'observe')],cwd=work,capture_output=True,timeout=30);assert run.returncode==0,run.stderr
 (OUT/f'fuzz-native-{width}.stderr').write_bytes(run.stderr)
 for line in run.stdout.splitlines():events.append(json.loads(line))
 with gzip.open(OUT/f'fuzz-native-{width}.jsonl.gz','wb') as f:f.write(run.stdout)
 commands.append({'command':cmd,'exitCode':compile.returncode,'executableSha256':sha((work/'observe').read_bytes()),'headerSha256':sha((work/'cases.h').read_bytes()),'stdoutSha256':sha(run.stdout),'stderrSha256':sha(run.stderr),'runExitCode':run.returncode})
byid={c['id']:[] for c in inputs}
for event in events:byid[event['id']].append(event)
(OUT/'fuzz-inputs.json').write_text(json.dumps({'schemaVersion':'pcre2-parity-fuzz-1','sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','seed':seed,'cases':inputs},indent=2)+'\n')
with gzip.open(OUT/'fuzz-observations.json.gz','wt') as f:json.dump({'schemaVersion':'pcre2-native-observations-1','cases':[{'id':c['id'],'events':byid[c['id']]} for c in inputs]},f)
(OUT/'fuzz-provenance.json').write_text(json.dumps({'sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','archiveSha256':manifest['sha256'],'retainedMembers':identities,'clientSha256':sha(source),'inputsSha256':sha((OUT/'fuzz-inputs.json').read_bytes()),'commands':commands,'seed':seed,'cases':256,'limits':'100000nativework/1024depth/64MiB; not managed counter equality','scope':'New bounded independent correctness witnesses only; no upstream/native library rebuild/replay, performance or platform-support claim.'},indent=2)+'\n')
print('256 bounded native grammar cases captured independently in four native domains')
