"""No-argument source/data verification; native helper exports tables, never executes a regex."""
from source import intake, alias_fixture, ROOT, PRODUCT, REPORT, TEST
from generate import projection
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import tempfile

manifest, source = intake()
products, summary = projection()
for name, content in products.items():
    assert (PRODUCT / name).read_bytes() == content.encode(), 'Projection changed: '+name
assert json.loads((REPORT / 'projection-manifest.json').read_text()) == summary
assert (TEST / 'Pcre2SourceAliases.g.cs').read_text() == alias_fixture(source)
print('All generated managed products reproduce byte-for-byte.', flush=True)

with tempfile.TemporaryDirectory(prefix='gregsex-pcre2-data-') as temp:
    temp = Path(temp)
    maint = temp / 'maint'
    maint.mkdir()
    for name, content in source.items():
        if name.startswith('maint/') and (name.endswith('.py') or name.startswith('maint/Unicode.tables/')):
            output = temp / name
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(content)
    env = dict(os.environ, PYTHONHASHSEED='0')
    for script, output in [('GenerateUcpHeader.py','pcre2_ucp.h'),('GenerateUcpTables.py','pcre2_ucptables.c'),('GenerateUcd.py','pcre2_ucd.c')]:
        result = subprocess.run([sys.executable,script],cwd=maint,env=env,check=True,text=True,capture_output=True)
        if result.stdout: print(result.stdout,end='',flush=True)
        if result.stderr: print(result.stderr,end='',flush=True)
        actual = (maint / output).read_bytes()
        assert actual == source['src/'+output], 'Upstream regenerated output differs: '+output
        print('Exact upstream regeneration:', output, hashlib.sha256(actual).hexdigest(),flush=True)
    # Compile actual pinned table declarations with a minimal stand-alone read-only exporter.
    # It reads native records directly; no managed compression/offset projection is reused.
    for name in ['pcre2_ucd.c','pcre2_chartables.c.dist']:
        text = source['src/'+name].decode()
        text = '\n'.join(line for line in text.splitlines() if not line.lstrip().startswith('#include'))
        (temp / name).write_text(text)
    (temp / 'export.c').write_text(r'''
#include <stdint.h>
#include <stdio.h>
#define PRIV(x) x
#define SUPPORT_UNICODE 1
#define PCRE2_CODE_UNIT_WIDTH 16
#define UCD_BLOCK_SIZE 128
#define NOTACHAR 0xffffffff
typedef struct { uint8_t script,chartype,gbprop,caseset; int32_t other_case; uint16_t scriptx_bidiclass,bprops; } ucd_record;
#include "pcre2_ucd.c"
#include "pcre2_chartables.c.dist"
static void put(uint32_t n) { unsigned char b[4] = {n,n>>8,n>>16,n>>24}; fwrite(b,1,4,stdout); }
int main(void) {
  for (uint32_t cp=0;cp<0x110000;cp++) {
    const ucd_record *r=ucd_records+ucd_stage2[ucd_stage1[cp/128]*128+cp%128];
    uint32_t count=0; if(r->caseset) while(ucd_caseless_sets[r->caseset+count]!=NOTACHAR) count++;
    put(r->chartype); put(r->script); put(r->scriptx_bidiclass>>11); put(r->gbprop); put(r->other_case); put(count);
    for(uint32_t i=0;i<count;i++) put(ucd_caseless_sets[r->caseset+i]);
    uint32_t sx=r->scriptx_bidiclass&0x3ff;
    for(uint32_t i=0;i<6;i++) put(sx ? (i<4?ucd_script_sets[sx+i]:0) : (i==r->script/32?1u<<(r->script%32):0));
    put(ucd_boolprop_sets[r->bprops]); put(ucd_boolprop_sets[r->bprops+1]);
    uint32_t ds=0; if(r->chartype==13) { ds=1; while(cp>ucd_digit_sets[ds]) ds++; } put(ds);
    uint32_t lower=cp,upper=cp;
    if(cp<128) {
      lower = default_tables[512+4*32+cp/8]&(1u<<(cp%8))?cp:default_tables[256+cp];
      upper = default_tables[512+3*32+cp/8]&(1u<<(cp%8))?cp:default_tables[256+cp];
    } else if(r->chartype>=5&&r->chartype<=9) {
      if(r->chartype!=5) lower=cp+r->other_case;
      if(r->chartype!=9) upper=cp+r->other_case;
    }
    put(lower);put(upper);put(upper);
  }
  return ferror(stdout)?1:0;
}
''')
    compile_result = subprocess.run(['cc','-std=c99','-O2','export.c','-o','export'],cwd=temp,check=True,capture_output=True,text=True)
    if compile_result.stderr: print(compile_result.stderr,end='',flush=True)
    with (temp/'native.bin').open('wb') as stream:
        subprocess.run([str(temp/'export')],stdout=stream,check=True)
    h=hashlib.sha256()
    with (temp/'native.bin').open('rb') as stream:
        while chunk := stream.read(1024*1024): h.update(chunk)
    assert h.hexdigest()==summary['exhaustive_sha256']
    print('All1114112 native codepoint rows match canonical projection SHA256:',h.hexdigest(),flush=True)
print('Source regeneration, independent native table export, ABI projection and product hashes pass.')
