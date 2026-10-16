"""Finite source-derived reductions for reported discrepancies; expected native data precedes product checks."""
from pathlib import Path
import gzip,hashlib,importlib.util,json,subprocess
ROOT=Path(__file__).resolve().parents[3];OUT=ROOT/'test/corpora/pcre2/parity';BUILD=Path('/private/tmp/gregsex-m5-parity-fuzz')
spec=importlib.util.spec_from_file_location('pcre2_numeric_inputs',ROOT/'tools/oracles/pcre2/cases.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
# All changes are explicit input reductions, never replacements of frozen original descriptors.
reductions=[('backref-required',r'^(a|)\1b','b',0),('conditional-required',r'(a)?b(?(1)c)','b',0),
 ('conditional-group',r'^(a)?b(?(1)(c))$','b',0),('DEFINE-positive',r'(?<DEFINE>a)(?(DEFINE)b)(?&DEFINE)','aa',0),
 ('DEFINE-negative',r'(?<DEFINE>a)(?(DEFINE)b)(?&DEFINE)','aba',0),('automatic-alternative','a|b','a',4),
 ('accept-empty','a*(*ACCEPT)b','',0),('auto-anchored',r'(?(?=^a)a|^)','',0),('greedy-flags','a*b','ab',4),('capture-greedy-flags','a(b+)(c*)(?C1)','abbc',0)]
inputs=[]
for name,pattern,subject,flags in reductions:
 for width,utf in [(8,False),(8,True),(16,False),(16,True)]:
  for optimize in [True,False]:
   c=m.base('REDUCE-'+name+f'-{width}-'+('utf' if utf else 'raw')+('-full' if optimize else '-none'),pattern,subject,width,utf,
    compileFlags=flags|(m.UTF if utf else 0),optimization=[optimize]*3,workLimit=100000,depthLimit=1024,heapBytes=67108864)
   if name=='capture-greedy-flags':c['callbackReturns']=[1]
   c['nativeDisposition']='DefinedNativeInvocation';c['provenance']={'kind':'BoundedSourceDisagreementReduction','source':'test/corpora/pcre2/metadata/witnesses.json','license':'BSD-3-Clause WITH PCRE2-exception','note':'Fixed source-derived reductions; original retained observations and inputs unchanged. Both full/none native optimizer inputs observed independently.'};inputs.append(c)
source=(ROOT/'tools/oracles/pcre2/observe.c').read_bytes();commands=[];events=[]
prov=json.loads((OUT/'fuzz-provenance.json').read_text())
for member in prov['retainedMembers']:
 p=BUILD/Path(member['path']).name;assert hashlib.sha256(p.read_bytes()).hexdigest()==member['sha256']
for width in [8,16]:
 work=BUILD/('reductions-'+str(width));work.mkdir(exist_ok=True);m.header(inputs,width,work/'cases.h');(work/'observe.c').write_bytes(source)
 cmd=['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I',str(BUILD),str(work/'observe.c'),str(BUILD/f'libpcre2-{width}.a'),'-o',str(work/'observe')]
 build=subprocess.run(cmd,capture_output=True,timeout=60);assert build.returncode==0,build.stderr
 run=subprocess.run([str(work/'observe')],cwd=work,capture_output=True,timeout=30);assert run.returncode==0,run.stderr
 with gzip.open(OUT/f'reduction-native-{width}.jsonl.gz','wb') as f:f.write(run.stdout)
 for l in run.stdout.splitlines():events.append(json.loads(l))
 commands.append({'command':cmd,'buildExitCode':build.returncode,'runExitCode':run.returncode,'stdoutSha256':hashlib.sha256(run.stdout).hexdigest(),'executableSha256':hashlib.sha256((work/'observe').read_bytes()).hexdigest()})
(OUT/'reduction-inputs.json').write_text(json.dumps({'schemaVersion':'pcre2-parity-reductions-1','sourceCommit':m.PCRE if hasattr(m,'PCRE') else 'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','cases':inputs},indent=2)+'\n')
byid={c['id']:[] for c in inputs}
for e in events:byid[e['id']].append(e)
with gzip.open(OUT/'reduction-observations.json.gz','wt') as f:json.dump({'schemaVersion':'pcre2-native-observations-1','cases':[{'id':c['id'],'events':byid[c['id']]} for c in inputs]},f)
(OUT/'reduction-provenance.json').write_text(json.dumps({'sourceCommit':prov['sourceCommit'],'archiveSha256':prov['archiveSha256'],'retainedMembers':prov['retainedMembers'],'clientSha256':hashlib.sha256(source).hexdigest(),'inputsSha256':hashlib.sha256((OUT/'reduction-inputs.json').read_bytes()).hexdigest(),'commands':commands,'cases':len(inputs),'scope':'80 finite reductions in four domains and independently observed full/none policies. No native engine rebuild/broad upstream replay/performance claim.'},indent=2)+'\n')
print('80 finite native reductions captured independently')
