"""Reproduce bounded native INFO probes using libraries from the retained archive.
No build of PCRE2, matcher execution, timing, or product-derived expectations.
"""
import hashlib,json,pathlib,subprocess,tarfile
root=pathlib.Path(__file__).resolve().parents[4]
here=pathlib.Path(__file__).resolve().parent
out=root/'test/corpora/pcre2/compile-metadata'
work=pathlib.Path('/private/tmp/gregsex-m5-compile-metadata-native')
work.mkdir(exist_ok=True)
with tarfile.open(root/'test/corpora/pcre2/oracle/native-evidence.tar.gz') as t:
 for name in ['native-build/pcre2.h','native-build/libpcre2-8.a','native-build/libpcre2-16.a']:
  t.extract(name,work,filter='data')
patterns=[
 ('empty',''),('literal-lf','\n'),('literal-cr','\r'),('hex-lf',r'\x0a'),('octal-cr',r'\015'),('control-lf',r'\cJ'),
 ('braced-lf',r'\x{a}'),('vertical',r'\v'),('space',r'\s'),('newline',r'\R'),('dot','.'),
 ('positive-single',r'[\n]'),('negative-single',r'[^\n]'),('negative-cr',r'[^\r]'),
 ('positive-duplicate',r'[\n\n]'),('negative-duplicate',r'[^\n\n]'),('negative-multiple',r'[^\na]'),
 ('positive-equal-range',r'[\n-\n]'),('negative-equal-range',r'[^\n-\n]'),
 ('negative-equal-repeat',r'[^\n-\n]{0}a'),('inside-range',r'[\x09-\x0e]'),
 ('start-endpoint',r'[\x0a-\x0e]'),('end-endpoint',r'[\x09-\x0d]'),
 ('negative-inside-range',r'[^\x09-\x0e]'),('negative-start-endpoint',r'[^\x0a-\x0e]'),
 ('class-vertical',r'[\v]'),('class-space',r'[\s]'),('class-posix-space','[[:space:]]'),
 ('class-posix-blank','[[:blank:]]'),('class-properties',r'[\p{Z}]'),
 ('zero-literal',r'\n{0}a'),('zero-group',r'(\n){0}a'),('define',r'(?(DEFINE)(a\n))a'),
 ('unreachable',r'a(*ACCEPT)|\n'),('assertion',r'(?=\n)a'),('comment', '(?#\n)a'),
 ('quoted-escape',r'\Q\n\E'),('quoted-literal','\\Q\n\\E'),('class-quoted-escape',r'[\Q\n\E]'),
 ('negative-quoted-literal','[^\\Q\n\\E]'),('negative-quoted-duplicate','[^\\Q\n\n\\E]'),
 ('extended-whitespace','(?x)\na'),('extended-comment','(?x)# hello\r\na'),
 ('extended-escaped',r'(?x)\n'),('extended-class','(?xx)[\n ]'),('mark-string','(*MARK:\n)a'),
 ('callout-string',"(?C'\n')a"),('newline-directive','(*CRLF)a'),
 ('perl-leaf',r'(?[ [\n] ])'),('perl-negative-leaf',r'(?[ [^\n] ])'),
 ('perl-negation',r'(?[ !\n ])'),('perl-double-negation',r'(?[ !!\n ])'),
 ('perl-subtract',r'(?[ [\n] - [\n] ])'),('perl-intersection',r'(?[ [\n] & [a] ])'),
 ('perl-vertical',r'(?[ [\v] | [a] ])'),('perl-range',r'(?[ [\x09-\x0e] ])'),
 ('perl-raw-whitespace','(?[\n [a] ])'),
 ('perl-space','(?[  [a] ])'),('perl-tab','(?[\t [a] ])'),
 ('perl-vtab','(?[\v [a] ])'),('perl-formfeed','(?[\f [a] ])'),('perl-carriage','(?[\r [a] ])'),('bad-literal-escape','\\\n'),
]
patterns += [('perl-nested-unit-'+str(ord(c)), '(?[ ['+c+'a] ])') for c in [' ', '\t', '\n', '\v', '\f', '\r']]
variants=[('literal-spelling',r'\n',0x02000000,0),('literal-real','\n',0x02000000,0),
 ('normalize-cr',r'\r',0,0x10),('normalize-negative-cr',r'[^\r]',0,0x10),
 ('alt-plain-negative',r'[^\n]',0x08000000,0),('alt-nested-negative',r'[[^\n]]',0x08000000,0),
 ('alt-subtraction',r'[\n--\n]',0x08000000,0),('alt-vertical',r'[[\v]]',0x08000000,0),
 ('alt-negative-outer',r'[^\n--a]',0x08000000,0),('alt-union',r'[\n||a]',0x08000000,0),
 ('alt-u-lf',r'\u000a',2,0),('bad-escape-literal',r'\q\n',0,2),
 ('imported-first',r'[a\n]',0,0),('imported-second',r'[\na]',0,0),
 ('imported-duplicate',r'[aa\n]',0,0),('imported-negative',r'[^a\n]',0,0)]
rows=[]
for width in [8,16]:
 for utf in [False,True]:
  for id,pattern,flags,extra in [(id,p,0,0) for id,p in patterns]+variants:
   flags|=0x80000 if utf else 0
   units=list(pattern.encode('utf-8')) if width==8 and utf else [ord(c) for c in pattern]
   rows.append(dict(id=f'{id}/{width}/{int(utf)}',pattern=pattern,width=width,flags=flags,extra=extra,units=units,imported=id.startswith('imported-')))
# Supplementary source maps, Unicode property escapes, source UTF directives.
for width in [8,16]:
 for pattern in ['😀\\n', '😀[^\\n]',r'\N{U+000a}', '(*UTF)\\n']:
  flags=0x80000
  units=list(pattern.encode('utf-8')) if width==8 else [int.from_bytes(pattern.encode('utf-16-le')[i:i+2],'little') for i in range(0,len(pattern.encode('utf-16-le')),2)]
  rows.append(dict(id=f'mapped/{len(rows)}',pattern=pattern,width=width,flags=flags,extra=0,units=units,imported=False))
(out/'native-inputs.json').write_text(json.dumps(rows,ensure_ascii=True,indent=2)+'\n')
helper=r'''/* Bounded development oracle only. Uses retained PCRE2-10.46 libraries. */
#include "pcre2.h"
#include <stdio.h>
#include <string.h>
#include "cases.h"
int main(void) {
 for(size_t i=0;i<sizeof(cases)/sizeof(*cases);i++) {
  const struct probe *p=cases+i; int error;PCRE2_SIZE offset;
  pcre2_compile_context *cc=pcre2_compile_context_create(NULL);
  pcre2_set_compile_extra_options(cc,p->extra);
  unsigned char tables[1088];const unsigned char *defaults=pcre2_maketables(NULL);
  memcpy(tables,defaults,1088);pcre2_maketables_free(NULL,defaults);
  if(p->imported){tables[256+'a']=10;tables[256+10]='a';pcre2_set_character_tables(cc,tables);}
  pcre2_code *code=pcre2_compile(p->units,p->length,p->flags,&error,&offset,cc);
  uint32_t value=0;if(code)pcre2_pattern_info(code,PCRE2_INFO_HASCRORLF,&value);
  printf("{\"id\":\"%s\",\"accepted\":%s,\"error\":%d,\"offset\":%zu,\"hasCrOrLf\":%u}\n",p->id,code?"true":"false",code?0:error,code?0:offset,value);
  if(code)pcre2_code_free(code);pcre2_compile_context_free(cc);
 }
 return 0;
}
'''
(here/'native-probe.c').write_text(helper)
results=[];commands=[]
for width in [8,16]:
 items=[r for r in rows if r['width']==width]
 header='struct probe {const char *id;const PCRE2_UCHAR *units;size_t length;uint32_t flags,extra;int imported;};\n'
 for i,r in enumerate(items):header+=f'static const PCRE2_UCHAR pattern{i}[]={{'+','.join(map(str,r['units'] or [0]))+'};\n'
 header+='static const struct probe cases[]={\n'
 for i,r in enumerate(items):header+='{'+json.dumps(r['id'])+f',pattern{i},{len(r["units"])},{r["flags"]}u,{r["extra"]}u,{int(r["imported"])}'+'},\n'
 header+='};\n'
 casepath=here/f'native-cases-{width}.h';casepath.write_text(header)
 (work/'cases.h').write_text(header)
 command=['clang','-O2',f'-DPCRE2_CODE_UNIT_WIDTH={width}','-I',str(work/'native-build'),'-I',str(work),str(here/'native-probe.c'),str(work/f'native-build/libpcre2-{width}.a'),'-o',str(work/f'probe-{width}')]
 commands.append(command);subprocess.run(command,check=True)
 raw=subprocess.check_output([str(work/f'probe-{width}')],text=True)
 (out/f'native-{width}.jsonl').write_text(raw)
 results.extend(json.loads(line) for line in raw.splitlines())
print(f'{len(results)} bounded native INFO observations; rejected={sum(not r["accepted"] for r in results)}')
artifacts=['tools/oracles/pcre2/source/pcre2-10.46-source.tar.gz','test/corpora/pcre2/oracle/native-evidence.tar.gz']
manifest=dict(source_commit='b2bd4254b379b9d7dc9a3dda060a7e27009ccdff',commands=commands,sha256={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [root/a for a in artifacts]+[p for p in out.glob('native-*') if p.name!='native-provenance.json']+list(here.glob('native-*'))+[here/'probe.py']},library_sha256={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in (work/'native-build').glob('*.a')})
(out/'native-provenance.json').write_text(json.dumps(manifest,indent=2)+'\n')
