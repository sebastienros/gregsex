import {runtime} from './guard.mjs';
// Fixed-input corpus generation after committed identity qualification. Independent
// golden disagreements fail, remain retained, and never rewrite their assertions.
import fs from 'node:fs';import crypto from 'node:crypto';import {observe} from './adapter.mjs';
const root=new URL('../../../',import.meta.url),sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const pin=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle-pin.json',root)));
runtime(pin);
const recipes=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/recipes.json',root))).recipes;
const failures=[],cases=[],observations=[];
if(new Set(recipes.map(r=>r.id)).size!==recipes.length)throw Error('Duplicate recipe identity');
for(const recipe of recipes){
 const expected=observe(recipe),g=recipe.golden;let disagreement=[];
 if(g){
  if(g.status&&g.status!==expected.status)disagreement.push('status');
  if(Object.hasOwn(g,'captures')){const actual=expected.matches.length?expected.matches[0].captures.map(c=>c.participated?[c.index,c.length]:null):null;if(JSON.stringify(actual)!==JSON.stringify(g.captures))disagreement.push('captures')}
  if(g.lastIndexAfter&&g.lastIndexAfter!==expected.lastIndexAfter)disagreement.push('lastIndex');
 }
 if(disagreement.length)failures.push({id:recipe.id,recipe,golden:g,observed:expected,disagreement});
 observations.push({id:recipe.id,kind:recipe.kind,independentGolden:g!==null,diagnosticSpan:expected.error?.location==='Pattern'?'whole-pattern-category-only':'exact',outcome:disagreement.length?'failed':'passed'});
 cases.push({id:recipe.id,capabilities:recipe.capabilities,provenance:{kind:'Discovery',uri:'https://tc39.es/ecma262/2025/multipage/text-processing.html',clause:recipe.clause,license:'GregsEx repository license; independently authored recipe, not quoted specification text',oracleId:pin.oracleId},patternUnits:recipe.patternUnits,inputUnits:recipe.inputUnits,flags:recipe.flags,operation:recipe.operation,startAt:0,destinationLength:null,limits:null,lastIndexBefore:recipe.lastIndexBefore,replacementUnits:recipe.replacementUnits,splitLimit:recipe.splitLimit,expected});
}
const corpus={contractVersion:'ecmascript-fixtures-1',profileId:'EcmaScript2025',dataId:'Unicode-16.0.0+Emoji-16.0',cases};
fs.writeFileSync(new URL('test/corpora/ecmascript/oracle-corpus.json',root),JSON.stringify(corpus,null,2)+'\n');
fs.writeFileSync(new URL('test/corpora/ecmascript/oracle/golden-results.json',root),JSON.stringify({oracleId:pin.oracleId,cases:cases.length,goldenAssertions:recipes.filter(x=>x.golden).length,observations,failures},null,2)+'\n');
console.log(JSON.stringify({cases:cases.length,goldenAssertions:recipes.filter(x=>x.golden).length,failures}));if(failures.length)process.exitCode=1;
