"""Fixed-input intake of the externally acquired, exact Test262 archive. No fetching."""
import hashlib, json, pathlib, re, shutil
import yaml
ROOT = pathlib.Path(__file__).resolve().parents[3]
SOURCE = pathlib.Path('/tmp/gregsex-test262')
DEST = ROOT / 'tools/oracles/ecmascript/test262'
COMMIT = '6a73fa8f114611269ce4442e8f19a31239cb3827'
def identity(p):
    b=p.read_bytes(); return {'length':len(b),'sha256':hashlib.sha256(b).hexdigest()}
def write(p,x):
    p.parent.mkdir(parents=True,exist_ok=True); p.write_text(json.dumps(x,indent=2,ensure_ascii=True)+'\n')
selected=[]
for p in sorted((SOURCE/'test').rglob('*.js')):
    s=p.relative_to(SOURCE).as_posix()
    if re.search(r'RegExp|regexp|regular-expression',s) or re.search(r'test/built-ins/String/prototype/(match|matchAll|search|replace|replaceAll|split)/',s): selected.append(p)
files=[]; includes={'assert.js','sta.js'}
for p in selected:
    s=p.relative_to(SOURCE).as_posix(); text=p.read_text(); m=re.search(r'/\*---(.*?)---\*/',text,re.S)
    metadata=yaml.safe_load(m.group(1)) if m else {}
    metadata=metadata or {}; includes.update(metadata.get('includes',[]))
    target=DEST/s;target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,target)
    # Eligibility is by edition features, not runtime acceptance. All feature IDs
    # in this snapshot are retained for review; unknown proposals are never auto-skipped.
    boundary=bool(re.search(r'Get\b|Set\b|species|toString|compile|constructor|prototype|flags|source|lastIndex|Symbol|IsRegExp',str(metadata.get('esid',''))))
    files.append({'path':s,**identity(p),'copyright':text.split('/*---')[0].strip(),
        'metadata':metadata,'targetEdition':'ECMAScript2025-edition16',
        'editionDisposition':'nonnormative-pending-or-staging' if '/staging/' in s or metadata.get('esid')=='pending' or 'legacy-regexp' in metadata.get('features',[]) else 'edition16-or-earlier-assertion',
        'bindingDisposition':'javascript-object-boundary-review' if boundary else 'admitted-semantic-review',
        'bindingReason':'Original JavaScript assertions are retained and executed; typed adaptation excludes coercion, property override, Symbol/species, literal parser or descriptor assertions only after review. This label is triage, not an exclusion or passing product result.'})
for name in sorted(includes):
    p=SOURCE/'harness'/name;t=DEST/'harness'/name;t.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,t)
shutil.copyfile(SOURCE/'LICENSE',DEST/'LICENSE')
write(ROOT/'test/corpora/ecmascript/test262-intake.json',{'schemaVersion':'test262-intake-1','repository':'https://github.com/tc39/test262','commit':COMMIT,'commitDate':'2025-06-09T13:45:43Z','selection':'Every test path containing RegExp/regexp/regular-expression, plus all six String prototype match/matchAll/search/replace/replaceAll/split trees; no acceptance-based selection.','files':files,'harness':[{'path':'harness/'+n,**identity(SOURCE/'harness'/n)} for n in sorted(includes)],'license':identity(SOURCE/'LICENSE')})
print(json.dumps({'commit':COMMIT,'files':len(files),'harness':len(includes),'features':sorted({f for x in files for f in x['metadata'].get('features',[])})}))
