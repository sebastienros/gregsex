"""Copy immutable native observations, never managed output, into bounded INFO inputs."""
import gzip, hashlib, json, tarfile
from pathlib import Path
root=Path(__file__).resolve().parents[4]
report=root/'test/corpora/pcre2/metadata'
recipes=[json.loads(x) for x in gzip.open(root/'test/corpora/pcre2/upstream-recipes.jsonl.gz','rt')]
archive=root/'test/corpora/pcre2/oracle/native-evidence.tar.gz'
assert hashlib.sha256(archive.read_bytes()).hexdigest()=='339344898646d67a24c61e244a46ddf345ac8f55c6aacf9b7b32b7c34645bb71'
traces={};tables={}
with tarfile.open(archive) as tar:
 for m in tar:
  if m.name.startswith('indexed/') and m.name.endswith('.jsonl'):traces[m.name]=[json.loads(x) for x in tar.extractfile(m)]
  elif m.name.startswith('indexed/tables-') and m.name.endswith('.bin'):tables[m.name]=list(tar.extractfile(m).read())
count=0
with gzip.open(report/'upstream-info.jsonl.gz','wt') as out:
 for r in recipes:
  o=r.get('observation',{})
  if 'eventStart' not in o or 'traceMember' not in o:continue
  events=traces[o['traceMember']][o['eventStart']:o['eventEndExclusive']]
  inputs=[e for e in events if e['kind']=='compileInput'];expected=[e for e in events if e['kind']=='compileResult']
  if not inputs or not expected or not expected[0]['accepted']:continue
  inp=inputs[0];table=o['traceMember'].replace('.jsonl','').replace('indexed/','indexed/tables-')+'/'+str(inp['inputEnd'])+'.bin'
  row={'id':r['id'],'input':inp,'expected':expected[0],'controls':r['controls'],'tables':tables.get(table),'recipe':r['rawRecipeLineLatin1']}
  out.write(json.dumps(row,separators=(',',':'))+'\n');count+=1
(report/'upstream-intake.json').write_text(json.dumps({'native_accepted':count,'archive_sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'corpus_sha256':hashlib.sha256((root/'test/corpora/pcre2/upstream-recipes.jsonl.gz').read_bytes()).hexdigest(),'inputs_sha256':hashlib.sha256((report/'upstream-info.jsonl.gz').read_bytes()).hexdigest()},indent=2)+'\n')
print(count)
