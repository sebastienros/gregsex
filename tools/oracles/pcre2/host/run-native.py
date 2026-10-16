"""Finite source-only observation intake. No product-derived expectations."""
import hashlib,json,pathlib,subprocess,tarfile
root=pathlib.Path(__file__).resolve().parents[4]
here=pathlib.Path(__file__).resolve().parent
out=root/'test/corpora/pcre2/host'; temp=pathlib.Path('/private/tmp/gregsex-m5-host-native');temp.mkdir(exist_ok=True)
archive=root/'test/corpora/pcre2/oracle/native-evidence.tar.gz'
expected={'pcre2.h':'a75457e8b140a246aade9c8b36b44fbe241dfa253fce4545524b27d7d7e9b5c2','libpcre2-8.a':'460a19b179518523bc5cbf2162b5f0263c6f5a5113ae7fd0ad61c1f53ab4c531','libpcre2-16.a':'e0bbe10f169a236b10f5d5a939a10bd0a617466c2359226de847317235f84bed'}
assert hashlib.sha256(archive.read_bytes()).hexdigest()=='339344898646d67a24c61e244a46ddf345ac8f55c6aacf9b7b32b7c34645bb71'
with tarfile.open(archive) as tf:
 for name,digest in expected.items():
  data=tf.extractfile('native-build/'+name).read();assert hashlib.sha256(data).hexdigest()==digest;(temp/name).write_bytes(data)
G=256;E=512;O=4096
cases=[]
def add(p,s,r,flags=0,compile=0,extra=0,capacity=128,pairs=0,start=0,sub=999,case=0):
 cases.append(dict(id=f'HOST-N{len(cases)+1:03}',pattern=p,subject=s,replacement=r,flags=flags,compile=compile,extra=extra,capacity=capacity,pairs=pairs,start=start,sub=sub,case=case))
for r in ['$1$1','$&|$`|$\'|$_','$$','${1}','${1:+YES:NO}','${1:-default}',r'\1\g<1>',r'\x{41}\o{102}\cC\b\v\n',r'\U$1\Eabc',r'\u\LAbC',r'\l\UAbC',r'\Q$1\E!','$9','${','${1','${1:?bad}',r'\d',r'\x','\\',r'\g<unknown>',r'\g<9>']:
 add('(a)','zaq',r,E)
for r in ['${1:+YES:NO}','${1:-default}','${1:+${2:-x}:none}','$1','$9','${9:-missing}']:
 add('(a)?b','b',r,E|2048)
add('(?J)(?<x>a)|(?<x>b)','b','$x$<x>')
add('a(*MARK:yes)','a','${*MARK}')
add('','ab','X',G)
add('(?:|a)','a','X',G)
add('(?<=\\G.)','ab','X',G)
add('a','aaa','LONG',G|O,capacity=2)
add('a','a','x',capacity=1)
add('a','aa','',G|O,capacity=0,sub=0)
add('a+','aaa','',G|O,capacity=0,sub=1)
add('a','aa','LONG',G,sub=1)
add('a','aa','LONG',G,sub=-1)
for r in [r'\Uabc\Edef',r'\uabc',r'\lABC',r'\l\UAbc',r'\u\LAbc',r'\Uabc\Edef']:
 add('a','a',r,E,case=1)
add('a','a',r'\Uabc',E,case=2)
add('a','a',r'\uABC',E,case=3)
add('a','a',r'\l\UABC',E,case=4,capacity=12)
add('a','a',r'\Uabc',E|O,case=1,capacity=1)
add('a','a',r'\uabc',E|O,case=4,capacity=4)
add('(a)','a','$1$1',pairs=1)
add('(a)','a','$1$1',pairs=2)
add('(a)','a','$1$1')
add('.','µ',r'\U$0',E,compile=0x20000)
add('.','µ',r'\U$0',E,compile=0xA0000)
add('.','😀',r'\u$0',E,compile=0xA0000)
add('a','aaa',r'\Q$1',G|E)
(out/'native-inputs.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2)+'\n')
raw=[]; commands=[]
for width in [8,16]:
 lines=[]; rows=[]
 for i,c in enumerate(cases):
  def encode(s):
   if width==8:return list(s.encode('utf-8' if c['compile']&0x80000 else 'latin1'))
   b=s.encode('utf-16-le');return [b[j]+256*b[j+1] for j in range(0,len(b),2)]
  if width==8 and any(ord(ch)>255 for s in [c['pattern'],c['subject'],c['replacement']] for ch in s) and not c['compile']&0x80000:continue
  arrays=[];lens=[]
  for field in ['pattern','subject','replacement']:
   units=encode(c[field]);name=f'v{i}_{field}';lines.append(f'static const PCRE2_UCHAR {name}[]={{'+','.join(map(str,units+[0]))+'};');arrays.append(name);lens.append(len(units))
  values=[f'"{c["id"]}"',*arrays,*map(str,lens),*map(str,[c['compile'],c['extra'],c['flags'],c['capacity'],c['pairs'],c['start'],c['sub'],c['case']])]
  rows.append('{'+','.join(values)+'}')
 (temp/'native-cases.h').write_text('\n'.join(lines)+'\nstatic const Case inputs[]={'+',\n'.join(rows)+'};\n')
 command=['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I'+str(temp),str(here/'native-probes.c'),str(temp/f'libpcre2-{width}.a'),'-o',str(temp/f'probe-{width}')]
 subprocess.run(command,check=True);commands.append(command)
 result=subprocess.run([str(temp/f'probe-{width}')],check=True,capture_output=True,text=True);raw.append(result.stdout)
(out/'native-probes.jsonl').write_text(''.join(raw))
(out/'native-identity.json').write_text(json.dumps(dict(sourceCommit='b2bd4254b379b9d7dc9a3dda060a7e27009ccdff',archiveSha256=hashlib.sha256(archive.read_bytes()).hexdigest(),members=expected,commands=commands),indent=2)+'\n')
print(len(cases),'bounded input descriptors;',sum(1 for line in ''.join(raw).splitlines() if json.loads(line)['event']=='result'),'native results')
