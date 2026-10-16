import hashlib,json,subprocess,tarfile
from pathlib import Path
here=Path(__file__).resolve().parent
root=Path(__file__).resolve().parents[4];out=root/'test/corpora/pcre2/metadata';temp=Path('/private/tmp/gregsex-m5-parity-metadata-native');temp.mkdir(exist_ok=True)
archive=root/'test/corpora/pcre2/oracle/native-evidence.tar.gz';receipt=json.loads((root/'test/corpora/pcre2/controls/native-window-provenance.json').read_text())
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(archive)==receipt['archiveSha256']
with tarfile.open(archive) as tar:
 for m in receipt['members']:
  content=tar.extractfile(m['member']).read();assert hashlib.sha256(content).hexdigest()==m['sha256'];p=temp/m['member'];p.parent.mkdir(exist_ok=True,parents=True);p.write_bytes(content)
seeds=[
 ('^(cow|)\\1(bell)','bell',0),('(?(?=(a))a)(b)','b',0),('a*(*ACCEPT)b','',0),('(?(?=^.*b)b|^)','b',0),
 ('(?<DEFINE>b)(?(DEFINE)(a+))(?&DEFINE)','bbbb',0),('(?<DEFINE>b)(?(DEFINE)(a+))(?&DEFINE)','baaab',0),
 ('(?<DEFINE>b)(?(<DEFINE>)(a+))(?&DEFINE)','baaab',0),("(?<DEFINE>b)(?('DEFINE')(a+))(?&DEFINE)",'baaab',0),
 ('(a)(*scs:(1)b)*c','ac',0),('(?<x>a)(*scs:(<x>)b)*c','ac',0),('(?<x>a)(?(R&x)b|c)','ac',0),
 ('(?J)(?<x>a)(?<x>b)(?(R&x)c|d)','abd',0),('^(a\\1?)(a\\1?)(a\\2?)(a\\3?)$','aaaa',0),
 ('^(a|)\\1*b','b',0),('^(aa(bb)?)+$','aaaa',0),('^(a){2,}+(\\w)','aab',0),('^(a)(?1)+ab','aaaab',0),
 ('^(x(y|(?1){2})z)','xyz',0),('(?>(*ACCEPT)b)c','',0),('[\\S\\s]*','abc',0),
 ('(?P=same)?(?P<same>a)(?P<same>b)','ab',64),('(*LIMIT_DEPTH=4294967280)abc','abc',0),
 ('(*LIMIT_DEPTH=10)(*LIMIT_DEPTH=1000)abc','abc',0),('(*LIMIT_MATCH=10)(*LIMIT_MATCH=1000)abc','abc',0),
 ('(*LIMIT_HEAP=10)(*LIMIT_HEAP=1000)abc','abc',0),('(*LIMIT_DEPTH=4294967289)abc','abc',0),
 ('(?(VERSION>=999)yes)^bc','bc',0),('^a\\Rb','a\nb',0),('^a\\X41z','aa41z',0),('^\\P{Any}?X','X',0),
 ('(?(DEFINE)(?<x>a?))^(?&x)a$','a',0),('(*scs:(1)a)?(a)','a',0),
 ('(?(1)^()|^)*b','b',0),('^(?=a(*PRUNE)b)','ab',0),('^(?=a(*SKIP)b|ac)','ac',0),('(?J)(?<x>.*)(?<x>a)(*scs:(<x>)b)','ab',32),
 ('(?J)(?<x>.*)(?<x>a)(?(R&x)b|c)','ac',32),('(?J)(?<x>.*)(?<x>a)(?(x)b|c)','ab',32),('(?<x>.*)(*scs:(<x>)b)','ab',32)]
inputs=[];commands=[]
for width in (8,16):
 header=[];rows=[]
 for utf in (False,True):
  for pattern,subject,flags in seeds:
   idx=len(rows);domain=(0 if width==8 else 2)+int(utf);request={'id':idx,'domain':domain,'pattern':pattern,'subject':subject,'flags':flags|(0x80000 if utf else 0)};rows.append(request)
   for name,text in (('p',pattern),('s',subject)):
    header.append('static PCRE2_UCHAR '+name+str(idx)+'[]={'+','.join(map(str,map(ord,text)))+(',0' if text else '0')+'};')
 inputs.extend(dict(r,width=width) for r in rows)
 header.append('static const struct {const PCRE2_UCHAR *pattern,*subject;size_t length,subject_length;unsigned flags,domain;} cases[]={'+','.join('{p%d,s%d,%d,%d,%du,%d}'%(r['id'],r['id'],len(r['pattern']),len(r['subject']),r['flags'],r['domain']) for r in rows)+'};')
 path=here/f'native-cases-{width}.h';path.write_text('\n'.join(header)+'\n');(temp/'native-cases.h').write_bytes(path.read_bytes())
 command=['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I',str(temp/'native-build'),'-I',str(temp),str(here/'native-probe.c'),str(temp/f'native-build/libpcre2-{width}.a'),'-o',str(temp/f'probe-{width}')];commands.append(command);subprocess.run(command,check=True)
 with (out/f'native-{width}.jsonl').open('w') as file:subprocess.run([str(temp/f'probe-{width}')],stdout=file,check=True)
(out/'native-inputs.json').write_text(json.dumps(inputs,indent=2)+'\n');(out/'native-provenance.json').write_text(json.dumps({'source_commit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','archive_sha256':sha(archive),'commands':commands,'libraries':receipt['members'],'outputs':{str(p.relative_to(root)):sha(p) for p in [out/f for f in ('native-inputs.json','native-8.jsonl','native-16.jsonl')]+[here/f for f in ('native-probe.c','native-cases-8.h','native-cases-16.h')]}},indent=2)+'\n');print(len(inputs))
