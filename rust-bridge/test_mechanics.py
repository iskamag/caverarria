#!/usr/bin/env python3
"""Adapter diagnostics with private TSC fixtures; these are not a campaign playthrough."""
import ctypes
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def decrypt(raw):
    middle = len(raw) // 2
    key = raw[middle] or 7
    return bytes(x if i == middle else (x-key) & 255 for i,x in enumerate(raw))


def encrypt(plain):
    middle = len(plain) // 2
    key = plain[middle] or 7
    return bytes(x if i == middle else (x+key) & 255 for i,x in enumerate(plain))


class Mechanics(unittest.TestCase):
    def setUp(self):
        os.environ['CAVERARRIA_AUDIO'] = '0'
        self.temp = tempfile.TemporaryDirectory(prefix='caverarria-mechanics-')
        self.base = Path(self.temp.name)
        data = self.base / 'data'
        shutil.copytree(ROOT / 'runtime/data', data)
        # Equipment and spawned platforms go through the real TSC interpreter.
        plain = decrypt((data/'Head.tsc').read_bytes())
        plain += b'\r\n#9000\r\n<EQ+0001<EQ-0032<END\r\n#9001\r\n<EQ+0032<EQ-0001<END\r\n#9002\r\n<SNP0231:0010:0010:0000<END\r\n#9003\r\n<CMU0008<SOU0012<END\r\n#9005\r\n<FAI0000<PRI<MSGLayer test.<NOD<END\r\n#9006\r\n<FAI0000<END\r\n#9007\r\n<AM+0013:0000<AM+0002:0000<END\r\n#9008\r\n<UNI0001<END\r\n'
        (data/'Head.tsc').write_bytes(encrypt(plain))
        attributes = bytearray((data/'Stage/0.pxa').read_bytes())
        attributes[200] = 0x81  # isolated original up-current collision fixture
        (data/'Stage/0.pxa').write_bytes(attributes)
        plain = decrypt((data/'Head.tsc').read_bytes())
        plain += b'\r\n#9009\r\n<CMP0010:0008:0200<END\r\n'
        (data/'Head.tsc').write_bytes(encrypt(plain))
        library = Path(os.environ.get('CAVERARRIA_TEST_LIBRARY',
                       ROOT/'rust-bridge/target/release/libcaverarria_bridge.so'))
        self.lib = ctypes.CDLL(str(library))
        self.lib.cave_create.argtypes = [ctypes.c_char_p,ctypes.c_char_p,ctypes.c_int,ctypes.c_int]
        self.lib.cave_create.restype = ctypes.c_void_p
        self.lib.cave_command.argtypes = [ctypes.c_void_p,ctypes.c_char_p]
        self.lib.cave_command.restype = ctypes.c_char_p
        self.lib.cave_pixels.argtypes = [ctypes.c_void_p,ctypes.c_int]
        self.lib.cave_pixels.restype = ctypes.POINTER(ctypes.c_ubyte)
        self.lib.cave_destroy.argtypes = [ctypes.c_void_p]
        self.lib.cave_last_error.restype = ctypes.c_char_p
        self.handle = self.lib.cave_create(str(data).encode(),str(self.base/'save').encode(),320,240)
        if not self.handle: raise RuntimeError(self.lib.cave_last_error().decode())
        self.command(op='warp',stage=0,x=160,y=120)

    def tearDown(self):
        self.lib.cave_destroy(self.handle)
        self.temp.cleanup()

    def command(self,**request):
        response = json.loads(self.lib.cave_command(self.handle,json.dumps(request).encode()))
        self.assertTrue(response.get('ok'),response)
        return response

    def tick(self,controls=0,**player):
        defaults = dict(x=160,y=120,vx=0,vy=0,width=6.667,height=14,life=3,max_life=3,grounded=False)
        defaults.update(player)
        return self.command(op='tick',controls=controls,player=defaults)

    def equip(self,event):
        self.command(op='event',event=event)
        state = self.tick(grounded=True)
        return state

    def test_resize_preserves_campaign_state(self):
        before = self.command(op='snapshot')
        self.assertEqual(60,before['timing_hz'])
        after = self.command(op='resize',width=630,height=343)
        self.assertEqual(dict(width=630,height=343),after['viewport'])
        for key in ('epoch','stage','tick','script','flags','items','weapons'):
            self.assertEqual(before[key],after[key],key)
        for key in ('x','y','vx','vy','life','max_life','equipment'):
            self.assertEqual(before['player'][key],after['player'][key],key)
        pixels = self.lib.cave_pixels(self.handle,0)
        self.assertEqual(255,pixels[630*343*4-1])
        after = self.command(op='resize',width=320,height=240)
        self.assertEqual(dict(width=320,height=240),after['viewport'])

    def test_create_resumes_existing_original_profile(self):
        self.command(op='warp',stage=10,x=180,y=100)
        before = self.command(op='save')
        self.lib.cave_destroy(self.handle)
        self.handle = self.lib.cave_create(str(self.base/'data').encode(),str(self.base/'save').encode(),320,240)
        self.assertTrue(self.handle)
        after = self.command(op='snapshot')
        self.assertEqual(before['stage']['id'],after['stage']['id'])
        self.assertEqual(before['player']['x'],after['player']['x'])
        self.assertEqual(before['player']['y'],after['player']['y'])

    def test_original_dialogue_uses_separate_ui_layer(self):
        self.command(op='event',event=9005)
        for _ in range(90):
            state = self.tick()
        self.assertEqual(3,state['render_layers'])
        self.assertIn('WaitInput',state['script'])
        ui = self.lib.cave_pixels(self.handle,2)
        self.assertTrue(ui)
        pixels = ctypes.string_at(ui,320*240*4)
        self.assertGreater(sum(pixels[3::4]),0)
        # Dialogue must not be baked into the world foreground buffer.
        foreground = ctypes.string_at(self.lib.cave_pixels(self.handle,1),320*240*4)
        self.assertEqual(0,sum(foreground[3::4]))

    def test_external_player_keeps_air_without_duplicate_corner_hud(self):
        self.command(op='event',event=9006)
        for _ in range(80):
            state = self.tick()
        self.assertTrue(state['control_enabled'])
        ui = ctypes.string_at(self.lib.cave_pixels(self.handle,2),320*240*4)
        self.assertEqual(0,sum(ui[3:320*64*4:4]))
        state = self.tick(y=4000)
        self.assertLess(state['player']['air'],1000)
        ui = ctypes.string_at(self.lib.cave_pixels(self.handle,2),320*240*4)
        self.assertEqual(0,sum(ui[3:320*64*4:4]))
        self.assertGreater(sum(ui[320*100*4+3:320*140*4:4]),0)

    def test_booster_2_direction_fuel_and_release(self):
        state=self.equip(9001)
        self.assertEqual(32,state['player']['equipment'] & 32)
        self.assertEqual(50,state['player']['booster_fuel'])
        state=self.tick(64|2)
        self.assertTrue(state['player']['booster_active'])
        self.assertEqual(3,state['player']['booster_direction'])
        self.assertEqual(49,state['player']['booster_fuel'])
        self.assertGreater(state['player']['vx'],2.9)
        self.assertTrue(state['player']['force_velocity'])
        state=self.tick(0,vx=state['player']['vx'])
        self.assertFalse(state['player']['booster_active'])
        self.assertAlmostEqual(1.499,state['player']['vx'],delta=.01)
        state=self.tick(grounded=True)
        self.assertEqual(50,state['player']['booster_fuel'])

    def test_real_host_jump_sound_does_not_trigger_booster(self):
        before = self.equip(9001)
        state = self.tick(64,jump_started=True,vy=-2.5)
        self.assertEqual(before['audio_trace']['sfx_events']+1,state['audio_trace']['sfx_events'])
        self.assertEqual(15,state['audio_trace']['last_sfx'])
        self.assertFalse(state['player']['booster_active'])
        self.assertEqual(50,state['player']['booster_fuel'])
        after = self.tick(64,vy=-2.5)
        self.assertEqual(state['audio_trace']['sfx_events'],after['audio_trace']['sfx_events'])

    def test_failed_jump_input_does_not_play_jump_sound(self):
        before = self.command(op='snapshot')
        after = self.tick(64,jump_started=False)
        self.assertEqual(before['audio_trace']['sfx_events'],after['audio_trace']['sfx_events'])

    def test_hotbar_selection_preserves_charge_and_spur_reset(self):
        self.command(op='event',event=9007)
        self.tick()
        for _ in range(35):
            state = self.command(op='tick',controls=128,weapon=13,player=dict(x=160,y=120,grounded=True))
        spur = next(w for w in state['weapons'] if w['id']==13)
        self.assertGreater(spur['level'],1)
        state = self.command(op='tick',controls=0,weapon=0,player=dict(x=160,y=120,grounded=True))
        self.assertEqual(spur,next(w for w in state['weapons'] if w['id']==13))
        self.assertTrue(all(b['age']>1 for b in state['bullets']))
        state = self.command(op='tick',controls=128,weapon=13,player=dict(x=160,y=120,grounded=True))
        spur = next(w for w in state['weapons'] if w['id']==13)
        self.assertEqual(1,spur['level'])
        self.assertEqual(2,spur['experience'])
        # Repeating the same held weapon must not repeatedly reset its XP.
        state = self.command(op='tick',controls=128,weapon=13,player=dict(x=160,y=120,grounded=True))
        self.assertEqual(4,next(w for w in state['weapons'] if w['id']==13)['experience'])
        state = self.command(op='tick',controls=0,weapon=2,player=dict(x=160,y=120,grounded=True))
        state = self.command(op='tick',controls=128,weapon=13,player=dict(x=160,y=120,grounded=True))
        # The unchanged native refire timer suppresses this rapid trigger's first
        # tick. Entry still resets XP immediately; holding then resumes charging.
        self.assertEqual(0,next(w for w in state['weapons'] if w['id']==13)['experience'])
        state = self.command(op='tick',controls=128,weapon=13,player=dict(x=160,y=120,grounded=True))
        self.assertEqual(2,next(w for w in state['weapons'] if w['id']==13)['experience'])

    def test_booster_08_lifts_without_overriding_ordinary_motion(self):
        state=self.equip(9000)
        self.assertEqual(1,state['player']['equipment'] & 1)
        state=self.tick(64,vy=-1)
        self.assertTrue(state['player']['booster_active'])
        self.assertLess(state['player']['vy'],-1)
        self.assertEqual(49,state['player']['booster_fuel'])
        state=self.tick(0,vx=1.2,vy=-.4)
        self.assertFalse(state['player']['booster_active'])
        self.assertAlmostEqual(1.2,state['player']['vx'],delta=.003)
        self.assertAlmostEqual(-.4,state['player']['vy'],delta=.003)
        self.assertFalse(state['player']['force_velocity'])

    def test_native_rocket_support_refills_and_allows_jump(self):
        self.equip(9001)
        self.command(op='event',event=9002)
        self.tick(x=160,y=120)
        state=self.tick(x=160,y=148,vy=1)
        self.assertTrue(state['player']['native_support'],state['player'])
        state=self.tick(64,x=160,y=148,vy=1)
        self.assertFalse(state['player']['native_support'])
        self.assertAlmostEqual(-2.5,state['player']['vy'],places=3)
        self.assertEqual(50,state['player']['booster_fuel'])
        self.assertTrue(state['player']['force_velocity'])

    def test_ironhead_owns_motion_and_ignores_host_integration(self):
        self.command(op='event',event=9008)
        before = self.tick()
        self.assertTrue(before['player']['ironhead'])
        # Supply deliberately hostile host motion; only HP and body metadata
        # may be imported in this authored swimming mode.
        previous = before['player']
        for i in range(1,7):
            state = self.tick(2,x=9999,y=9999,vx=99,vy=99,
                              grounded=True,wet=True,life=7,max_life=9)
            player = state['player']
            self.assertAlmostEqual(min(i*.5,2),player['vx'],places=3)
            self.assertAlmostEqual(previous['x']+player['vx'],player['x'],places=3)
            self.assertEqual(previous['y'],player['y'])
            self.assertEqual(0,player['vy'])
            self.assertEqual(7,player['life'])
            self.assertEqual(9,player['max_life'])
            self.assertTrue(player['force_position'])
            previous = player

    def test_native_up_current_caps_only_current_motion(self):
        ordinary = self.tick(vx=8,vy=-8)
        self.assertEqual(8,ordinary['player']['vx'])
        self.assertEqual(-8,ordinary['player']['vy'])
        self.command(op='event',event=9009)
        self.tick(x=160,y=128)
        velocity = 0
        for _ in range(30):
            state = self.tick(x=160,y=128,vy=velocity)
            self.assertTrue(state['player']['flags'] & 0x2000)
            velocity = state['player']['vy']
            self.assertGreaterEqual(velocity,-1535/512)
        self.assertEqual(-1535/512,velocity)

    def test_lethal_native_spike_defers_to_real_host_damage(self):
        state=self.command(op='warp',stage=10,x=160,y=120)
        tile=next(i for i,t in enumerate(state['map']['tiles']) if state['map']['attributes'][t] in (0x42,0x62))
        x,y=tile % state['stage']['width']*16,tile // state['stage']['width']*16
        state=self.tick(x=x,y=y)
        self.assertEqual(10,state['player']['pending_damage_raw'])
        self.assertEqual(3,state['player']['life'])
        self.assertTrue(state['player']['alive'])
        state=self.command(op='death')
        self.assertFalse(state['player']['alive'])
        self.assertEqual(0,state['player']['life'])
        self.assertIn('40',state['script'])

    def test_original_audio_requests_and_host_volume(self):
        self.command(op='audio',music_volume=.2,sfx_volume=.3)
        self.command(op='event',event=9003)
        state=self.tick()
        self.assertEqual(8,state['audio_trace']['last_song'])
        self.assertEqual(12,state['audio_trace']['last_sfx'])
        self.assertGreaterEqual(state['audio_trace']['song_events'],1)
        self.assertGreaterEqual(state['audio_trace']['sfx_events'],1)
        self.assertAlmostEqual(.2,state['audio_trace']['music_volume'],places=5)
        self.assertAlmostEqual(.3,state['audio_trace']['sfx_volume'],places=5)


if __name__=='__main__': unittest.main(verbosity=2)
