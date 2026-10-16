"""Durably retain bounded native evidence; deterministic archive metadata."""
from pathlib import Path
import gzip,hashlib,io,json,tarfile
from cases import ROOT,HERE
OUT=ROOT/'test/corpora/pcre2/oracle';BUILD=HERE/'.artifacts/build';SRC=HERE/'.artifacts/source/src'
def main():
 members=[];paths={}
 for folder in ['upstream','indexed']:
  for p in sorted((OUT/folder).rglob('*')):
   if p.is_file():paths[str(p.relative_to(OUT))]=p
 for p in sorted(OUT.glob('observer-*')):
  if p.suffix in ['.jsonl','.stderr']:paths[p.name]=p
 for p in [BUILD/'libpcre2-8.a',BUILD/'libpcre2-16.a',BUILD/'pcre2test',BUILD/'pcre2test-indexed',BUILD/'pcre2test-indexed.c',SRC/'config.h',SRC/'pcre2.h',SRC/'pcre2_chartables.c']:
  paths['native-build/'+p.name]=p
 for width in [8,16]:
  for name in ['cases.h','observe']:paths[f'native-build/observer-{width}/{name}']=BUILD/f'observer-{width}/{name}'
  paths[f'native-build/reduce-c048-{width}']=BUILD/f'reduce-c048-{width}'
 for pattern in ['supplement-*.jsonl','upstream-supplement-*.jsonl']:
  for p in OUT.glob(pattern):paths[p.name]=p
 for width in [8,16]:
  for folder in [f'supplement-{width}',f'upstream-supplement-{width}']:
   for name in ['cases.h','observe']:paths[f'native-build/{folder}/{name}']=BUILD/folder/name
 for p in (HERE/'notices').iterdir():paths['notices/'+p.name]=p
 raw=io.BytesIO()
 with tarfile.open(fileobj=raw,mode='w') as t:
  for name,p in sorted(paths.items()):
   data=p.read_bytes();i=tarfile.TarInfo(name);i.size=len(data);i.mode=0o755 if name.endswith('/observe') or name.startswith('native-build/pcre2test') and not name.endswith('.c') else 0o644;i.mtime=0;t.addfile(i,io.BytesIO(data));members.append({'path':name,'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()})
 data=gzip.compress(raw.getvalue(),mtime=0);(OUT/'native-evidence.tar.gz').write_bytes(data)
 record={'schemaVersion':'pcre2-native-artifacts-1','archive':'test/corpora/pcre2/oracle/native-evidence.tar.gz','bytes':len(data),'sha256':hashlib.sha256(data).hexdigest(),'members':members,'sourceArchive':'tools/oracles/pcre2/source/pcre2-10.46-source.tar.gz','sourceArchiveSha256':'ffde0c6ead457c4f8edcd8f9ce490bbabd30adabdfd179ecaddd320946cf3872','note':'All raw outputs/native objects are retained durably, not ephemeral artifact paths. Existing source/archive evidence remains immutable.'}
 (OUT/'artifact-manifest.json').write_text(json.dumps(record,indent=2)+'\n');print(len(members),'durable evidence members;',len(data),'compressed bytes;',record['sha256'])
if __name__=='__main__':main()
