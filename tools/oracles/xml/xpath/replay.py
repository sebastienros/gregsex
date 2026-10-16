"""Fixed bounded processor replay. Configuration uses explicit environment paths, no CLI arguments."""
import os,pathlib,subprocess
ROOT=pathlib.Path(__file__).resolve().parents[4];os.chdir(ROOT)
if __import__('sys').argv[1:]:raise SystemExit('No arguments; see README for environment configuration.')
cache=pathlib.Path(os.environ.get('GREGSEX_XPATH_ORACLE_CACHE','/private/tmp/gregsex-xpath-intake'))
java=pathlib.Path(os.environ.get('GREGSEX_XPATH_JAVA_HOME','/Library/Java/JavaVirtualMachines/temurin-25.jdk/Contents/Home'))
subprocess.run(['python3','tools/oracles/xml/xpath/qualify.py'],check=True,timeout=60)
classes=cache/'replay-classes';classes.mkdir(exist_ok=True)
subprocess.run([str(java/'bin/javac'),'-cp',str(cache/'Saxon-HE-12.5.jar'),'-d',str(classes),'tools/oracles/xml/xpath/Replay.java'],check=True,timeout=60)
cp=os.pathsep.join(str(p) for p in [classes,cache/'Saxon-HE-12.5.jar',cache/'xmlresolver-5.2.2.jar',cache/'xmlresolver-5.2.2-data.jar'])
subprocess.run([str(java/'bin/java'),'-Xmx512m','-cp',cp,'Replay'],check=True,timeout=60)
subprocess.run(['python3','tools/oracles/xml/xpath/verify.py'],check=True,timeout=60)
