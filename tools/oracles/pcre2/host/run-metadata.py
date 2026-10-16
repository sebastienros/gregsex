import pathlib,json,subprocess
here=pathlib.Path(__file__).resolve().parent
out=here.parents[3]/'test/corpora/pcre2/host';temp=pathlib.Path('/private/tmp/gregsex-m5-host-native')
# Libraries/header were separately verified by run-native.py. No rebuild.
inputs=[('SUBROUTINE','(a)(?1)',0),('CONDITION','(a)(?(1)b|c)',0),('REFERENCE',r'(a)\1',0),('RECCONDITION','(a)(?(R1)b|c)',0),('NAMEDREF',r'(?J)(?<x>a)(?<x>b)\k<x>',0),('CZERO',r'\C{0}',0),('CDEFINE',r'(?(DEFINE)\C)',0),('CUNREACH',r'(*FAIL)\C',0),('JREDUNDANT','(?J)',0x40),('JTOGGLES','(?J)(?-J)',0),('JARGONLY','a',0x40),('NAMESUTF','(?<𐐀>a)(?<Ａ>b)',0x80000)]
(out/'metadata-inputs.json').write_text(json.dumps([dict(id=i,pattern=p,flags=f) for i,p,f in inputs],ensure_ascii=False,indent=2)+'\n')
raw=[]
for width in [8,16]:
 declarations=[];rows=[]
 for j,(ident,p,f) in enumerate(inputs):
  b=p.encode('utf-8' if width==8 else 'utf-16-le'); units=list(b) if width==8 else [b[k]+256*b[k+1] for k in range(0,len(b),2)]
  declarations.append(f'static const PCRE2_UCHAR v{j}[]={{'+','.join(map(str,units+[0]))+'};');rows.append('{"'+ident+'",v'+str(j)+','+str(len(units))+','+str(f)+'}')
 (temp/'metadata-cases.h').write_text('\n'.join(declarations)+'\nstatic const struct{const char *id;const PCRE2_UCHAR *pattern;size_t length;unsigned flags;} inputs[]={'+','.join(rows)+'};\n')
 subprocess.run(['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I'+str(temp),str(here/'native-metadata.c'),str(temp/f'libpcre2-{width}.a'),'-o',str(temp/f'metadata-{width}')],check=True)
 raw.append(subprocess.run([str(temp/f'metadata-{width}')],check=True,capture_output=True,text=True).stdout)
(out/'native-metadata.jsonl').write_text(''.join(raw));print(len(inputs)*2,'finite metadata plans')
