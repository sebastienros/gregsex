// Fixed-input oracle replay, golden validation and negative identity checks.
import fs from 'node:fs';import assert from 'node:assert/strict';
import {observe} from './adapter.mjs';import {identity,runtime,sha} from './guard.mjs';
const root=new URL('../../../',import.meta.url);
const pin=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle-pin.json',root)));runtime(pin);
const manifest=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/artifacts.json',root)));
for(const x of manifest.files)identity(fs.readFileSync(new URL(x.path,root)),x);
const bytes=Buffer.from('fixed identity');const item={length:bytes.length,sha256:sha(bytes)};
identity(bytes,item);assert.throws(()=>identity(Buffer.from('fixed identitx'),item));assert.throws(()=>identity(bytes,{...item,length:item.length+1}));assert.throws(()=>runtime({...pin,versions:{...pin.versions,unicode:'15.0'}}));assert.throws(()=>runtime({...pin,sha256:'0'.repeat(64)}));
let checked=0;
const rawFlag=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/raw-flag-boundary.json',root)));
assert.throws(()=>new RegExp(String.fromCharCode(...rawFlag.patternUnits),String.fromCharCode(...rawFlag.rawFlagUnits)),{name:'SyntaxError'});checked++;
for(const name of ['oracle-corpus.json','imported-corpus.json']){
 const corpus=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/'+name,root)));
 assert.equal(new Set(corpus.cases.map(c=>c.id)).size,corpus.cases.length,'Duplicate fixture identity in '+name);
 for(const fixture of corpus.cases){assert.deepEqual(observe(fixture),fixture.expected,fixture.id);checked++}
}
const original=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/contract-corpus.json',root)));
const portable=[];
for(const fixture of original.cases){
 if(fixture.limits!==null){portable.push({id:fixture.id,route:'GregsEx resource integration; JS oracle has no finite-budget adapter'});continue}
 const actual=observe(fixture);
 // Runtime error strings/diagnostic span/work counters are not ECMAScript values.
 // Compare exact flag error domain/span and semantic category; retain original
 // project diagnostic details intact rather than inventing native counters.
 if(actual.error&&fixture.expected.error){assert.equal(actual.status,fixture.expected.status,fixture.id);assert.equal(actual.error.code,fixture.expected.error.code,fixture.id);assert.equal(actual.error.location,fixture.expected.error.location,fixture.id)}
 else assert.deepEqual(actual,fixture.expected,fixture.id);
 checked++;
}
console.log(JSON.stringify({oracleId:pin.oracleId,exactReplays:checked,negativeIdentityChecks:4,portableResourceRecipes:portable,gregsExEngineRuns:0}));
