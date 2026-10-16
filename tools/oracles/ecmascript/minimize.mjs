// Exact standalone witnesses for source/oracle and excluded-object disagreements.
import fs from 'node:fs';import {runtime} from './guard.mjs';
const root=new URL('../../../',import.meta.url),pin=JSON.parse(fs.readFileSync(new URL('test/corpora/ecmascript/oracle-pin.json',root)));runtime(pin);
const cases=[];
function record(id,procedure,f){try{cases.push({id,procedure,observed:f()})}catch(e){cases.push({id,procedure,observed:{error:e.name,constructor:e.constructor.name,message:e.message}})}}
record('C018','new RegExp(".","uy") on [55357,56832], lastIndex=1',()=>{let r=new RegExp('.','uy');r.lastIndex=1;const m=r.exec(String.fromCharCode(55357,56832));return {index:m.index,length:m[0].length,units:[...Array(m[0].length)].map((_,i)=>m[0].charCodeAt(i)),lastIndexAfter:r.lastIndex}});
record('static-setter','descriptor RegExp.$1 setter absent in pending legacy-regexp tests',()=>({setter:typeof Object.getOwnPropertyDescriptor(RegExp,'$1').set}));
record('static-receiver','RegExp.$1 getter with plain object receiver',()=>Object.getOwnPropertyDescriptor(RegExp,'$1').get.call({}));
record('compile-subclass','RegExp.prototype.compile on subclass instance',()=>{class R extends RegExp{};let r=new R('a');r.compile('b');return r.source});
record('replace-flags-getter','Symbol.replace receiver has throwing flags and global accessors',()=>{class FlagsError extends Error{};class GlobalError extends Error{};return RegExp.prototype[Symbol.replace].call({get flags(){throw new FlagsError()},get global(){throw new GlobalError()}},'a','b')});
record('primitive-symbol-getter','String.match number primitive with Symbol.match getter on Number.prototype',()=>{class DispatchError extends Error{};Object.defineProperty(Number.prototype,Symbol.match,{configurable:true,get(){throw new DispatchError()}});try{return 'a1b'.match(1)}finally{delete Number.prototype[Symbol.match]}});
fs.writeFileSync(new URL('test/corpora/ecmascript/oracle/minimized-observations.json',root),JSON.stringify({oracleId:pin.oracleId,cases},null,2)+'\n');console.log(JSON.stringify(cases));
