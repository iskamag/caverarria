#!/usr/bin/env node
// Runs the actual portable engine with host-fed original files and no imports.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
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
// A private TSC fixture selects original ACCESS and plays a genuine PixTone SFX.
const head=fs.readFileSync(path.join(dataRoot,'Head.tsc'));
function cipher(data,sign){let middle=Math.floor(data.length/2),key=data[middle]||7;return Buffer.from(data.map((v,i)=>i===middle?v:(v+sign*key)&255));}
put('/Head.tsc',cipher(Buffer.concat([cipher(head,-1),Buffer.from(
    '\r\n#9000\r\n<CMU0021<SOU0012<END\r\n#9010\r\n<CMU0000<CMU0021<END\r\n#9011\r\n<CMU0000<SOU0015<END\r\n#9012\r\n<SOU0015<END\r\n#9013\r\n<CMU0008<CMU0021<RMU<END\r\n#9014\r\n<RMU<END\r\n#9015\r\n<MSGHigh zoom dialogue remains readable.<NOD<END\r\n#9016\r\n<FAI0000<AM+0002:0000<END\r\n#9020\r\n<KEY<MSGSkip this dialogue through the ordinary replay controller.<NOD<FL+7999<END\r\n#9021\r\n<KEY<ML+0003<END\r\n#9022\r\n<END\r\n')]),1));
e.cave_set_time(1790800000n);
const empty=string('');let handle=e.cave_create(empty,empty,320,240);
assert.ok(handle,read(e.cave_last_error()));e.cave_free(empty,1);
function command(request){const text=JSON.stringify(request),p=string(text);const response=JSON.parse(read(e.cave_command(handle,p)));e.cave_free(p,encode.encode(text+'\0').length);assert.ok(response.ok,JSON.stringify(response));return response;}
let initial=command({op:'snapshot'});
assert.equal(95,initial.stages.length);assert.equal(60,initial.timing_hz);assert.equal(3,initial.render_layers);
if(process.env.CAVERARRIA_INPUT_TEST){
    const egg=initial.stages.findIndex(s=>s.map==='EggX');assert.ok(egg>=0);
    const script=cipher(fs.readFileSync(path.join(dataRoot,'Stage/EggX.tsc')),-1).toString('latin1');
    assert.match(script,/#0300\s+<KEY<MSG<TURSky Dragon Egg No\. 00[\s\S]*?Input Password:<NOD<END/);
    command({op:'warp',stage:egg,x:64,y:64});command({op:'event',event:300});
    let tick;let waits=0;
    for(let i=0;i<180;i++){
        tick=command({op:'tick',controls:i%2?64|2048:0});
        if(tick.script.startsWith('WaitInput'))waits++;
        if(tick.script==='Ended')break;
    }
    assert.equal('Ended',tick.script);assert.equal(true,tick.control_enabled);assert.equal(egg,tick.stage.id);
    assert.ok(waits>0,'original password prompt never waited for acknowledgment');
    command({op:'warp',stage:0,x:64,y:64});command({op:'event',event:9020});
    for(let i=0;i<90;i++)tick=command({op:'tick',controls:0});
    assert.equal(false,tick.flags.includes(7999),'unacknowledged dialogue advanced');
    let skipped=0;
    for(;skipped<100;skipped++){
        tick=command({op:'tick',controls:4096});
        if(tick.script==='Ended')break;
    }
    assert.equal('Ended',tick.script);assert.equal(true,tick.flags.includes(7999));assert.ok(skipped>=49);
    const nativeMax=tick.player.max_life;
    tick=command({op:'tick',controls:0,player:{max_life:65535,life:nativeMax}});
    assert.equal(nativeMax,tick.player.max_life,'host effective HP contaminated native capsule maximum');
    command({op:'event',event:9021});tick=command({op:'tick',controls:0});
    assert.equal(nativeMax+3,tick.player.max_life,'native Life Capsule opcode stopped working');
    e.cave_destroy(handle);
    console.log(JSON.stringify({module:modulePath,egg_password_is_acknowledgment:true,original_event_ends:true,held_skip_ticks:skipped+1,host_maximum_ignored:true,native_capsule_grants:true}));
    process.exit(0);
}
if(process.env.CAVERARRIA_MICRO_TERRAIN_TEST){
    const room=initial.stage.id,width=initial.stage.width;
    command({op:'event',event:9022});
    command({op:'tick',controls:0});
    const x=Math.min(8,width-3),y=Math.min(8,initial.stage.height-3),idx=y*width+x;
    let state=initial;
    const edit=(cx,cy,solid,extra={})=>{
        const result=command({op:'terrain_edit',epoch:state.epoch,stage:room,x:cx,y:cy,solid,...extra});
        assert.equal(true,result.terrain_edit_accepted);state=result;return result;
    };
    // Mine a genuine native neighborhood, leaving no authored collisions near probes.
    for(let cy=y-1;cy<=y+1;cy++)for(let cx=x-1;cx<=x+1;cx++)edit(cx,cy,false);
    state=edit(x,y,true,{sub_x:0,sub_y:0});
    assert.equal(0,state.map.cell_attributes[idx],'single subtile became a whole native wall');
    assert.equal(1,state.map.terrain_edits.find(t=>t.x===x&&t.y===y).mask);
    const left=x*16-8,top=y*16-8;
    const occupied=command({op:'tile_hit',x:left+2,y:top+2,width:1,height:1});
    const empty=command({op:'tile_hit',x:left+12,y:top+2,width:1,height:1});
    assert.ok(occupied.tile_hit_flags&512,'native bullet missed occupied subtile');
    assert.equal(0,empty.tile_hit_flags&512,'native bullet hit unoccupied part of cell');
    const floor=command({op:'tick',controls:0,player:{x:left+2,y:top-0.5,vx:0,vy:1,width:2,height:2}});
    assert.ok(Math.abs(floor.player.y-(top-1))<1/512,'native player did not land on actual subtile top: '+JSON.stringify({player:floor.player,top}));
    assert.ok(floor.player.flags&8,'native player did not report floor contact');
    const clear=command({op:'tick',controls:0,player:{x:left+12,y:top-0.5,vx:0,vy:1,width:2,height:2}});
    assert.ok(Math.abs(clear.player.y-(top-0.5))<1/512,'empty neighboring subtile displaced player');
    command({op:'save'});e.cave_destroy(handle);
    const name=string('');handle=e.cave_create(name,name,320,240);e.cave_free(name,1);
    assert.ok(handle,read(e.cave_last_error()));state=command({op:'snapshot'});
    assert.equal(1,state.map.terrain_edits.find(t=>t.x===x&&t.y===y).mask);
    edit(x,y,true,{sub_x:1,sub_y:1});
    state=edit(x,y,false,{sub_x:0,sub_y:0});
    assert.equal(8,state.map.terrain_edits.find(t=>t.x===x&&t.y===y).mask,'subtile removal erased its neighbor');
    assert.ok(command({op:'tile_hit',x:left+12,y:top+12,width:1,height:1}).tile_hit_flags&512,'bottom-right 2x2 subtile missed');
    assert.equal(0,command({op:'tile_hit',x:left+2,y:top+2,width:1,height:1}).tile_hit_flags&512,'removed subtile remained solid');
    assert.throws(()=>edit(x,y,true,{sub_x:2,sub_y:0}),/Terrain subcell/);
    const terrainFile=string('/Terrain.json');
    const terrainDocument=JSON.parse(decode.decode(new Uint8Array(e.memory.buffer,e.cave_fs_get(terrainFile,1),e.cave_fs_len(terrainFile,1))));
    assert.equal(2,terrainDocument.version);assert.equal(2,terrainDocument.subdivisions);assert.equal(8,terrainDocument.stages[room][idx]);
    e.cave_free(terrainFile,encode.encode('/Terrain.json\0').length);
    // Old 3x3 layouts must never be mistaken for new 2x2 masks.
    command({op:'save'});e.cave_destroy(handle);
    const savedName=string('/Profile.dat');
    const profileBefore=Buffer.from(new Uint8Array(e.memory.buffer,e.cave_fs_get(savedName,1),e.cave_fs_len(savedName,1)));
    for(const incompatible of [{[room]:{[idx]:true,[idx+1]:511}}, {version:1,subdivisions:3,stages:{[room]:{[idx]:15}}}]) {
        const legacyBytes=Buffer.from(JSON.stringify(incompatible));
        put('/Terrain.json',legacyBytes,1);
        const legacy=string('');handle=e.cave_create(legacy,legacy,320,240);e.cave_free(legacy,1);
        state=command({op:'snapshot'});assert.equal(2,state.terrain_layout_version);
        assert.deepEqual([],state.map.terrain_edits,'incompatible 3x3 edits were interpreted as 2x2');
        const profileAfter=Buffer.from(new Uint8Array(e.memory.buffer,e.cave_fs_get(savedName,1),e.cave_fs_len(savedName,1)));
        assert.deepEqual(profileBefore,profileAfter,'layout reset changed campaign checkpoint');
        const terrainName=string('/Terrain.json');
        const untouched=Buffer.from(new Uint8Array(e.memory.buffer,e.cave_fs_get(terrainName,1),e.cave_fs_len(terrainName,1)));
        assert.deepEqual(legacyBytes,untouched,'ignored legacy terrain was overwritten');
        e.cave_free(terrainName,encode.encode('/Terrain.json\0').length);
        e.cave_destroy(handle);
    }
    e.cave_free(savedName,encode.encode('/Profile.dat\0').length);
    console.log(JSON.stringify({module:modulePath,micro_cell:{room,x,y},partial_collision:true,native_bullet_precise:true,native_player_precise:true,persistence:true,independent_removal:true,incompatible_layout_ignored:true,checkpoint_unchanged:true}));
    process.exit(0);
}
if(process.env.CAVERARRIA_TERRAIN_TEST){
    const room=initial.stage.id,width=initial.stage.width;
    const idx=initial.map.cell_attributes.findIndex(a=>a===0x41);
    assert.ok(idx>=0,'fixture room has no genuine solid cell');
    const x=idx%width,y=Math.floor(idx/width);
    const edit=(snapshot,solid,extra={})=>command({op:'terrain_edit',epoch:snapshot.epoch,stage:room,x,y,solid,...extra});
    let mined=edit(initial,false);
    assert.equal(true,mined.terrain_edit_accepted);assert.equal(0,mined.map.cell_attributes[idx]);
    let placed=edit(mined,true);
    assert.equal(true,placed.terrain_edit_accepted);assert.equal(0x41,placed.map.cell_attributes[idx]);
    assert.equal(false,edit(placed,false,{epoch:placed.epoch+1}).terrain_edit_accepted);
    assert.equal(false,edit(placed,false,{stage:room+1}).terrain_edit_accepted);
    assert.equal(false,edit(placed,false,{x:width}).terrain_edit_accepted);
    command({op:'warp',stage:(room+1)%95});
    let returned=command({op:'warp',stage:room});
    assert.equal(0x41,returned.map.cell_attributes[idx]);
    mined=edit(returned,false);command({op:'save'});
    let retried=command({op:'retry'});
    assert.equal(room,retried.stage.id);assert.equal(0,retried.map.cell_attributes[idx]);
    const list=JSON.parse(read(e.cave_fs_list(1)));
    assert.ok(list.includes('/terrain.json'));
    e.cave_destroy(handle);
    const empty=string('');handle=e.cave_create(empty,empty,320,240);e.cave_free(empty,1);
    assert.ok(handle,read(e.cave_last_error()));
    let recreated=command({op:'snapshot'});
    assert.equal(room,recreated.stage.id);assert.equal(0,recreated.map.cell_attributes[idx]);
    assert.equal(false,command({op:'terrain_persistence',enabled:false}).terrain_persistent);
    const beforePolicy=string('/Terrain.json');
    const persisted=Buffer.from(new Uint8Array(e.memory.buffer,e.cave_fs_get(beforePolicy,1),e.cave_fs_len(beforePolicy,1)));
    placed=edit(recreated,true);assert.equal(0x41,placed.map.cell_attributes[idx]);
    const volatileBytes=Buffer.from(new Uint8Array(e.memory.buffer,e.cave_fs_get(beforePolicy,1),e.cave_fs_len(beforePolicy,1)));
    assert.deepEqual(persisted,volatileBytes,'disabled edit modified persistent terrain');
    command({op:'warp',stage:(room+1)%95});returned=command({op:'warp',stage:room});
    assert.equal(0x41,returned.map.cell_attributes[idx]);
    retried=command({op:'retry'});assert.equal(0,retried.map.cell_attributes[idx]);
    placed=edit(retried,true);
    assert.equal(true,command({op:'terrain_persistence',enabled:true}).terrain_persistent);
    e.cave_destroy(handle);
    const again=string('');handle=e.cave_create(again,again,320,240);e.cave_free(again,1);
    assert.ok(handle,read(e.cave_last_error()));
    assert.equal(0x41,command({op:'snapshot'}).map.cell_attributes[idx],'enabling persistence failed to save current edits');
    e.cave_free(beforePolicy,encode.encode('/Terrain.json\0').length);
    let fresh=command({op:'new'});
    assert.equal(0,fresh.map.terrain_edits.length);
    e.cave_destroy(handle);
    console.log(JSON.stringify({module:modulePath,terrain_cell:{room,x,y},mining:true,placement:true,reject_stale:true,reject_bounds:true,room_transfer:true,retry:true,recreate:true,new_clears:true,volatile_room_transfer:true,volatile_retry_restores:true,reenable_saves:true}));
    process.exit(0);
}
if(process.env.CAVERARRIA_SILENT_AUDIO_TEST){
    assert.equal(true,initial.audio_ready);
    command({op:'warp',stage:0,x:160,y:120});
    const tick=()=>command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
    const event=id=>{command({op:'event',event:id});return tick();};
    const pcm=(frames=4096)=>new Int16Array(e.memory.buffer,e.cave_audio(handle,frames),frames*2);
    // Queue music/effects before disabling, without ever pumping the mixer.
    assert.equal(21,event(9000).song);
    let muted=command({op:'audio',enabled:false,music_volume:0,sfx_volume:1});
    assert.equal(false,muted.audio_ready);assert.equal(21,muted.song);
    const effects=muted.audio_trace.sfx_events;
    for(let i=0;i<200;i++)muted=event(9012);
    assert.equal(effects+200,muted.audio_trace.sfx_events);
    assert.ok(pcm().every(x=>x===0));
    // CMU/RMU must retain logical song IDs while playback is disabled.
    assert.equal(8,event(9013).song);
    assert.equal(21,event(9000).song);
    command({op:'save'});
    assert.equal(21,command({op:'load'}).song);
    assert.equal(false,command({op:'snapshot'}).audio_ready);
    assert.equal(true,command({op:'audio',enabled:true,music_volume:0,sfx_volume:1}).audio_ready);
    for(let i=0;i<4;i++)assert.ok(pcm().every(x=>x===0),'discarded sounds replayed after enabling');
    event(9012);
    let peak=0;for(let i=0;i<4;i++)for(const sample of pcm())peak=Math.max(peak,Math.abs(sample));
    assert.ok(peak>0,'fresh effects did not resume');
    // Active/partially consumed sound buffers must also be removed by disable.
    event(9012);pcm(64);
    command({op:'audio',enabled:false});
    command({op:'audio',enabled:true,music_volume:0,sfx_volume:1});
    for(let i=0;i<4;i++)assert.ok(pcm().every(x=>x===0));
    command({op:'audio',music_volume:1,sfx_volume:0});
    assert.equal(8,event(9014).song);
    let energy=0;for(let i=0;i<4;i++)for(const sample of pcm())energy+=sample*sample;
    assert.ok(energy>0,'current music did not resume');
    console.log(JSON.stringify({module:modulePath,muted_effect_requests:200,queued_and_active_audio_cleared:true,
        silent_song_and_profile_preserved:true,reenable_has_no_stale_effects:true,fresh_pixtone_peak:peak}));
    e.cave_destroy(handle);process.exit(0);
}
if(process.env.CAVERARRIA_LOAD_AUDIO_TEST){
    command({op:'warp',stage:0,x:160,y:120});
    command({op:'audio',music_volume:1,sfx_volume:0});
    command({op:'event',event:9000});
    command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
    assert.equal(21,command({op:'save'}).song);
    e.cave_destroy(handle);
    const name=string('');handle=e.cave_create(name,name,320,240);e.cave_free(name,1);
    assert.ok(handle,read(e.cave_last_error()));
    // Match the host: create loads the profile, then Start explicitly loads it.
    assert.equal(21,command({op:'load'}).song);
    command({op:'audio',music_volume:1,sfx_volume:0});
    let energy=0;for(let i=0;i<12;i++){
        const pointer=e.cave_audio(handle,800);
        for(const sample of new Int16Array(e.memory.buffer,pointer,1600))energy+=sample*sample;
    }
    assert.ok(energy>0,'saved music did not start before the first game tick');
    console.log(JSON.stringify({module:modulePath,saved_song:21,load_pcm_rms:Math.sqrt(energy/19200)}));
    e.cave_destroy(handle);process.exit(0);
}
if(process.env.CAVERARRIA_UI_VIEWPORT_TEST){
    command({op:'audio',enabled:false});
    command({op:'warp',stage:0,x:160,y:120});
    const small=command({op:'resize',width:160,height:120});
    assert.deepEqual(small.viewport,{width:160,height:120});
    assert.deepEqual(small.ui_viewport,{width:320,height:240});
    command({op:'event',event:9016});
    let equipped;
    for(let i=0;i<60;i++)equipped=command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
    assert.ok(equipped.weapons.some(weapon=>weapon.id===2),'fixture failed to acquire native weapon');
    let xpAlpha=0;
    const xpPointer=e.cave_pixels(handle,2),xpRgba=new Uint8Array(e.memory.buffer,xpPointer,320*240*4);
    for(let y=32;y<40;y++)for(let x=0;x<80;x++)xpAlpha+=xpRgba[(y*320+x)*4+3];
    assert.ok(xpAlpha>0,'host-controlled avatar lost original native weapon LV/XP bar');
    const progressAlpha=()=>{
        const pixels=new Uint8Array(e.memory.buffer,e.cave_pixels(handle,2),320*240*4);
        let sum=0;
        for(let y=32;y<40;y++)for(let x=0;x<80;x++)sum+=pixels[(y*320+x)*4+3];
        return sum;
    };
    command({op:'tick',controls:0,host_inventory_open:true,player:{x:160,y:120,vx:0,vy:0}});
    assert.equal(0,progressAlpha(),'weapon progress overlaps the open Terraria inventory');
    command({op:'tick',controls:0,host_inventory_open:false,player:{x:160,y:120,vx:0,vy:0}});
    assert.ok(progressAlpha()>0,'weapon progress did not return after closing inventory');
    command({op:'event',event:9015});
    let displayed;
    for(let i=0;i<180;i++)displayed=command({op:'tick',controls:0,host_inventory_open:true,player:{x:160,y:120,vx:0,vy:0}});
    assert.deepEqual(displayed.viewport,{width:160,height:120},'UI draw leaked its dimensions into world simulation');
    assert.deepEqual(displayed.ui_viewport,{width:320,height:240});
    const pointer=e.cave_pixels(handle,2),rgba=new Uint8Array(e.memory.buffer,pointer,320*240*4);
    let rightHalfAlpha=0;
    for(let y=120;y<240;y++)for(let x=160;x<320;x++)rightHalfAlpha+=rgba[(y*320+x)*4+3];
    assert.ok(rightHalfAlpha>0,'dialogue right half was clipped or hidden by the open inventory');
    assert.equal(0,progressAlpha(),'dialogue should remain visible without the overlapping weapon progress');
    const large=command({op:'resize',width:426,height:300});
    assert.deepEqual(large.viewport,{width:426,height:300});
    assert.deepEqual(large.ui_viewport,{width:426,height:300});
    console.log(JSON.stringify({module:modulePath,world_viewport:displayed.viewport,ui_viewport:displayed.ui_viewport,
        dialogue_right_half_alpha:rightHalfAlpha,weapon_xp_bar_alpha:xpAlpha,inventory_hides_progress_only:true,world_canvas_restored:true}));
    e.cave_destroy(handle);process.exit(0);
}
for(let i=0;i<180;i++)command({op:'tick',controls:0,player:{x:160,y:128,vx:0,vy:0}});
const rendered=command({op:'snapshot'});
let alpha=0;
for(let layer=1;layer<=2;layer++){
    const p=e.cave_pixels(handle,layer),data=new Uint8Array(e.memory.buffer,p,320*240*4);
    for(let i=3;i<data.length;i+=4)alpha+=data[i];
}
assert.ok(alpha>0);
command({op:'warp',stage:0,x:160,y:120});command({op:'event',event:9000});
command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
assert.equal(48000,e.cave_audio_rate());let energy=0;
const audioStarted=performance.now();
for(let i=0;i<60;i++){
    const ptr=e.cave_audio(handle,800);assert.equal(3200,e.cave_audio_length(handle));
    const pcm=new Int16Array(e.memory.buffer,ptr,1600);
    for(const sample of pcm)energy+=sample*sample;
}
assert.ok(energy>0);
const audioMs=performance.now()-audioStarted;
const before=command({op:'snapshot'}),after=command({op:'resize',width:426,height:240});
assert.equal(before.tick,after.tick);assert.equal(426,after.viewport.width);
const revision=e.cave_fs_revision?.(1)??0n;
command({op:'save'});
if(e.cave_fs_revision)assert.ok(e.cave_fs_revision(1)>revision);
const list=JSON.parse(read(e.cave_fs_list(1)));
assert.ok(list.some(name=>name.endsWith('/profile.dat')));
const profile=string('/Profile.dat'),size=e.cave_fs_len(profile,1);
assert.ok(size>0);assert.ok(e.cave_fs_get(profile,1));
e.cave_free(profile,encode.encode('/Profile.dat\0').length);
const t=performance.now();
for(let i=0;i<120;i++)command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
const ms=(performance.now()-t)/120;
console.log(JSON.stringify({module:modulePath,bytes:fs.statSync(modulePath).size,imports:0,stages:initial.stages.length,timing_hz:60,render_alpha_sum:alpha,pcm_rms:Math.sqrt(energy/96000),pcm_ms_per_second:audioMs,save_bytes:size,save_files:list,tick_ms:ms},null,2));
if(process.env.CAVERARRIA_AUDIO_CORPUS){
    command({op:'warp',stage:0,x:160,y:120});
    command({op:'audio',music_volume:1,sfx_volume:0});
    command({op:'event',event:9010});command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
    const song=fs.readFileSync(path.join(root,'runtime/data/Org/ACCESS.org'));
    const wait=song.readUInt16LE(6),start=song.readInt32LE(10),end=song.readInt32LE(14);
    const loopFrames=(end-start)*48*wait,totalFrames=loopFrames*3;
    const pcm=Buffer.alloc(totalFrames*4);
    for(let at=0;at<totalFrames;){
        const frames=Math.min(4096,totalFrames-at),pointer=e.cave_audio(handle,frames);
        assert.equal(frames*4,e.cave_audio_length(handle));
        pcm.set(new Uint8Array(e.memory.buffer,pointer,frames*4),at*4);at+=frames;
    }
    function rms(from,to){let sum=0;for(let frame=from;frame<to;frame++){
        const l=pcm.readInt16LE(frame*4),r=pcm.readInt16LE(frame*4+2);sum+=l*l+r*r;
    }return Math.sqrt(sum/(to-from)/2);}
    const levels=[0,1,2].map(i=>rms(i*loopFrames,(i+1)*loopFrames));
    const envelopes=[0,1,2].map(i=>Array.from({length:Math.floor(loopFrames/2400)},(_,j)=>
        rms(i*loopFrames+j*2400,i*loopFrames+(j+1)*2400)));
    function correlation(a,b){
        const ma=a.reduce((s,v)=>s+v,0)/a.length,mb=b.reduce((s,v)=>s+v,0)/b.length;
        let ab=0,aa=0,bb=0;for(let i=0;i<a.length;i++){const x=a[i]-ma,y=b[i]-mb;ab+=x*y;aa+=x*x;bb+=y*y;}
        return ab/Math.sqrt(aa*bb);
    }
    const correlations=[correlation(envelopes[0],envelopes[1]),correlation(envelopes[1],envelopes[2])];
    assert.ok(Math.min(...correlations)>.95);assert.ok(Math.min(...levels)/Math.max(...levels)>.8);
    command({op:'audio',music_volume:0,sfx_volume:0});
    let pointer=e.cave_audio(handle,4096);
    assert.ok(new Uint8Array(e.memory.buffer,pointer,4096*4).every(x=>x===0));
    command({op:'audio',music_volume:0,sfx_volume:1});
    command({op:'event',event:9011});command({op:'tick',controls:0,player:{x:160,y:120,vx:0,vy:0}});
    let peak=0;for(let i=0;i<4;i++){
        pointer=e.cave_audio(handle,4096);for(const sample of new Int16Array(e.memory.buffer,pointer,8192))peak=Math.max(peak,Math.abs(sample));
    }
    assert.ok(peak>0);
    const destination=process.env.CAVERARRIA_AUDIO_CORPUS;fs.mkdirSync(path.dirname(destination),{recursive:true});fs.writeFileSync(destination,pcm);
    const report={module:modulePath,wasm_sha256:crypto.createHash('sha256').update(fs.readFileSync(modulePath)).digest('hex'),
        sample_rate:48000,channels:2,format:'s16le',song:'ACCESS',original_sha256:crypto.createHash('sha256').update(song).digest('hex'),
        loop_seconds:loopFrames/48000,complete_pcm_bytes:pcm.length,loop_rms:levels,loop_envelope_correlations:correlations,
        full_composition_repeats:true,music_and_sfx_muted_are_silent:true,pixtone_sfx_id:15,pixtone_peak:peak};
    fs.writeFileSync(destination.replace(/\.[^.]+$/,'')+'.json',JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report,null,2));
}
e.cave_destroy(handle);
