// No CLI parser: executes precisely the frozen intake, both required strictness variants.
import {pathToFileURL} from 'node:url';
import fs from 'node:fs'; import vm from 'node:vm'; import crypto from 'node:crypto';
const root=new URL('../../../',import.meta.url), source=new URL('test262/',import.meta.url);
const intake=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/test262-intake.json',root)));
const pin=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle-pin.json',root)));
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
function verify(url,item){const b=fs.readFileSync(url);if(b.length!==item.length||sha(b)!==item.sha256)throw Error(`Identity mismatch: ${url}`);return b.toString()}
if(sha(fs.readFileSync(fs.realpathSync(process.execPath)))!==pin.sha256||JSON.stringify(process.versions)!==JSON.stringify(pin.versions)||process.env.NODE_OPTIONS)throw Error('Unqualified runtime/environment');
for(const lib of pin.libraries)if(sha(fs.readFileSync(lib.real))!==lib.sha256)throw Error('Unqualified shared library');
const harness=new Map(intake.harness.map(x=>[x.path,verify(new URL(x.path,source),x)]));
verify(new URL('LICENSE',source),intake.license);
function context(){const c=vm.createContext({print:()=>{}});c.$262={global:vm.runInContext('globalThis',c),evalScript:s=>vm.runInContext(s,c),createRealm:()=>{const r=context();return r.$262},gc:()=>{}};return c}
export async function runSuite(recheck=false){
const prior=recheck?JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle/test262-results.json',root))):null;
const selected=prior?new Set(prior.results.filter(x=>x.outcome==='failed').map(x=>x.path)):null;
const files=selected?intake.files.filter(x=>selected.has(x.path)):intake.files;
const results=[];
for(const file of files){
 const text=verify(new URL(file.path,source),file), m=file.metadata, flags=m.flags??[];
 const variants=flags.includes('module')?['module']:flags.includes('onlyStrict')?['strict']:flags.includes('noStrict')?['sloppy']:['sloppy','strict'];
 for(const variant of variants){
  const c=context();let done=false,doneError;
  c.$DONE=e=>{done=true;doneError=e};
  const negative=m.negative, isModule=variant==='module';
  let phase='parse',error=null;
  try{
   const code=(variant==='strict'?'"use strict";\n':'')+text;
   let script=isModule?new vm.SourceTextModule(code,{context:c,identifier:file.path}):new vm.Script(code,{filename:file.path});
   if(!negative||negative.phase!=='parse'){
    phase='runtime';
    for(const name of ['sta.js','assert.js',...(m.includes??[])])vm.runInContext(harness.get('harness/'+name),c,{timeout:10000});
    if(isModule){await script.link(()=>{throw Error('Unprovided module dependency')});await script.evaluate({timeout:10000})}else script.runInContext(c,{timeout:10000});
    if(flags.includes('async')){await Promise.resolve();if(!done)throw Error('Async test never completed');if(doneError)throw doneError}
   }
  }catch(e){error={name:e.name,message:e.message,phase}}
  const passed=negative?error?.name===negative.type&&error.phase===negative.phase:!error;
  results.push({path:file.path,variant,outcome:passed?'passed':'failed',expectedNegative:negative??null,error});
 }
}
const failed=results.filter(x=>x.outcome==='failed');
const report={schemaVersion:'test262-run-1',commit:intake.commit,oracleId:pin.oracleId,sourceFiles:files.length,variants:results.length,passed:results.length-failed.length,failed:failed.length,skipped:0,results};
fs.writeFileSync(new URL('test/corpora/ecmascript/oracle/'+(recheck?'test262-recheck-results.json':'test262-results.json'),root),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({files:report.sourceFiles,variants:report.variants,passed:report.passed,failed:report.failed,skipped:0,failures:failed}));
if(failed.length)process.exitCode=1;
}
if(import.meta.url===pathToFileURL(process.argv[1]).href)await runSuite();
