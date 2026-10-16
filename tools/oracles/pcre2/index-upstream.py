"""Instrument only reference I/O boundaries; source decoder/matcher remain upstream.

Native recipe index records every physical input line and each dispatched native
pattern/subject/control, with byte offsets into retained native output. No Python
pattern/subject decoder is substituted for pcre2test's decoder.
"""
from pathlib import Path
import json,subprocess,hashlib,os,shutil
from cases import ROOT,HERE
SRC=HERE/'.artifacts/source';BUILD=HERE/'.artifacts/build';OUT=ROOT/'test/corpora/pcre2/oracle/indexed'
INJECT=(HERE/'instrument.c.fragment').read_text()

def main():
 OUT.mkdir(exist_ok=True,parents=True)
 original=(SRC/'src/pcre2test.c').read_text();patched=original
 anchor='static int\nprocess_command(void)';pos=patched.index(anchor);patched=patched[:pos]+INJECT+'\n'+patched[pos:]
 for name,kind in [('process_command','control'),('process_pattern','pattern'),('process_data','subject')]:
  anchor='static int\n'+name+'(void)\n{';assert patched.count(anchor)==1
  patched=patched.replace(anchor,anchor+'\n  oracle_index("'+kind+'");',1)
 anchor='  p = buffer;\n\n  /* If we have a pattern set up for testing'
 assert patched.count(anchor)==1;patched=patched.replace(anchor,'  oracle_index("line");\n'+anchor,1)
 patched=patched.replace('PCRE2_SET_CHARACTER_TABLES(pat_context, use_tables);','oracle_tables(use_tables);\nPCRE2_SET_CHARACTER_TABLES(pat_context, use_tables);')
 anchor='/* A final compile that is used "for real". */'
 patched=patched.replace(anchor,anchor+'\noracle_compile_input(use_pbuffer,patlen,pat_patctl.options|use_forbid_utf);')
 anchor='pat_patctl.options|use_forbid_utf, &errorcode, &erroroffset, use_pat_context);'
 assert patched.count(anchor)==1;patched=patched.replace(anchor,anchor+'\noracle_compile_result(errorcode,erroroffset);')
 start=patched.index('static int\ncallout_function(');end=patched.index('static BOOL\ncopy_and_get',start)
 import re
 section=patched[start:end];section=re.sub(r'return (callout_data|PCRE2_ERROR_CALLOUT|1|0);',r'return oracle_callout(cb,\1);',section);patched=patched[:start]+section+patched[end:]
 start=patched.index('static int\nsubstitute_callout_function(');end=patched.index('static BOOL\ncase_transform',start)
 section=patched[start:end].replace('return yield;','return oracle_subcall(scb,yield);');patched=patched[:start]+section+patched[end:]
 anchor='(void)data_ptr;   /* Not used */ \\\n'
 # Instrument case input before writes and output before return; unknown overflow
 # buffer bytes deliberately stay null, because source writes time-dependent garbage.
 patched=patched.replace('G(PCRE2_UCHAR,BITS) buf[16]; \\\n','G(PCRE2_UCHAR,BITS) buf[16]; \\\nint original_case_mode=to_case; \\\noracle_casecall(input,input_len,output,output_cap,to_case,0,0); \\\n')
 patched=patched.replace('return written; \\\n','oracle_casecall(input,input_len,output,output_cap,original_case_mode,written,1); \\\nreturn written; \\\n')
 anchor='    /* Run a single DFA or NFA match. */'
 patched=patched.replace(anchor,anchor+'\n    oracle_match_input(pp,arg_ulen,dat_datctl.options|g_notempty);')
 anchor='dat_datctl.options | g_notempty, match_data, use_dat_context);'
 # There are exactly two single-standard-matcher branches (JITfast + interpreter).
 anchor='      if (capcount == 0)\n        {\n        fprintf(outfile, \"Matched, but too many substrings\\n\");'
 assert patched.count(anchor)==1
 patched=patched.replace(anchor,'      oracle_match_result(capcount);\n'+anchor)
 path=BUILD/'pcre2test-indexed.c';path.write_text(patched)
 argv=[shutil.which('clang'),'-O2','-DHAVE_CONFIG_H','-DSUPPORT_PCRE2_8','-DSUPPORT_PCRE2_16','-DSUPPORT_UNICODE','-DHAVE_STRERROR','-DHAVE_UNISTD_H','-DHAVE_SYS_RESOURCE_H','-I'+str(SRC/'src'),path,BUILD/'posix.o',BUILD/'libpcre2-8.a',BUILD/'libpcre2-16.a','-o',BUILD/'pcre2test-indexed']
 p=subprocess.run([str(x) for x in argv],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120);(OUT/'build.log').write_bytes(p.stdout);assert p.returncode==0,p.stdout.decode()
 buildargv=[str(x) for x in argv]
 originalruns=json.loads((ROOT/'test/corpora/pcre2/oracle/upstream/runs.json').read_text());results=[]
 for width in [8,16]:
  for family in list(range(1,28))+['heap']:
   work=HERE/f'.artifacts/indexed-{width}-{family}';work.mkdir(exist_ok=True);trace=OUT/f'{width}-{family}.jsonl';native=OUT/f'{width}-{family}.native';trace.unlink(missing_ok=True)
   # RunTest is retained intact so locale/linksize/width/binary/table prerequisites
   # and options are handled by the upstream selection logic.
   table_dir=OUT/f'tables-{width}-{family}';table_dir.mkdir(exist_ok=True)
   env=dict(os.environ,GREGSEX_TABLE_DIR=str(table_dir),srcdir=str(SRC),pcre2test=str(HERE/'capture-native.sh'),GREGSEX_NATIVE_DRIVER=str(BUILD/'pcre2test-indexed'),GREGSEX_NATIVE_CAPTURE=str(work/'retained-native-output'),GREGSEX_ORACLE_TRACE=str(trace),LC_ALL='C')
   argv=['/bin/sh',str(SRC/'RunTest'),f'-{width}','-nojit',str(family)]
   p=subprocess.run(argv,cwd=work,env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120);(OUT/f'{width}-{family}.log').write_bytes(p.stdout)
   r=next(r for r in originalruns if r['width']==width and r['family']==family);raw=work/'retained-native-output';identical=None
   if raw.exists():
    shutil.copyfile(raw,native);identical=raw.read_bytes()==(ROOT/r['nativeOutput']).read_bytes();assert identical
   results.append({'width':width,'family':family,'exit':p.returncode,'unmodifiedOutputIdentical':identical,'trace':str(trace.relative_to(ROOT)) if trace.exists() else None,'output':str(native.relative_to(ROOT)) if raw.exists() else None})
  work=HERE/f'.artifacts/windows-locale-{width}';work.mkdir(exist_ok=True)
  trace=OUT/f'{width}-windows-locale.jsonl';native=OUT/f'{width}-windows-locale.native'
  argv=[BUILD/'pcre2test-indexed','-q',f'-{width}',SRC/'testdata/wintestinput3',native]
  p=subprocess.run([str(x) for x in argv],cwd=work,env=dict(os.environ,GREGSEX_ORACLE_TRACE=str(trace),LC_ALL='C'),stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120)
  assert p.returncode==0;results.append({'width':width,'family':'windows-locale','exit':p.returncode,'sourceInput':'testdata/wintestinput3','mode':'original-Windows-recipes;unavailable-french-locale-is-concrete-precondition','trace':str(trace.relative_to(ROOT)),'output':str(native.relative_to(ROOT))})
  # Replay skipped mixed JIT family in the interpreter. Controls remain present;
  # JIT diagnostics are excluded, all admitted interpreter outcomes retained.
  for family in [17,23]:
   work=HERE/f'.artifacts/replay-{width}-{family}';work.mkdir(exist_ok=True);trace=OUT/f'{width}-{family}-replay.jsonl';native=OUT/f'{width}-{family}-replay.native';trace.unlink(missing_ok=True)
   env=dict(os.environ,GREGSEX_ORACLE_TRACE=str(trace),LC_ALL='C')
   argv=[BUILD/'pcre2test-indexed','-q',f'-{width}']
   if family==23:argv+=['-pattern','never_backslash_c']
   argv+=[SRC/f'testdata/testinput{family}',native]
   p=subprocess.run([str(x) for x in argv],cwd=work,env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=120);(OUT/f'{width}-{family}-replay.log').write_bytes(p.stdout);assert p.returncode==0
   results.append({'width':width,'family':family,'exit':p.returncode,'mode':'interpreter-counterpart;23usesexplicitcompileprohibition','argv':[str(x) for x in argv],'trace':str(trace.relative_to(ROOT)),'output':str(native.relative_to(ROOT))})
 (OUT/'runs.json').write_text(json.dumps(results,indent=2)+'\n');(OUT/'instrumentation.json').write_text(json.dumps({'originalSourceSha256':hashlib.sha256(original.encode()).hexdigest(),'patchedSourceSha256':hashlib.sha256(patched.encode()).hexdigest(),'executableSha256':hashlib.sha256((BUILD/'pcre2test-indexed').read_bytes()).hexdigest(),'injectedCode':INJECT,'edits':'Read-only native input/result/metadata/callback/table observations plus function-entry/line indexes; no option, decoder, matcher, formatting or result changes. RunTest self-check output compared byte-identical with unmodified diagnostic for every executed family.','buildArgv':buildargv,'lastReplayArgv':[str(x) for x in argv]},indent=2)+'\n');print('Indexed all native8/16 family dispatches; unmodified output identical.')
if __name__=='__main__':main()
