import {runtime} from './guard.mjs';
// Adapt the original admitted Test262 algorithm assertions into frozen fixture v1.
// Full-suite assertions run separately without instrumentation. This pass retains
// every unique builtin exec call from the stated semantic selection, without caps.
import fs from 'node:fs';import vm from 'node:vm';import crypto from 'node:crypto';
import {observe,toUnits,numberToken} from './adapter.mjs';
const root=new URL('../../../',import.meta.url),base=new URL('test262/',import.meta.url);
const pin=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle-pin.json',root)));
const intake=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/test262-intake.json',root)));
const suite=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle/test262-results.json',root)));
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
runtime(pin);
const failures=new Set(suite.results.filter(x=>x.outcome!=='passed').map(x=>x.path));
const selected=intake.files.filter(x=>/^test\/built-ins\/RegExp\/S15\.10\.2\./.test(x.path)||/^test\/built-ins\/RegExp\/(named-groups|lookBehind|lookbehind|dotall|unicodeSets)\//.test(x.path));
const cases=[],files=[],errors=[];
function capabilities(path){
 if(path.includes('named-groups'))return ['ES-005','ES-006'];
 if(path.toLowerCase().includes('lookbehind'))return ['ES-007'];
 if(path.includes('dotall'))return ['ES-003'];
 if(path.includes('unicodeSets'))return ['ES-011'];
 const section=Number(path.match(/S15\.10\.2\.(\d+)/)?.[1]);
 return ({5:['ES-004'],6:['ES-003','ES-007','ES-009'],7:['ES-004'],
  8:['ES-002','ES-003','ES-005'],9:['ES-006','ES-008','ES-009'],
  10:['ES-008'],11:['ES-006','ES-014'],12:['ES-009'],
  13:['ES-009'],14:['ES-009'],15:['ES-009'],16:['ES-009'],
  17:['ES-008','ES-009'],18:['ES-008','ES-009'],19:['ES-008','ES-009']})[section]??['ES-002'];
}
for(const file of selected){
 if(failures.has(file.path))throw Error('Cannot import failed original assertions '+file.path);
 const b=fs.readFileSync(new URL(file.path,base));if(b.length!==file.length||sha(b)!==file.sha256)throw Error('Import identity mismatch');
 const metadata=file.metadata, flags=metadata.flags??[];if(metadata.negative){files.push({path:file.path,route:'original-negative-parse-suite',fixtures:0});continue}
 const c=vm.createContext({});const traces=[];
 c.record=(p,f,s,last)=>{if(typeof s!=='string'||typeof last!=='number')return;traces.push({patternUnits:toUnits(p),inputUnits:toUnits(s),flags:f,lastIndexBefore:numberToken(last)})};
 try{
  for(const name of ['sta.js','assert.js',...(metadata.includes??[])])vm.runInContext(fs.readFileSync(new URL('harness/'+name,base),'utf8'),c,{timeout:10000});
  vm.runInContext(`{
    const original=RegExp.prototype.exec;
    const source=Object.getOwnPropertyDescriptor(RegExp.prototype,'source').get;
    const flags=Object.getOwnPropertyDescriptor(RegExp.prototype,'flags').get;
    RegExp.prototype.exec=function(s){
      const p=Reflect.apply(source,this,[]), f=Reflect.apply(flags,this,[]),last=this.lastIndex;
      const result=Reflect.apply(original,this,[s]);
      record(p,f,s,last);return result;
    };
  }`,c);
  vm.runInContext((flags.includes('onlyStrict')?'"use strict";\n':'')+b.toString(),c,{timeout:10000});
 }catch(e){errors.push({path:file.path,error:String(e)});continue}
 let count=0;const seen=new Set();
 for(const trace of traces){const key=JSON.stringify(trace);if(seen.has(key))continue;seen.add(key);
  const id='ES-T262-'+String(cases.length+1).padStart(5,'0');
  const recipe={id,capabilities:capabilities(file.path),operation:'Exec',...trace,replacementUnits:null,splitLimit:null};
  const expected=observe(recipe);
  cases.push({...recipe,startAt:0,destinationLength:null,limits:null,
   provenance:{kind:'Imported',uri:`https://github.com/tc39/test262/blob/${intake.commit}/${file.path}`,clause:'ECMAScript2025 22.2.2; original '+(metadata.esid??metadata.es5id??'assertions at pinned file'),license:'BSD-3-Clause; original copyright headers and LICENSE retained in tools/oracles/ecmascript/test262',oracleId:pin.oracleId},expected});count++;
 }
 files.push({path:file.path,route:'builtin-exec-observation-with-original-assertions',fixtures:count});
}
fs.writeFileSync(new URL('test/corpora/ecmascript/imported-corpus.json',root),JSON.stringify({contractVersion:'ecmascript-fixtures-1',profileId:'EcmaScript2025',dataId:'Unicode-16.0.0+Emoji-16.0',cases},null,2)+'\n');
fs.writeFileSync(new URL('test/corpora/ecmascript/oracle/import-results.json',root),JSON.stringify({selection:'All S15.10.2 algorithm files and named-groups/lookBehind/lookbehind/dotall/unicodeSets trees; full suite covers all other intake. Parse negatives remain original syntax suite. No per-file/call cap; duplicates within a file are deduplicated by exact units/flags/state.',oracleId:pin.oracleId,files,fixtures:cases.length,errors},null,2)+'\n');
console.log(JSON.stringify({selected:files.length,fixtures:cases.length,errors}));if(errors.length)process.exitCode=1;
