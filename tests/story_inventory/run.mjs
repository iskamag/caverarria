#!/usr/bin/env node
// Runs the actual portable engine with host-fed original files and no imports.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const modulePath=process.argv[2]??path.join(root,'runtime/wasm-check-target/wasm32-unknown-unknown/release/caverarria_bridge.wasm');
const dataRoot=process.env.CAVERARRIA_WASM_DATA??path.join(root,'runtime/data');
const module=new WebAssembly.Module(fs.readFileSync(modulePath));
assert.deepEqual(WebAssembly.Module.imports(module),[]);
const e=new WebAssembly.Instance(module,{}).exports;
const encode=new TextEncoder(),decode=new TextDecoder();
function bytes(data){const ptr=e.cave_alloc(data.length);new Uint8Array(e.memory.buffer,ptr,data.length).set(data);return ptr;}
function string(text){return bytes(encode.encode(text+'\0'));}
function read(ptr){const data=new Uint8Array(e.memory.buffer);let end=ptr;while(data[end])end++;return decode.decode(data.subarray(ptr,end));}
function put(name,data,save=0){
    const p=string(name),b=bytes(data);
    assert.equal(0,e.cave_fs_put(p,b,data.length,save),read(e.cave_last_error()));
    e.cave_free(p,encode.encode(name+'\0').length);e.cave_free(b,data.length);
}
function visit(dir,prefix=''){
    for(const entry of fs.readdirSync(dir,{withFileTypes:true})){
        const name=path.join(dir,entry.name),virtual=prefix+'/'+entry.name;
        if(entry.isDirectory())visit(name,virtual);
        else put(virtual,fs.readFileSync(name));
    }
}
visit(dataRoot);
const exe=fs.readFileSync(path.join(dataRoot,'../Doukutsu.exe'));
const executable=bytes(exe);assert.equal(0,e.cave_extract_original(executable,exe.length),read(e.cave_last_error()));
e.cave_free(executable,exe.length);
// Fixtures acquire genuine items with original TSC; item actions use unmodified ArmsItem.tsc.
const head=fs.readFileSync(path.join(dataRoot,'Head.tsc'));
function cipher(data,sign){let middle=Math.floor(data.length/2),key=data[middle]||7;return Buffer.from(data.map((v,i)=>i===middle?v:(v+sign*key)&255));}
put('/Head.tsc',cipher(Buffer.concat([cipher(head,-1),Buffer.from('\r\n#9000\r\n<IT+0001<IT+0015<END\r\n')]),1));
e.cave_set_time(1790800000n);
const empty=string('');const handle=e.cave_create(empty,empty,320,240);
assert.ok(handle,read(e.cave_last_error()));e.cave_free(empty,1);
function command(request){const text=JSON.stringify(request),p=string(text);const response=JSON.parse(read(e.cave_command(handle,p)));e.cave_free(p,encode.encode(text+'\0').length);assert.ok(response.ok,JSON.stringify(response));return response;}
command({op:'audio',enabled:false});
command({op:'warp',stage:0,x:160,y:120});
const tick=(controls=0)=>command({op:'tick',controls,external:true,player:{x:160,y:120,vx:0,vy:0,life:1,max_life:10}});
command({op:'event',event:9000});let state=tick();
assert.equal(state.items.some(item=>item.id===15),true);
state=command({op:'use_item',id:1});assert.equal(state.script_mode,'Inventory');
for(let i=0;i<5;i++)state=tick();
assert.equal(state.script_mode,'Inventory');
state=tick(32);assert.equal(state.script_mode,'Map');assert.equal(state.control_enabled,true);
tick();
state=command({op:'use_item',id:15});assert.equal(state.script_mode,'Inventory');
for(let i=0;i<600 && !state.script.startsWith('WaitConfirmation');i++)state=tick();
assert.equal(state.script.startsWith('WaitConfirmation'),true,state.script);
for(let i=0;i<20;i++)state=tick();
state=tick(64|2048); // accept Yes with ordinary controller confirmation
let healed=state.player.life_delta>0;
for(let i=0;i<20;i++){state=tick();healed ||= state.player.life_delta>0;}
assert.equal(healed,true,'Life Pot must call the original LI+ healing path');
assert.equal(state.items.some(item=>item.id===15),false,'Original IT- must consume the Life Pot');
state=tick(32);assert.equal(state.script_mode,'Map');assert.equal(state.control_enabled,true);
tick();
state=command({op:'use_item',id:15});assert.equal(state.script_mode,'Map','Cannot use absent item');
e.cave_destroy(handle);
console.log('Story inventory: original description, close, Life Pot heal/consume, and absent-item rejection passed.');
