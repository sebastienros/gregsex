"""Fixed, argument-free pinned native build using upstream NON-AUTOTOOLS-BUILD."""
from pathlib import Path
import hashlib, json, os, platform, shutil, subprocess, tarfile
ROOT=Path(__file__).resolve().parents[3]
HERE=Path(__file__).resolve().parent
OUT=ROOT/'test/corpora/pcre2/oracle'
WORK=HERE/'.artifacts'
SRC=WORK/'source'
BUILD=WORK/'build'
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def run(argv, name, cwd=BUILD):
    p=subprocess.run([str(x) for x in argv],cwd=cwd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=300)
    (OUT/name).write_bytes(p.stdout)
    if p.returncode: raise RuntimeError(f'{name}: exit {p.returncode}')
    return p.stdout.decode(errors='replace')
def main():
    OUT.mkdir(parents=True,exist_ok=True); WORK.mkdir(exist_ok=True); SRC.mkdir(exist_ok=True); BUILD.mkdir(exist_ok=True)
    manifest=json.loads((ROOT/'tools/oracles/pcre2/source/source-manifest.json').read_text())
    archive=ROOT/'tools/oracles/pcre2/source/pcre2-10.46-source.tar.gz'
    assert digest(archive)==manifest['archive_sha256']
    with tarfile.open(archive) as z:
        by={m.name.split('/',1)[1]:m for m in z.getmembers() if m.isfile()}
        assert len(by)==len(manifest['files'])==488
        for m in manifest['files']:
            data=z.extractfile(by[m['path']]).read()
            assert len(data)==m['bytes'] and hashlib.sha256(data).hexdigest()==m['sha256']
            target=SRC/m['path']; assert target.resolve().is_relative_to(SRC.resolve())
            target.parent.mkdir(parents=True,exist_ok=True); target.write_bytes(data)
    s=SRC/'src'
    shutil.copyfile(s/'config.h.generic',s/'config.h'); shutil.copyfile(s/'pcre2.h.generic',s/'pcre2.h')
    shutil.copyfile(s/'pcre2_chartables.c.dist',s/'pcre2_chartables.c')
    cc=shutil.which('clang'); assert cc
    flags=['-O2','-DHAVE_CONFIG_H','-DSUPPORT_PCRE2_8','-DSUPPORT_PCRE2_16','-DSUPPORT_UNICODE','-DHAVE_STRERROR','-DHAVE_UNISTD_H','-DHAVE_SYS_RESOURCE_H','-I'+str(s)]
    # Exactly the source list in NON-AUTOTOOLS-BUILD step4. JIT stubs mandatory;
    # DFA/conversion/serialization/POSIX are only upstream diagnostic dependencies.
    modules='auto_possess chkdint chartables compile compile_class config context convert dfa_match error extuni find_bracket jit_compile maketables match match_data newline ord2utf pattern_info script_run serialize string_utils study substitute substring tables ucd valid_utf xclass'.split()
    commands=[]
    for width in [8,16]:
        objects=[]
        for module in modules:
            obj=BUILD/f'{module}-{width}.o'; cmd=[cc,*flags,f'-DPCRE2_CODE_UNIT_WIDTH={width}','-c',s/f'pcre2_{module}.c','-o',obj]
            commands.append([str(x) for x in cmd]); run(cmd,f'compile-{module}-{width}.log'); objects.append(obj)
        cmd=['/usr/bin/ar','rcs',BUILD/f'libpcre2-{width}.a',*objects]; commands.append([str(x) for x in cmd]);run(cmd,f'archive-{width}.log')
    cmd=[cc,*flags,'-DPCRE2_CODE_UNIT_WIDTH=8','-c',s/'pcre2posix.c','-o',BUILD/'posix.o']
    commands.append([str(x) for x in cmd]);run(cmd,'compile-posix.log')
    cmd=[cc,*flags,s/'pcre2test.c',BUILD/'posix.o',BUILD/'libpcre2-8.a',BUILD/'libpcre2-16.a','-o',BUILD/'pcre2test']
    commands.append([str(x) for x in cmd]);run(cmd,'link-pcre2test.log')
    config=run([BUILD/'pcre2test','-C'],'native-config.log')
    compiler=run([cc,'--version'],'compiler.log')
    record={'schemaVersion':'pcre2-native-build-1','sourceCommit':manifest['commit'],'archiveSha256':manifest['archive_sha256'],
      'verifiedMembers':488,'recipe':'NON-AUTOTOOLS-BUILD steps1–9; no SUPPORT_JIT,32,EBCDIC',
      'platform':platform.platform(),'machine':platform.machine(),'compiler':compiler,'commands':commands,'nativeConfig':config,
      'environment':{k:os.environ.get(k) for k in ['MACOSX_DEPLOYMENT_TARGET','SDKROOT','CPATH','CFLAGS','LANG','LC_ALL']},
      'artifacts':[{'path':str(p.relative_to(ROOT)),'sha256':digest(p),'bytes':p.stat().st_size} for p in [s/'config.h',s/'pcre2.h',s/'pcre2_chartables.c',BUILD/'libpcre2-8.a',BUILD/'libpcre2-16.a',BUILD/'pcre2test']]}
    (OUT/'build-identity.json').write_text(json.dumps(record,indent=2)+'\n')
    print(config); print('Verified488source members; built exact8/16 interpreter; identities recorded.')
if __name__=='__main__': main()
