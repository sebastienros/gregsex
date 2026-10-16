"""Account each imported member, reference control, and native recipe dispatch.

Classification annotates reference observations; it does not parse regex syntax
or implement matching. Original source bytes and the native decoder remain the
source of truth. Mixed recipes retain required and excluded fields separately.
"""
from pathlib import Path
import bisect,collections,hashlib,json,re
from cases import ROOT,HERE
SRC=HERE/'.artifacts/source';OUT=ROOT/'test/corpora/pcre2/oracle';CORPUS=ROOT/'test/corpora/pcre2'
EXCLUDED={
 'dfa':'Alternative DFA algorithm/output policy; approved PH-012 excludes pcre2_dfa_match, not interpreter syntax in the same recipe.',
 'dfa_restart':'DFA workspace/restart protocol; no standard-matcher counterpart.',
 'dfa_shortest':'DFA shortest-result policy; no standard-matcher counterpart.',
 'jit':'Native JIT compilation/mode/artifacts excluded PH-012; interpreter language and outputs remain required.',
 'jitfast':'Explicit native pcre2_jit_match shortcut excluded PH-012; standard counterpart kept separately.',
 'jitstack':'Native executable-stack ownership/physical sizing excluded PH-012.',
 'jitverify':'Native JIT-vs-interpreter diagnostic comparison excluded PH-012; interpreter results remain required.',
 'bincode':'Physical compiled opcode listing excluded; logical syntax/captures/errors retained.',
 'fullbincode':'Full physical opcode listing/link sizes excluded; logical syntax/captures/errors retained.',
 'debug':'Physical opcode listing plus semantic information; exclude bytecode portion only.',
 'memory':'Physical native code allocation/sizeof values excluded; no allocation acceptance inferred.',
 'framesize':'Physical heap-frame byte size excluded; managed logical depth tested later.',
 'heapframes_size':'Native allocated frame-vector bytes excluded; no managed resource-counter equivalence.',
 'find_limits':'Native physical match/depth/heap counter minima excluded; explicit error identities/limit distinction retained.',
 'find_limits_noheap':'Native match/depth minima excluded; explicit error identities retained.',
 'stackguard':'Native compile C-stack recursion-guard callback excluded; managed bounded compilation distinct.',
 'convert':'Experimental glob/POSIX pattern-conversion operation excluded; not a supported regex parser endpoint.',
 'convert_glob_escape':'Glob conversion escape configuration excluded with conversion operation.',
 'convert_glob_separator':'Glob conversion path-separator configuration excluded with conversion operation.',
 'convert_length':'Conversion ABI length configuration excluded with conversion operation.',
 'posix':'Native PCRE2 POSIX-wrapper API excluded; does not narrow standard-interpreter grammar.',
 'posix_nosub':'Native POSIX-wrapper no-subexpression ABI excluded.',
 'posix_startend':'Native POSIX wrapper REG_STARTEND protocol excluded.',
 'regerror_buffsize':'Native POSIX-wrapper message buffer ABI excluded; exact human error messages not required.',
 'push':'Diagnostic compiled-code stack/container protocol excluded; underlying pattern language remains required.',
 'pushcopy':'Native code-copy/diagnostic stack protocol excluded; logical pattern counterpart remains required.',
 'pushtablescopy':'Native compiled-code-and-tables copy ownership excluded; immutable table payload semantics required.',
 'null_pattern':'C pointer-null pattern ABI error fixture excluded; managed argument safety belongs later lanes.',
 'null_subject':'C pointer-null subject ABI fixture excluded; empty numeric subject remains valid independently.',
 'null_replacement':'C pointer-null replacement ABI fixture excluded; numeric empty replacement independent.',
}
SPECIAL={
 'info':('mixed','Semantic INFO required; physical size/JIT/frame/bitmap/name-entry-layout queries excluded individually in INFO inventory.'),
 'locale':('required-table-precondition','Native setlocale/maketables used only to snapshot immutable1088byte payload. Runtime locale forwarding excluded; exact observed payload hashes retained. Unavailable locale is concrete prerequisite failure.'),
 'tables':('required','Exact immutable1088byte table payload/identity required; loader CLI excluded.'),
 'no_utf_check':('required-defined-precondition','Admitted on valid native UTF input; invalid no-check input is source-undefined and excluded, never a required success.'),
 'depth_limit':('mixed','Depth-limit status and directives required; exact native physical counters excluded.'),
 'recursion_limit':('mixed','Native obsolete alias of depth_limit retained; exact native physical counters excluded.'),
 'match_limit':('mixed','Match-limit error/ceiling distinction required; native work-counter equality excluded.'),
 'heap_limit':('mixed','Heap-limit error/directive semantics retained; physical bytes/counter equality excluded.'),
 'max_pattern_compiled_length':('mixed','Native physical-bytecode bound excluded; managed retained-plan/compile budgets remain mandatory separately.'),
 'null_context':('required-defaults','Native NULL context chooses reference defaults; managed defaults replacement required, pointer ABI excluded.'),
 'expand':('input-recipe','Upstream deterministic expansion decoder, not public syntax; native expanded units observed.'),
 'hex':('input-recipe','Upstream hex pattern decoder, numeric native units observed; no source text normalization.'),
 'utf8_input':('input-recipe','Explicit diagnostic input-encoding control, numeric native width conversion observed.'),
 'subject_literal':('input-recipe','Diagnostic escape-decoding suppression; numeric native units observed.'),
 'use_length':('input-recipe','Explicit length vs native terminator ABI fixture; native NUL pattern units/length semantics required.'),
 'zero_terminate':('input-recipe','Diagnostic zero-terminator ABI fixture; defined effective native units observed.'),
}
COMMANDS={
 'forbid_utf':('precondition','Fixture family rejects UTF directives with NEVER_UTF; actual compile identities retained.'),
 'newline_default':('required-defaults','Pinned allowed newline defaults and explicit override selected by RunTest/reference decoder.'),
 'pattern':('required-context','Default pattern modifiers inherited per source; all modifiers individually classified.'),
 'subject':('required-context','Default subject modifiers inherited per source; all modifiers individually classified.'),
 'perltest':('tooling','Comparison-script compatibility marker; no managed feature exclusion.'),
 'loadtables':('required-payload','CLI binary table loader excluded; exact source testbtables1088 payload is required immutable data.'),
 'save':('excluded-container','Native serialized-code/diagnostic stack file operation excluded PH-012; compiled pattern semantics retained.'),
 'load':('excluded-container','Native serialized-code reader excluded PH-012; restored pattern semantic observations retained.'),
 'pop':('excluded-container','Diagnostic code-stack ownership operation excluded; actual restored pattern outputs remain required.'),
 'popcopy':('excluded-container','Native stack code-copy operation excluded; pattern/output counterpart remains required.'),
}
def hashbytes(b):return hashlib.sha256(b).hexdigest()
def valid_utf(units,width):
 try:
  if width==8:bytes(units).decode('utf-8')
  else:bytes().join(int(x).to_bytes(2,'little') for x in units).decode('utf-16le')
  return True
 except (UnicodeError,OverflowError):return False

def main():
 source=(SRC/'src/pcre2test.c').read_text();block=source.split('static modstruct modlist[] = {',1)[1].split('\n};',1)[0]
 controls=[]
 for m in re.finditer(r'\{ "([a-z0-9_]+)"\s*,\s*(\w+)\s*,\s*(\w+)\s*,\s*([^,]+),\s*(.*?)\s*\}',block):
  name,ctx,typ,value,field=m.groups();line=source[:source.index(block)+m.start()].count('\n')+1
  if name in EXCLUDED:category,reason='excluded-native-field',EXCLUDED[name]
  elif name in SPECIAL:category,reason=SPECIAL[name]
  elif value.startswith('PCRE2_') or typ=='MOD_OPTMZ':category,reason='required','Source option/extra/optimizer identity and all defined observable effects required.'
  else:category,reason='required-observable','Native semantic/reporting/callback/host recipe control; full reference decoder output/event observation retained.'
  controls.append({'name':name,'source':'src/pcre2test.c','line':line,'context':ctx,'type':typ,'nativeValue':value,'field':field,'disposition':category,'reason':reason})
 assert len(controls)==len(re.findall(r'^  \{ "',block,re.M)),(len(controls),block)
 short=dict(re.findall(r'\{ "([a-z_]+)"\s*,\s*\'(.)\'',source.split('static c1modstruct c1modlist[] = {',1)[1].split('};',1)[0]))
 short={v:k for k,v in short.items()}
 cmdentries=[{'name':name,'disposition':disp,'reason':reason,'source':'src/pcre2test.c','line':source[:source.index('{ "'+name+'"')].count('\n')+1} for name,(disp,reason) in COMMANDS.items()]
 (CORPUS/'controls.json').write_text(json.dumps({'schemaVersion':'pcre2-controls-1','modifiers':controls,'shortAliases':short,'commands':cmdentries,
 'runTestControls':{'-8/-16':'Required independent widths','-32':'Excluded32domain','-nojit':'Exact approved interpreter oracle','-jit':'Excluded native JIT execution/artifacts', '-dfa':'Excluded alternative algorithm','0':'Excluded unchecked CLI/physical/timing test; -C and help observed separately, no timing performed','heap':'Mixed semantic outcomes required; native physical sizing excluded, environment-specific source diff retained','ebcdic':'Excluded EBCDIC alphabet/build','-valgrind/-valgrind-log':'External native memory diagnostic tool, no managed allocation authority','-sim':'Cross-compiled reference host prerequisite; no claim from unavailable simulated width','-bigstack':'Native C-stack tooling prerequisite, excluded physical layout','number/range/~selector/list':'Diagnostic execution controls; each individual source family accounted'},
 'info':{'requiredQueries':[0,1,2,3,4,5,6,8,9,11,12,13,14,15,16,17,19,20,21,23,25,26],'excludedQueries':{'7':'Physical first-bitmap layout','10':'Native JIT artifact bytes','18':'Native name-table entry physical stride (decoded native names/numbers/order required)','22':'Native compiled physical byte size','24':'Native physical frame byte size'},'aliases':{'RECURSIONLIMIT':'DEPTHLIMIT'}}},indent=2)+'\n')
 names={x['name']:x for x in controls};runs=json.loads((OUT/'indexed/runs.json').read_text());recipes=[];counts=collections.Counter();unique=set();tablemap={}
 for p in sorted((OUT/'indexed').glob('tables-*/*.bin')):
  tablemap[str(p.relative_to(ROOT))]={'sha256':hashbytes(p.read_bytes()),'bytes':p.stat().st_size};assert p.stat().st_size==1088
 for run in runs:
  if not run['trace']:continue
  family=run['family'];sp=run.get('sourceInput',f'testdata/testinput{family}')
  raw=(SRC/sp).read_bytes();lines=raw.splitlines(keepends=True);ends=[];total=0
  for line in lines:total+=len(line);ends.append(total)
  trace=[json.loads(l) for l in (ROOT/run['trace']).read_text().splitlines()];native=(ROOT/run['output']).read_bytes() if run['output'] else b'';width=run['width'];active=[];default_pattern=[];default_subject=[];effective_flags=0;pattern_units=[]
  dispatch=[i for i,e in enumerate(trace) if e['kind'] in ['pattern','subject','control']];covered=set();parent=None
  for ix,start in enumerate(dispatch):
   end=dispatch[ix+1] if ix+1<len(dispatch) else len(trace);e=trace[start];lineNo=bisect.bisect_left(ends,e['inputEnd'])+1;line=lines[lineNo-1].decode('latin1').rstrip('\r\n')
   events=trace[start:end];kind=e['kind'];suffix=line
   consumed=max([e['inputEnd']]+[ev['inputEnd'] for ev in events if ev['kind']!='line'])
   consumedLine=bisect.bisect_left(ends,consumed)+1
   covered.update(range(lineNo,consumedLine+1))
   if kind=='pattern':
    delimiter=line[0];j=1
    while j<len(line):
     if line[j]=='\\' and j+1<len(line):j+=2;continue
     if line[j]==delimiter:break
     j+=1
    suffix=line[j+1:] if j<len(line) else ''
   elif kind=='subject':suffix=line.split('\\=',1)[1] if '\\=' in line else ''
   mods=sorted({n for n in names if re.search(r'(?<![a-zA-Z0-9_])'+re.escape(n)+r'(?![a-zA-Z0-9_])',suffix)})
   if kind=='pattern' and re.fullmatch('[a-zA-Z]+',suffix):mods+=list({short[ch] for ch in suffix if ch in short})
   command=None
   if kind=='control':
    command=line[1:].split(None,1)[0];assert command in COMMANDS,(sp,lineNo,command)
    if command=='pattern':default_pattern=sorted((set(default_pattern)|set(mods))-{n for n in mods if re.search(r'-'+re.escape(n)+r'(?![a-zA-Z0-9_])',suffix)})
    if command=='subject':default_subject=sorted((set(default_subject)|set(mods))-{n for n in mods if re.search(r'-'+re.escape(n)+r'(?![a-zA-Z0-9_])',suffix)})
   if kind=='pattern':active=sorted(set(mods+default_pattern))
   effective=sorted(set(active+(mods+default_subject if kind=='subject' else mods)))
   for ev in events:
    if ev['kind']=='compileInput':pattern_units=ev['units']
    if ev['kind']=='compileResult' and ev['accepted']:effective_flags=next(x[2] for x in ev['info'] if x[0]==0)
   fields=[{'control':n,'disposition':names[n]['disposition'],'reason':names[n]['reason']} for n in effective]
   reasons=[]
   if family in [6,7,13,14] or 'dfa' in effective:reasons.append('DFA matching outcome policy excluded; source pattern/interpreter-compatible compilation independently required.')
   if 'convert' in effective:reasons.append('Conversion input/output is outside standard regex endpoint; no reinterpretation of shell/glob syntax as PCRE regex.')
   if 'posix' in effective or 'posix_nosub' in effective:reasons.append('Native POSIX wrapper outcome excluded; no unrestricted wrapper/API parity claim.')
   unsafe=False
   for ev in events:
    if ev['kind']=='compileInput' and ev['options']&0x40000000 and ev['options']&0x80000 and not valid_utf(ev['units'],width):unsafe=True
    if ev['kind']=='matchInput' and ev['options']&0x40000000 and effective_flags&0x80000 and not effective_flags&0x4000000 and not valid_utf(ev['units'],width):unsafe=True
   if unsafe:reasons.append('Native NO_UTF_CHECK with malformed UTF violates documented valid-input precondition; undefined outputs retained without managed equality obligation.')
   # Local \C consumption can put a scalar reader at a malformed UTF position;
   # source explicitly declares that continuation undefined, rather than promising
   # a decoder's scalar repair. Defined single-unit capture itself stays required.
   if kind=='subject' and effective_flags&0x80000:
    text=''.join(chr(u) for u in pattern_units)
    if re.search(r'\\C(?=\.|\\X|\[)',text):reasons.append(r'Source doc/pcre2pattern.3:1380–1386 defines remaining malformed UTF after splitting \C as undefined; preserve actual observations without required equality for that continuation.')
   output=run['output']
   outputStart=e['outputStart'];outputEnd=trace[end]['outputStart'] if end<len(trace) else len(native)
   # Dispatch events retain their full native event span, including continuations.
   record={'id':f"UP-{width}-{family}-{run.get('mode','normal')[:6]}-{kind}-{lineNo}-{ix}",'width':width,'source':sp,'sourceLine':lineNo,'inputEnd':e['inputEnd'],'kind':kind,'rawRecipeLineLatin1':line,'controls':fields,'controlContextNote':'Source lexical/inherited names classify fields; native compileInput/matchInput events remain authoritative for exact effective options and units.','command':command,'excludedFields':reasons,'observation':{'archive':'test/corpora/pcre2/oracle/native-evidence.tar.gz','traceMember':str((ROOT/run['trace']).relative_to(OUT)),'eventStart':start,'eventEndExclusive':end,'outputMember':str((ROOT/output).relative_to(OUT)) if output else None,'outputStart':outputStart,'outputEnd':outputEnd},'observedKinds':dict(collections.Counter(x['kind'] for x in events if x['kind']!='line'))}
   # No recipe disappears when a native compile/modifier error prevents matching.
   record['disposition']='observed-source-error-or-precondition' if not any(x['kind'] in ['compileResult','matchResult','substituteCallout','caseCalloutOutput'] for x in events) else 'observed-required-and-explicit-boundaries'
   recipes.append(record);counts[kind]+=1;unique.add((sp,lineNo,kind))
  # Compile/modifier/locale errors intentionally prevent the reference from
  # dispatching dependent subjects. Preserve each original subject line with
  # the exact earlier error/precondition instead of deleting/skipping recipes.
  parent_dispatch=None
  event_dispatch={trace[i]['inputEnd']:i for i in dispatch}
  for ev in trace:
   if ev['kind'] in ['pattern','control','subject']:parent_dispatch=ev
   if ev['kind']!='line':continue
   no=bisect.bisect_left(ends,ev['inputEnd'])+1
   textline=lines[no-1].decode('latin1').rstrip('\r\n')
   if no in covered or not textline.strip() or textline.startswith('#') or textline.lstrip().startswith('\\= '):continue
   recipes.append({'id':f'UP-{width}-{family}-undispatched-{no}','width':width,'source':sp,'sourceLine':no,'kind':'undispatched-dependent-source-line','rawRecipeLineLatin1':textline,'disposition':'observed-source-error-or-precondition','reason':'Reference decoder does not dispatch this source line after its preceding pattern/command failed or was in a skip state. The actual preceding native error/locale/modifier precondition is retained, not a semantic pass.','parentNativeInputEnd':parent_dispatch['inputEnd'] if parent_dispatch else None,'observation':{'archive':'test/corpora/pcre2/oracle/native-evidence.tar.gz','traceMember':str((ROOT/run['trace']).relative_to(OUT)),'outputMember':str((ROOT/run['output']).relative_to(OUT)) if run['output'] else None,'lineOutputStart':ev['outputStart']}})
   counts['undispatched-dependent-source-line']+=1;covered.add(no)

 # EBCDIC recipes cannot be executed in the approved ASCII/native8/16 builds.
 # Enumerate each source pattern and subject separately with its exact precondition.
 raw=(SRC/'testdata/testinputEBC').read_bytes();lines=raw.splitlines();kind='pattern'
 for no,line in enumerate(lines,1):
  if not line.strip():kind='pattern';continue
  if line.startswith(b'#') or line.lstrip().startswith(b'\\= '):continue
  itemkind='pattern' if kind=='pattern' else 'subject';kind='subject'
  recipes.append({'id':f'UP-EBC-{itemkind}-{no}','source':'testdata/testinputEBC','sourceLine':no,'kind':itemkind,'rawRecipeLineLatin1':line.decode('latin1'),'disposition':'approved-domain-exclusion','reason':'Exact recipe requires EBCDIC build/alphabet/encoded escape letters; PH-012 excludes EBCDIC. No ASCII reinterpretation/native success claim.'});counts['EBCDIC-'+itemkind]+=1
 manifest=json.loads((ROOT/'tools/oracles/pcre2/source/source-manifest.json').read_text());files=[]
 for m in manifest['files']:
  if not m['path'].startswith('testdata/'):continue
  p=m['path'];name=Path(p).name
  if 'testinput' in name:role='native input recipes accounted individually; EBCDIC/Windows locale preconditions explicit'
  elif 'testoutput' in name and '-32' in name:role='32bit-domain reference output excluded; preserve source bytes/digest'
  elif name=='testbtables':role='required imported1088byte binary character table, exact source digest and observed use_tables snapshot'
  elif 'valgrind-jit' in name:role='native JIT memory diagnostic suppressions excluded'
  elif 'testoutput' in name:role='exact pinned reference output; width/linksize/locale variant selection from RunTest, physical fields excluded individually'
  else:role='auxiliary upstream recipe/member retained; see original bytes and RunTest source'
  files.append(dict(m,role=role))
 assert len(files)==106
 summary={'schemaVersion':'pcre2-upstream-accounting-1','sourceCommit':manifest['commit'],'memberCount':106,'members':files,'recipeCount':len(recipes),'uniqueNativeSourceDispatches':len(unique),'counts':dict(counts),'recipesArtifact':'test/corpora/pcre2/upstream-recipes.jsonl.gz','tableSnapshots':tablemap,'unicodeInputs':[m for m in manifest['files'] if m['path'].startswith('maint/Unicode.tables/')],
 'selectionSource':{'path':'RunTest','sha256':hashbytes((SRC/'RunTest').read_bytes()),'actualConfig':'test/corpora/pcre2/oracle/build-identity.json','widths':[8,16],'linksize':2,'unicode':'16.0.0','locale':'fr_FR selected successfully; original wintestinput3 French locale failures are preserved concrete preconditions'},
 'publishedInputErrata':[{'id':'PCRE-C048','source':'test/corpora/pcre2/C048-erratum.md','effectiveSuccessChange':'pairCapacity1to2 only','mandatoryOriginalRegression':'explicit1pair -54','mandatoryAdditionalCounterpart':'NULLauto-full rc1/outputaa','nativeEvidence':'test/corpora/pcre2/oracle/C048-reduction.json','frozenSeedBytesUnchanged':True}],
 'note':'No whole mixed family is silently excluded. Recipe/control records preserve required observations and individually reasoned boundaries. Native errors preventing subject dispatch are preserved with raw original source lines. ABI/physical/JIT/DFA32/EBCDIC/conversion/POSIX tool boundaries are not managed grammar errors.'}
 import gzip
 recipebytes=b''.join((json.dumps(r,separators=(',',':'))+'\n').encode() for r in recipes)
 compressed=gzip.compress(recipebytes,mtime=0)
 (CORPUS/'upstream-recipes.jsonl.gz').write_bytes(compressed)
 summary['recipesSha256']=hashbytes(compressed)
 summary['uncompressedRecipesSha256']=hashbytes(recipebytes)
 (CORPUS/'upstream-accounting.json').write_text(json.dumps(summary,indent=2)+'\n');print(len(controls),'modifiers;',len(recipes),'recipe dispatches;',len(tablemap),'exact1088byte table snapshots;',counts)
if __name__=='__main__':main()
