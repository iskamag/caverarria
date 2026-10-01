//! Runs upstream scenes, TSC, NPCs, bosses, save profiles and original draw code.
//! Included inside doukutsu-rs so the adapter need not make engine internals public.
use crate::common::{Color, Direction, Rect};
use crate::data::builtin_fs::BuiltinFS;
use crate::data::vanilla::VanillaExtractor;
use crate::framework::backend::{
    BackendRenderer, BackendShader, BackendTexture, SpriteBatchCommand, VertexData,
};
use crate::framework::context::Context;
use crate::framework::error::GameResult;
use crate::framework::filesystem::{mount_user_vfs, mount_vfs};
use crate::framework::graphics::BlendMode;
use crate::framework::vfs::PhysicalFS;
use crate::game::npc::list::NPCTokenProvider;
use crate::game::npc::NPC;
use crate::game::physics::PhysicalEntity;
use crate::game::player::ControlMode;
use crate::game::shared_game_state::{SharedGameState, TimingMode};
use crate::input::replay_player_controller::{KeyState, ReplayController};
use crate::scene::game_scene::GameScene;
use crate::scene::Scene;
use serde_json::{json, Value};
use std::any::Any;
use std::cell::RefCell;
use std::ffi::CString;
use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::path::Path;
use std::rc::Rc;

#[cfg(feature = "portable")]
#[path = "portable.rs"]
pub mod portable;

pub struct Runtime {
    ctx: Context,
    state: SharedGameState,
    scene: Box<dyn Scene>,
    raster: Rc<RefCell<Raster>>,
    controller: ReplayController,
    epoch: u64,
    map_hash: u64,
    terrain: BTreeMap<usize, BTreeMap<usize, u16>>,
    saved_terrain: BTreeMap<usize, BTreeMap<usize, u16>>,
    terrain_persistent: bool,
    force_position: bool,
    force_velocity: bool,
    life_delta: i32,
    #[cfg(feature = "pull-audio")]
    audio: Vec<i16>,
    pub response: CString,
}
fn err<E: std::fmt::Display>(e: E) -> String {
    e.to_string()
}
impl Runtime {
    pub fn new(data: &str, save: &str, width: i32, height: i32) -> Result<Self, String> {
        if !(160..=1920).contains(&width) || !(120..=1080).contains(&height) {
            return Err("Invalid viewport dimensions".into());
        }
        #[cfg(not(feature = "portable"))]
        let data = Path::new(data);
        #[cfg(not(feature = "portable"))]
        if !data.join("stage.sect").exists() {
            VanillaExtractor::from_path(
                data.parent().unwrap().join("Doukutsu.exe"),
                data.file_name().unwrap().to_string_lossy().into_owned(),
            )
            .map_err(err)?
            .extract_data()
            .map_err(err)?;
        }
        #[cfg(not(feature = "portable"))]
        std::fs::create_dir_all(save).map_err(err)?;
        let mut ctx = Context::new();
        // Audio is enabled in ordinary installed worlds. Explicit 0 keeps tests
        // silent; texture capture still needs headless=false after initialization.
        #[cfg(not(feature = "portable"))]
        {
            ctx.headless = std::env::var("CAVERARRIA_AUDIO").as_deref() == Ok("0");
        }
        #[cfg(feature = "portable")]
        mount_vfs(&mut ctx, portable::data_fs());
        #[cfg(not(feature = "portable"))]
        mount_vfs(&mut ctx, Box::new(PhysicalFS::new(data, true)));
        mount_vfs(&mut ctx, Box::new(BuiltinFS::new()));
        #[cfg(feature = "portable")]
        mount_user_vfs(&mut ctx, portable::save_fs());
        #[cfg(not(feature = "portable"))]
        mount_user_vfs(&mut ctx, Box::new(PhysicalFS::new(Path::new(save), false)));
        ctx.screen_size = (width as f32, height as f32);
        ctx.real_screen_size = (width as u32, height as u32);
        let raster = Rc::new(RefCell::new(Raster::new(width as usize, height as usize)));
        #[cfg(feature = "imgui")]
        let mut imgui = imgui::Context::create();
        #[cfg(feature = "imgui")]
        {
            imgui.io_mut().display_size = [width as f32, height as f32];
        }
        #[cfg(feature = "imgui")]
        imgui.fonts().build_alpha8_texture();
        ctx.renderer = Some(Box::new(Renderer {
            raster: raster.clone(),
            #[cfg(feature = "imgui")]
            imgui: RefCell::new(imgui),
        }));
        let mut state = SharedGameState::new(&mut ctx).map_err(err)?;
        ctx.headless = false;
        state.settings.original_textures = true;
        state.settings.shader_effects = false;
        state.settings.touch_controls = false;
        state.settings.seasonal_textures = false;
        state.settings.motion_interpolation = true;
        state.settings.debug_outlines = false;
        state.settings.speed = 1.0;
        state.settings.timing_mode = TimingMode::_60Hz;
        state.scale = 1.0;
        state.screen_size = (width as f32, height as f32);
        state.canvas_size = (width as f32, height as f32);
        state.frame_time = 1.0;
        state.reload_resources(&mut ctx).map_err(err)?;
        state.load_or_start_game(&mut ctx).map_err(err)?;
        let mut scene = state
            .next_scene
            .take()
            .ok_or("Profile loading did not create scene")?;
        scene.init(&mut state, &mut ctx).map_err(err)?;
        if let Some(game) = downcast::Downcast::<GameScene>::downcast_mut(&mut *scene).ok() {
            game.player1.external_kinematics = true;
        }
        let terrain = match crate::framework::filesystem::user_open(&ctx, "/Terrain.json") {
            Ok(mut file) => {
                let mut bytes = Vec::new();
                file.read_to_end(&mut bytes).map_err(err)?;
                let document: Value = serde_json::from_slice(&bytes).map_err(err)?;
                // 0.2.0 changes the physical grid from 3x3 to exact 2x2.
                // Old masks have incompatible meaning; retain the original file
                // until host archival/new edits, and leave campaign Profile.dat alone.
                if document["version"].as_u64() == Some(2) && document["subdivisions"].as_u64() == Some(2) {
                    let edits: BTreeMap<usize, BTreeMap<usize, u16>> = serde_json::from_value(document["stages"].clone()).map_err(err)?;
                    if edits.values().any(|cells| cells.values().any(|&mask| mask > 15)) {
                        return Err("Invalid saved terrain mask for 2x2 layout".into());
                    }
                    edits
                } else { BTreeMap::new() }
            }
            Err(_) => BTreeMap::new(),
        };
        let mut runtime = Self {
            ctx,
            state,
            scene,
            raster,
            controller: ReplayController::new(),
            epoch: 1,
            map_hash: 0,
            saved_terrain: terrain.clone(),
            terrain,
            terrain_persistent: true,
            force_position: true,
            force_velocity: true,
            life_delta: 0,
            #[cfg(feature = "pull-audio")]
            audio: Vec::new(),
            response: CString::new("{}").unwrap(),
        };
        runtime.apply_terrain();
        Ok(runtime)
    }
    fn apply_terrain(&mut self) {
        if let Ok(game) = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene) {
            game.stage.map.terrain_edits = self.terrain.get(&game.stage_id).cloned().unwrap_or_default();
        }
    }
    fn persist_terrain(&mut self) -> Result<(), String> {
        let bytes = serde_json::to_vec(&json!({"version":2,"subdivisions":2,"stages":self.terrain})).map_err(err)?;
        let mut file = crate::framework::filesystem::user_create(&self.ctx, "/Terrain.json").map_err(err)?;
        file.write_all(&bytes).map_err(err)?;
        file.flush().map_err(err)?;
        self.saved_terrain = self.terrain.clone();
        Ok(())
    }
    pub fn pixels(&self, layer: usize) -> *const u8 {
        self.raster.borrow().buffers[layer].as_ptr()
    }
    #[cfg(feature = "pull-audio")]
    pub fn audio(&mut self, frames: usize) -> *const i16 {
        self.audio.resize(frames * 2, 0);
        self.state.sound_manager.render_pcm(&mut self.audio);
        self.audio.as_ptr()
    }
    #[cfg(feature = "pull-audio")]
    pub fn audio_length(&self) -> usize {
        self.audio.len() * 2
    }
    fn transitions(&mut self) -> Result<bool, String> {
        let mut changed = false;
        let external = downcast::Downcast::<GameScene>::downcast_ref(&*self.scene)
            .map(|game| game.player1.external_kinematics)
            .unwrap_or(true);
        while let Some(mut scene) = self.state.next_scene.take() {
            scene.init(&mut self.state, &mut self.ctx).map_err(err)?;
            if let Ok(game) = downcast::Downcast::<GameScene>::downcast_mut(&mut *scene) {
                game.player1.external_kinematics = external;
            }
            self.scene = scene;
            self.epoch += 1;
            self.map_hash = 0;
            changed = true;
        }
        if changed { self.apply_terrain(); }
        Ok(changed)
    }
    pub fn command(&mut self, input: &str) -> Result<String, String> {
        let v: Value = serde_json::from_str(input).map_err(err)?;
        let op = v["op"].as_str().unwrap_or("tick");
        let mut changed = false;
        self.force_position = false;
        self.force_velocity = false;
        self.life_delta = 0;
        let mut hit_accepted = false;
        let mut terrain_accepted = false;
        let mut tile_hit_flags = 0u32;
        match op {
            "resize" => {
                let width = v["width"].as_i64().ok_or("Resize requires width")?;
                let height = v["height"].as_i64().ok_or("Resize requires height")?;
                if !(160..=1920).contains(&width) || !(120..=1080).contains(&height) {
                    return Err("Invalid viewport dimensions".into());
                }
                // Keep the shared Raster allocation: all cached engine textures
                // hold this Rc. Only its pixel buffers and canvas dimensions change.
                self.raster
                    .borrow_mut()
                    .resize(width as usize, height as usize);
                self.ctx.screen_size = (width as f32, height as f32);
                self.ctx.real_screen_size = (width as u32, height as u32);
                self.state.screen_size = (width as f32, height as f32);
                self.state.canvas_size = (width as f32, height as f32);
                self.state.scale = 1.0;
                #[cfg(feature = "imgui")]
                if let Some(renderer) = &self.ctx.renderer {
                    if let Some(renderer) = renderer.as_any().downcast_ref::<Renderer>() {
                        renderer.imgui.borrow_mut().io_mut().display_size =
                            [width as f32, height as f32];
                    }
                }
            }
            "new" => {
                self.terrain.clear();
                self.persist_terrain()?;
                self.state.start_new_game(&mut self.ctx).map_err(err)?;
                changed = self.transitions()?;
            }
            "load" | "retry" => {
                if !self.terrain_persistent { self.terrain = self.saved_terrain.clone(); }
                self.state.load_or_start_game(&mut self.ctx).map_err(err)?;
                changed = self.transitions()?;
            }
            "save" => {
                let game = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene)
                    .map_err(|_| "No game scene")?;
                self.state
                    .save_game(game, &mut self.ctx, None)
                    .map_err(err)?;
            }
            "hit" => {
                if v["epoch"].as_u64().unwrap_or(self.epoch) == self.epoch {
                    let id = v["id"].as_u64().ok_or("Hit requires id")? as usize;
                    let damage = v["damage"]
                        .as_u64()
                        .ok_or("Hit requires positive damage")?
                        .min(32767) as u16;
                    if damage > 0 {
                        hit_accepted = self.hit(
                            id,
                            v["boss"].as_bool().unwrap_or(false),
                            damage,
                            v["generation"].as_u64(),
                        )?;
                    }
                }
            }
            "terrain_persistence" => {
                let enabled = v["enabled"].as_bool().ok_or("Terrain persistence requires enabled")?;
                if enabled && !self.terrain_persistent { self.persist_terrain()?; }
                self.terrain_persistent = enabled;
            }
            "terrain_edit" => {
                let epoch = v["epoch"].as_u64().ok_or("Terrain edit requires epoch")?;
                let stage = usize::try_from(v["stage"].as_u64().ok_or("Terrain edit requires stage")?).map_err(|_| "Terrain stage out of bounds")?;
                let x = usize::try_from(v["x"].as_u64().ok_or("Terrain edit requires nonnegative x")?).map_err(|_| "Terrain x out of bounds")?;
                let y = usize::try_from(v["y"].as_u64().ok_or("Terrain edit requires nonnegative y")?).map_err(|_| "Terrain y out of bounds")?;
                let solid = v["solid"].as_bool().ok_or("Terrain edit requires solid")?;
                let game = downcast::Downcast::<GameScene>::downcast_ref(&*self.scene).map_err(|_| "No game scene")?;
                if epoch == self.epoch && stage == game.stage_id
                    && x < game.stage.map.width as usize && y < game.stage.map.height as usize {
                    let index = y * game.stage.map.width as usize + x;
                    let sub_x = v["sub_x"].as_u64(); let sub_y = v["sub_y"].as_u64();
                    let mask = match (sub_x, sub_y) {
                        (None, None) if v["sub_x"].is_null() && v["sub_y"].is_null() => if solid { 15 } else { 0 },
                        (Some(sx), Some(sy)) if sx < 2 && sy < 2 => {
                            let prior = self.terrain.get(&stage).and_then(|m| m.get(&index)).copied();
                            // Partial placement never replaces an authored slope,
                            // hazard, water or wall. Mine authored terrain first.
                            if prior.is_none() && game.stage.map.get_attribute(x, y) != 0 {
                                return Err("Partial edit requires an empty or player-edited cell".into());
                            }
                            let old = prior.unwrap_or(0);
                            let bit = 1u16 << (sy * 2 + sx);
                            if solid { old | bit } else { old & !bit }
                        }
                        _ => return Err("Terrain subcell requires sub_x and sub_y in 0..2".into()),
                    };
                    self.terrain.entry(stage).or_default().insert(index, mask);
                    if self.terrain_persistent { self.persist_terrain()?; }
                    self.apply_terrain();
                    terrain_accepted = true;
                }
            }
            "tile_hit" => {
                let game = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene)
                    .map_err(|_| "No game scene")?;
                let x = (v["x"].as_f64().ok_or("tile_hit requires native x")? * 512.0) as i32;
                let y = (v["y"].as_f64().ok_or("tile_hit requires native y")? * 512.0) as i32;
                let mut bullet = crate::game::weapon::bullet::Bullet::new(
                    x,
                    y,
                    4,
                    crate::game::player::TargetPlayer::Player1,
                    Direction::Right,
                    &self.state.constants,
                );
                bullet.weapon_flags.set_check_block_hit(true);
                bullet.hit_bounds.left = (v["width"].as_f64().unwrap_or(8.0) * 256.0) as u32;
                bullet.hit_bounds.right = bullet.hit_bounds.left;
                bullet.hit_bounds.top = (v["height"].as_f64().unwrap_or(8.0) * 256.0) as u32;
                bullet.hit_bounds.bottom = bullet.hit_bounds.top;
                // This is the actual upstream bullet collision, including snack tile
                // decrement, native caret, sound, and smoke spawns.
                bullet.tick_map_collisions(&mut self.state, &game.npc_list, &mut game.stage);
                tile_hit_flags = bullet.flags.0;
            }
            "use_item" => {
                let id = v["id"].as_u64().ok_or("Item use requires id")?;
                if id > u16::MAX as u64 - 6000 {
                    return Err("Invalid item id".into());
                }
                let game = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene)
                    .map_err(|_| "No game scene")?;
                if game.player1.cond.alive()
                    && game.inventory_player1.has_item(id as u16)
                    && self.state.control_flags.control_enabled()
                    && self.state.textscript_vm.mode == crate::game::scripting::tsc::text_script::ScriptMode::Map
                    && self.state.textscript_vm.state == crate::game::scripting::tsc::text_script::TextScriptExecutionState::Ended
                {
                    game.external_inventory = true;
                    self.state.textscript_vm.set_mode(crate::game::scripting::tsc::text_script::ScriptMode::Inventory);
                    self.state.textscript_vm.start_script(6000 + id as u16);
                }
            }
            "event" => {
                self.state
                    .textscript_vm
                    .start_script(v["event"].as_u64().ok_or("event id missing")? as u16);
            }
            "flag" => {
                self.state.set_flag(
                    v["id"].as_u64().ok_or("flag id missing")? as usize,
                    v["value"].as_bool().unwrap_or(true),
                );
            }
            "warp" => {
                let id = v["stage"].as_u64().ok_or("Stage id missing")? as usize;
                if id >= self.state.stages.len() {
                    return Err("Stage out of bounds".into());
                }
                let mut next = GameScene::new(&mut self.state, &mut self.ctx, id).map_err(err)?;
                let current = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene)
                    .map_err(|_| "No game scene")?;
                next.player1 = current.player1.clone();
                next.inventory_player1 = current.inventory_player1.clone();
                next.player1.x = (v["x"].as_f64().unwrap_or(64.0) * 512.0) as i32;
                next.player1.y = (v["y"].as_f64().unwrap_or(64.0) * 512.0) as i32;
                next.player1.vel_x = 0;
                next.player1.vel_y = 0;
                self.state.textscript_vm.state =
                    crate::game::scripting::tsc::text_script::TextScriptExecutionState::Ended;
                self.state.control_flags.set_control_enabled(true);
                self.state.control_flags.set_interactions_disabled(false);
                self.state.control_flags.set_tick_world(true);
                self.state.next_scene = Some(Box::new(next));
                changed = self.transitions()?;
            }
            "death" => {
                let game = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene)
                    .map_err(|_| "No game scene")?;
                game.player1.shock_counter = 0;
                // Actual host death decides lethality; invoke the original death
                // path explicitly rather than applying armor twice in the engine.
                let external = game.player1.external_kinematics;
                game.player1.external_kinematics = false;
                game.player1.damage(
                    game.player1.life.max(1) as i32,
                    &mut self.state,
                    &game.npc_list,
                );
                game.player1.external_kinematics = external;
            }
            "audio" => {
                #[cfg(feature = "pull-audio")]
                if let Some(enabled) = v["enabled"].as_bool() {
                    self.state.sound_manager.set_audio_enabled(
                        enabled,
                        &self.state.constants,
                        &self.state.settings,
                        &mut self.ctx,
                    ).map_err(err)?;
                }
                let music = v["music_volume"]
                    .as_f64()
                    .unwrap_or(self.state.settings.bgm_volume as f64)
                    .clamp(0.0, 1.0) as f32;
                let sfx = v["sfx_volume"]
                    .as_f64()
                    .unwrap_or(self.state.settings.sfx_volume as f64)
                    .clamp(0.0, 1.0) as f32;
                self.state.settings.bgm_volume = music;
                self.state.settings.sfx_volume = sfx;
                self.state.sound_manager.set_song_volume(music);
                self.state.sound_manager.set_sfx_volume(sfx);
            }
            "tick" => {
                self.state.frame_time = 1.0;
                self.controller.state = KeyState(v["controls"].as_u64().unwrap_or(0) as u16);
                let mut before = None;
                if let Ok(game) = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene) {
                    game.hud_player1.hide_weapon_progress = v["host_inventory_open"].as_bool().unwrap_or(false);
                    game.player1.external_damage_pending = 0;
                    game.player1.external_jump_started = false;
                    game.player1.controller = Box::new(self.controller);
                    game.player1.external_kinematics = v["external"].as_bool().unwrap_or(true);
                    let was_weapon_active = game.player1.external_weapon_active;
                    game.player1.external_weapon_active = v["weapon"].as_u64().is_none();
                    if let Some(weapon) = v["weapon"].as_u64() {
                        for i in 0..game.inventory_player1.get_weapon_count() {
                            if game
                                .inventory_player1
                                .get_weapon(i)
                                .is_some_and(|w| w.wtype as u64 == weapon)
                            {
                                game.player1.external_weapon_active = true;
                                if game.inventory_player1.current_weapon != i as u16
                                    || !was_weapon_active
                                {
                                    game.inventory_player1.current_weapon = i as u16;
                                    if let Some(selected) =
                                        game.inventory_player1.get_current_weapon_mut()
                                    {
                                        if selected.wtype == crate::game::weapon::WeaponType::Spur {
                                            selected.reset_xp();
                                        }
                                    }
                                    self.state.sound_manager.play_sfx(4);
                                }
                                break;
                            }
                        }
                    }
                    if self.state.control_flags.control_enabled()
                        && game.player1.external_kinematics
                        && game.player1.cond.alive()
                    {
                        let p = &v["player"];
                        // Ironhead uses the original swim controller and integrates
                        // motion here. The host mirrors that result but must not
                        // feed its own integration back into the next native tick.
                        if game.player1.control_mode == ControlMode::Normal {
                            game.player1.external_jump_started =
                                p["jump_started"].as_bool().unwrap_or(false);
                            if let Some(x) = p["x"].as_f64() {
                                game.player1.x = (x * 512.0) as i32;
                            }
                            if let Some(y) = p["y"].as_f64() {
                                game.player1.y = (y * 512.0) as i32;
                            }
                            game.player1.vel_x = (p["vx"].as_f64().unwrap_or(0.0) * 512.0) as i32;
                            game.player1.vel_y = (p["vy"].as_f64().unwrap_or(0.0) * 512.0) as i32;
                            game.player1
                                .flags
                                .set_hit_bottom_wall(p["grounded"].as_bool().unwrap_or(false));
                            game.player1
                                .flags
                                .set_in_water(p["wet"].as_bool().unwrap_or(false));
                        }
                        if let Some(life) = p["life"].as_u64() {
                            game.player1.life = life.min(65535) as u16;
                        }
                        // Campaign maximum belongs to native capsules/scripts. Host
                        // effective-health recalculation must never manufacture capsules.
                        if let Some(direction) = p["direction"].as_i64() {
                            game.player1.direction = if direction < 0 {
                                Direction::Left
                            } else {
                                Direction::Right
                            };
                        }
                        if let Some(width) = p["width"].as_f64() {
                            game.player1.hit_bounds.left = (width * 256.0) as u32;
                            game.player1.hit_bounds.right = (width * 256.0) as u32;
                        }
                        if let Some(height) = p["height"].as_f64() {
                            game.player1.hit_bounds.top = (height * 256.0) as u32;
                            game.player1.hit_bounds.bottom = (height * 256.0) as u32;
                        }
                    }
                    before = Some((
                        game.player1.x,
                        game.player1.y,
                        game.player1.vel_x,
                        game.player1.vel_y,
                        game.player1.life,
                    ));
                    if self.state.control_flags.control_enabled()
                        && game.player1.external_kinematics
                    {
                        sample_environment(&self.state, game);
                    }
                }
                self.scene.draw_tick(&mut self.state).map_err(err)?;
                self.scene
                    .tick(&mut self.state, &mut self.ctx)
                    .map_err(err)?;
                self.controller.old_state = self.controller.state;
                changed = self.transitions()?;
                if let (Some((x, y, vx, vy, life)), Ok(game)) = (
                    before,
                    downcast::Downcast::<GameScene>::downcast_ref(&*self.scene),
                ) {
                    self.force_position = changed
                        || !self.state.control_flags.control_enabled()
                        || game.player1.x != x
                        || game.player1.y != y;
                    self.force_velocity = changed
                        || !self.state.control_flags.control_enabled()
                        || game.player1.vel_x != vx
                        || game.player1.vel_y != vy;
                    self.life_delta = game.player1.life as i32 - life as i32;
                }
            }
            "snapshot" => (),
            _ => return Err(format!("Unknown operation {op}")),
        }
        self.state.frame_time = 1.0;
        // Queue combat, tile and audio mutations without repeatedly rendering
        // the same update. The next tick captures their combined result. Scene
        // changes and explicit snapshots still return a current frame.
        if changed
            || matches!(op, "tick" | "snapshot" | "resize")
            || v["render"].as_bool().unwrap_or(false)
        {
            self.raster
                .borrow_mut()
                .reset(self.state.constants.background_color);
            // UI drawing may use a larger canvas than a zoomed world viewport.
            // Restore simulation dimensions even if an authored draw fails.
            let canvas = self.state.canvas_size;
            let screen = self.state.screen_size;
            let context_screen = self.ctx.screen_size;
            let real_screen = self.ctx.real_screen_size;
            let draw_result = self.scene.draw(&mut self.state, &mut self.ctx);
            self.state.canvas_size = canvas;
            self.state.screen_size = screen;
            self.ctx.screen_size = context_screen;
            self.ctx.real_screen_size = real_screen;
            self.raster.borrow_mut().end_ui();
            draw_result.map_err(err)?;
        }
        let mut result = self.snapshot(changed)?;
        result["hit_accepted"] = json!(hit_accepted);
        result["terrain_edit_accepted"] = json!(terrain_accepted);
        result["terrain_persistent"] = json!(self.terrain_persistent);
        result["terrain_layout_version"] = json!(2);
        if op == "tile_hit" { result["tile_hit_flags"] = json!(tile_hit_flags); }
        Ok(result.to_string())
    }
    fn hit(
        &mut self,
        id: usize,
        boss: bool,
        damage: u16,
        generation: Option<u64>,
    ) -> Result<bool, String> {
        let game = downcast::Downcast::<GameScene>::downcast_mut(&mut *self.scene)
            .map_err(|_| "No game scene")?;
        if boss {
            if id >= game.boss.parts.len() {
                return Ok(false);
            }
            let part = &game.boss.parts[id];
            if !part.cond.alive() || !part.npc_flags.shootable() {
                return Ok(false);
            }
            let idx = if part.cond.damage_boss() { 0 } else { id };
            let npc = &mut game.boss.parts[idx];
            npc.life = npc.life.saturating_sub(damage);
            if npc.life == 0 {
                npc.life = npc.id;
                if game.player1.cond.alive() && npc.npc_flags.event_when_killed() {
                    self.state.control_flags.set_tick_world(true);
                    self.state.control_flags.set_interactions_disabled(true);
                    self.state.textscript_vm.start_script(npc.event_num);
                } else {
                    self.state
                        .sound_manager
                        .play_sfx(game.boss.death_sound[idx]);
                    let count = 4usize * (2usize).pow((npc.size as u32).saturating_sub(1));
                    game.npc_list.create_death_smoke(
                        npc.x,
                        npc.y,
                        npc.display_bounds.right as usize,
                        count,
                        &mut self.state,
                        &npc.rng,
                    );
                    npc.cond.set_alive(false);
                }
            } else {
                if npc.shock < 14 {
                    self.state.sound_manager.play_sfx(game.boss.hurt_sound[idx]);
                }
                npc.shock = 8;
                if npc.npc_flags.show_damage() {
                    npc.popup.add_value(-(damage as i16));
                }
                game.boss.parts[id].shock = 8;
            }
            Ok(true)
        } else {
            let Some(cell) = game.npc_list.get_npc(id) else {
                return Ok(false);
            };
            let mut npc = cell.borrow_mut(&mut game.npc_token);
            if generation.is_some_and(|g| g != npc.external_generation) {
                return Ok(false);
            }
            if !npc.cond.alive() || !npc.npc_flags.shootable() || npc.npc_flags.interactable() {
                return Ok(false);
            }
            npc.life = npc.life.saturating_sub(damage);
            if npc.npc_flags.show_damage() {
                npc.popup.add_value(-(damage as i16));
            }
            if npc.life == 0 {
                if game.player1.cond.alive() && npc.npc_flags.event_when_killed() {
                    self.state.control_flags.set_tick_world(true);
                    self.state.control_flags.set_interactions_disabled(true);
                    self.state.textscript_vm.start_script(npc.event_num);
                } else {
                    let can_drop_missile = game
                        .inventory_player1
                        .has_weapon(crate::game::weapon::WeaponType::MissileLauncher)
                        || game
                            .inventory_player1
                            .has_weapon(crate::game::weapon::WeaponType::SuperMissileLauncher);
                    let npc_id = npc.id as usize;
                    let vanish = !npc.cond.drs_novanish();
                    npc.unborrow_then(|token| {
                        game.npc_list.kill_npc(
                            npc_id,
                            vanish,
                            can_drop_missile,
                            &mut self.state,
                            token,
                        )
                    });
                }
            } else {
                if npc.shock < 14 {
                    if let Some(entry) = self.state.npc_table.get_entry(npc.npc_type) {
                        self.state.sound_manager.play_sfx(entry.hurt_sound);
                    }
                }
                npc.shock = 16;
            }
            Ok(true)
        }
    }
    fn snapshot(&mut self, changed: bool) -> Result<Value, String> {
        let stages: Vec<_> = self
            .state
            .stages
            .iter()
            .enumerate()
            .map(|(i, s)| json!({"id":i,"map":s.map,"name":s.name,"boss":s.boss_no}))
            .collect();
        let audio = self.state.sound_manager.audio_trace();
        let mut result = json!({"ok":true,"epoch":self.epoch,"stage_changed":changed,"viewport":{"width":self.raster.borrow().width,"height":self.raster.borrow().height},"control_enabled":self.state.control_flags.control_enabled(),"tick_world":self.state.control_flags.tick_world(),"credits":self.state.control_flags.credits_running(),"script":format!("{:?}",self.state.textscript_vm.state),"script_mode":format!("{:?}",self.state.textscript_vm.mode),"stages":stages,"flags":(0..8000).filter(|&i|self.state.get_flag(i)).collect::<Vec<_>>(),"song":self.state.sound_manager.current_song(),"audio_ready":self.state.sound_manager.audio_ready(),"audio_trace":{"song_events":audio.0,"sfx_events":audio.1,"last_sfx":audio.2,"last_song":audio.3,"music_volume":self.state.settings.bgm_volume,"sfx_volume":self.state.settings.sfx_volume}});
        result["render_layers"] = json!(3);
        result["ui_viewport"] = json!({"width": self.raster.borrow().ui_width, "height": self.raster.borrow().ui_height});
        result["timing_hz"] = json!(self.state.settings.timing_mode.get_tps());
        let Ok(game) = downcast::Downcast::<GameScene>::downcast_ref(&*self.scene) else {
            result["scene"] = json!("title");
            return Ok(result);
        };
        result["scene"] = json!("game");
        result["stage"] = json!({"id":game.stage_id,"map":game.stage.data.map,"name":game.stage.data.name,"width":game.stage.map.width,"height":game.stage.map.height,"tile_size":game.stage.map.tile_size.as_int()});
        // Include current map when its contents changed, including TSC tile edits.
        let mut hash = game
            .stage
            .map
            .tiles
            .iter()
            .fold(1469598103934665603u64, |h, &x| {
                (h ^ x as u64).wrapping_mul(1099511628211)
            });
        for (&index, &mask) in &game.stage.map.terrain_edits {
            hash = (hash ^ index as u64).wrapping_mul(1099511628211);
            hash = (hash ^ mask as u64).wrapping_mul(1099511628211);
        }
        if hash != self.map_hash || changed {
            result["map"] = json!({"tiles":game.stage.map.tiles,"attributes":game.stage.map.attrib.to_vec(),"width":game.stage.map.width,"height":game.stage.map.height,"revision":hash});
            result["map"]["cell_attributes"] = json!((0..game.stage.map.height as usize).flat_map(|y| (0..game.stage.map.width as usize).map(move |x| game.stage.map.get_attribute(x, y))).collect::<Vec<_>>());
            result["map"]["terrain_edits"] = json!(game.stage.map.terrain_edits.iter().map(|(&index, &mask)| json!({"x":index % game.stage.map.width as usize,"y":index / game.stage.map.width as usize,"solid":mask != 0,"mask":mask})).collect::<Vec<_>>());
            self.map_hash = hash;
        }
        let (camera_x, camera_y) = game.frame.xy_interpolated(self.state.frame_time);
        result["camera"] = json!({"x":camera_x,"y":camera_y});
        let p = &game.player1;
        result["player"] = json!({"x":p.x as f64/512.0,"y":p.y as f64/512.0,"vx":p.vel_x as f64/512.0,"vy":p.vel_y as f64/512.0,"life":p.life,"max_life":p.max_life,"alive":p.cond.alive(),"hidden":p.cond.hidden(),"shock":p.shock_counter,"equipment":p.equip.0,"ironhead":p.control_mode==ControlMode::IronHead,"script_locked":!self.state.control_flags.control_enabled(),"force_position":changed||self.force_position,"force_velocity":changed||self.force_velocity,"life_delta":self.life_delta,"air":p.air,"flags":p.flags.0,"pending_damage_raw":p.external_damage_pending,"booster_active":p.booster_active(),"booster_fuel":p.booster_fuel,"booster_direction":p.booster_direction(),"native_support":p.external_support});
        result["npcs"] = json!(game
            .npc_list
            .iter_alive(&game.npc_token)
            .map(|npc| npc_json(&npc, false, npc.id as usize))
            .collect::<Vec<_>>());
        result["bosses"] = json!(game
            .boss
            .parts
            .iter()
            .enumerate()
            .filter(|(_, n)| n.cond.alive())
            .map(|(i, n)| npc_json(n, true, i))
            .collect::<Vec<_>>());
        result["weapons"] = json!((0..16).filter_map(|i| game.inventory_player1.get_weapon(i)).map(|w|json!({"id":w.wtype as u8,"level":w.level as u8,"ammo":w.ammo,"max_ammo":w.max_ammo,"experience":w.experience})).collect::<Vec<_>>());
        result["items"] = json!((0..64)
            .filter_map(|i| game.inventory_player1.get_item_idx(i))
            .map(|x| json!({"id":x.0,"amount":x.1}))
            .collect::<Vec<_>>());
        result["bullets"] = json!(game.bullet_manager.bullets.iter().filter(|b|b.cond.alive()).map(|b|json!({"type":b.btype,"x":b.x as f64/512.0,"y":b.y as f64/512.0,"damage":b.damage,"life":b.life,"age":b.action_counter})).collect::<Vec<_>>());
        result["tick"] = json!(game.tick);
        Ok(result)
    }
}
fn sample_environment(state: &SharedGameState, game: &mut GameScene) {
    let p = &mut game.player1;
    p.flags.0 &= 0x0f; // host ground/wall contacts; sensors are recomputed from original map
    let tile = state.tile_size.as_int() * 512;
    let cx = p.x / tile;
    let cy = p.y / tile;
    for y in cy - 1..=cy + 1 {
        for x in cx - 1..=cx + 1 {
            let attr = game.stage.map.get_attribute(x as usize, y as usize);
            match attr {
                0x42 | 0x62 => p.test_hit_spike(state, x, y, attr & 0x20 != 0),
                0x02 | 0x60 | 0x61 | 0x70..=0x77 => p.test_hit_water(state, x, y),
                0x80 | 0xa0 => p.test_hit_force(state, x, y, Direction::Left, attr & 0x20 != 0),
                0x81 | 0xa1 => p.test_hit_force(state, x, y, Direction::Up, attr & 0x20 != 0),
                0x82 | 0xa2 => p.test_hit_force(state, x, y, Direction::Right, attr & 0x20 != 0),
                0x83 | 0xa3 => p.test_hit_force(state, x, y, Direction::Bottom, attr & 0x20 != 0),
                _ => (),
            }
        }
    }
    if p.y > state.water_level {
        p.flags.set_in_water(true);
    }
}
fn npc_json(n: &NPC, boss: bool, id: usize) -> Value {
    json!({"id":id,"boss":boss,"type":n.npc_type,"life":n.life,"damage":n.damage,"x":n.x as f64/512.0,"y":n.y as f64/512.0,"left":n.hit_bounds.left as f64/512.0,"top":n.hit_bounds.top as f64/512.0,"right":n.hit_bounds.right as f64/512.0,"bottom":n.hit_bounds.bottom as f64/512.0,"shootable":n.npc_flags.shootable() && !n.npc_flags.interactable(),"event":n.event_num,"action":n.action_num,"flags":n.npc_flags.0,"generation":n.external_generation})
}

// The engine's draw order switches after its player layer. Terraria inserts its player there.
pub fn begin_overlay(ctx: &mut Context) {
    set_capture_layer(ctx, 1);
}
pub fn begin_ui(ctx: &mut Context, state: &mut SharedGameState) {
    if let Some(renderer) = &ctx.renderer {
        if let Some(renderer) = renderer.as_any().downcast_ref::<Renderer>() {
            let mut raster = renderer.raster.borrow_mut();
            raster.layer = 2;
            raster.width = raster.ui_width;
            raster.height = raster.ui_height;
            raster.clip = None;
            state.canvas_size = (raster.width as f32, raster.height as f32);
            state.screen_size = state.canvas_size;
            ctx.screen_size = state.canvas_size;
            ctx.real_screen_size = (raster.width as u32, raster.height as u32);
        }
    }
}
fn set_capture_layer(ctx: &mut Context, layer: usize) {
    if let Some(renderer) = &ctx.renderer {
        if let Some(renderer) = renderer.as_any().downcast_ref::<Renderer>() {
            renderer.raster.borrow_mut().layer = layer;
        }
    }
}
struct Raster {
    width: usize,
    height: usize,
    world_width: usize,
    world_height: usize,
    ui_width: usize,
    ui_height: usize,
    layer: usize,
    buffers: [Vec<u8>; 3],
    clip: Option<Rect>,
    blend: BlendMode,
}
impl Raster {
    fn new(width: usize, height: usize) -> Self {
        Self {
            width,
            height,
            world_width: width,
            world_height: height,
            ui_width: width.max(320),
            ui_height: height.max(240),
            layer: 0,
            buffers: [vec![0; width * height * 4], vec![0; width * height * 4],
                vec![0; width.max(320) * height.max(240) * 4]],
            clip: None,
            blend: BlendMode::Alpha,
        }
    }
    fn reset(&mut self, color: Color) {
        self.layer = 0;
        self.clip = None;
        let c = color.to_rgba();
        for px in self.buffers[0].chunks_exact_mut(4) {
            px.copy_from_slice(&[c.0, c.1, c.2, 255]);
        }
        self.buffers[1].fill(0);
        self.buffers[2].fill(0);
    }
    fn resize(&mut self, width: usize, height: usize) {
        self.width = width;
        self.height = height;
        self.world_width = width;
        self.world_height = height;
        self.ui_width = width.max(320);
        self.ui_height = height.max(240);
        self.buffers[0].resize(width * height * 4, 0);
        self.buffers[1].resize(width * height * 4, 0);
        self.buffers[2].resize(self.ui_width * self.ui_height * 4, 0);
        self.clip = None;
    }
    fn end_ui(&mut self) {
        self.width = self.world_width;
        self.height = self.world_height;
        self.layer = 0;
        self.clip = None;
    }
    fn pixel(&mut self, x: i32, y: i32, c: [u8; 4]) {
        if x < 0 || y < 0 || x >= self.width as i32 || y >= self.height as i32 {
            return;
        }
        if let Some(clip) = self.clip {
            if x < clip.left as i32
                || y < clip.top as i32
                || x >= clip.right as i32
                || y >= clip.bottom as i32
            {
                return;
            }
        }
        let i = (y as usize * self.width + x as usize) * 4;
        let dest = &mut self.buffers[self.layer][i..i + 4];
        let a = c[3] as u32;
        match self.blend {
            BlendMode::None => dest.copy_from_slice(&c),
            BlendMode::Alpha => {
                if a == 0 {
                    return;
                }
                if a == 255 || dest[3] == 0 {
                    dest.copy_from_slice(&c);
                    return;
                }
                let da = dest[3] as u32;
                let oa = a + da * (255 - a) / 255;
                if oa > 0 {
                    for k in 0..3 {
                        dest[k] =
                            ((c[k] as u32 * a + dest[k] as u32 * da * (255 - a) / 255) / oa) as u8;
                    }
                    dest[3] = oa as u8;
                }
            }
            BlendMode::Add => {
                for k in 0..3 {
                    dest[k] = dest[k].saturating_add((c[k] as u32 * a / 255) as u8);
                }
                dest[3] = dest[3].max(c[3]);
            }
            BlendMode::Multiply => {
                for k in 0..3 {
                    dest[k] = (dest[k] as u32 * c[k] as u32 / 255) as u8;
                }
            }
        }
    }
    fn rect(&mut self, rect: Rect, c: Color) {
        let c = c.to_rgba();
        if self.blend == BlendMode::None || self.blend == BlendMode::Alpha && c.3 == 255 {
            let mut left = rect.left.max(0);
            let mut top = rect.top.max(0);
            let mut right = rect.right.min(self.width as isize);
            let mut bottom = rect.bottom.min(self.height as isize);
            if let Some(clip) = self.clip {
                left = left.max(clip.left);
                top = top.max(clip.top);
                right = right.min(clip.right);
                bottom = bottom.min(clip.bottom);
            }
            if right <= left || bottom <= top {
                return;
            }
            let rgba = [c.0, c.1, c.2, c.3];
            for y in top..bottom {
                let start = (y as usize * self.width + left as usize) * 4;
                let end = (y as usize * self.width + right as usize) * 4;
                for pixel in self.buffers[self.layer][start..end].chunks_exact_mut(4) {
                    pixel.copy_from_slice(&rgba);
                }
            }
            return;
        }
        for y in rect.top.max(0)..rect.bottom.min(self.height as isize) {
            for x in rect.left.max(0)..rect.right.min(self.width as isize) {
                self.pixel(x as i32, y as i32, [c.0, c.1, c.2, c.3]);
            }
        }
    }
}
struct Texture {
    width: u16,
    height: u16,
    data: Vec<u8>,
    commands: Vec<SpriteBatchCommand>,
    raster: Rc<RefCell<Raster>>,
}
impl BackendTexture for Texture {
    fn dimensions(&self) -> (u16, u16) {
        (self.width, self.height)
    }
    fn add(&mut self, c: SpriteBatchCommand) {
        self.commands.push(c);
    }
    fn clear(&mut self) {
        self.commands.clear();
    }
    fn draw(&mut self) -> GameResult {
        let mut raster = self.raster.borrow_mut();
        for command in &self.commands {
            let (src, dest, flipx, flipy, tint) = match command {
                SpriteBatchCommand::DrawRect(s, d) => {
                    (*s, *d, false, false, Color::from_rgba(255, 255, 255, 255))
                }
                SpriteBatchCommand::DrawRectFlip(s, d, x, y) => {
                    (*s, *d, *x, *y, Color::from_rgba(255, 255, 255, 255))
                }
                SpriteBatchCommand::DrawRectTinted(s, d, c) => (*s, *d, false, false, *c),
                SpriteBatchCommand::DrawRectFlipTinted(s, d, x, y, c) => (*s, *d, *x, *y, *c),
            };
            if dest.width() <= 0.0 || dest.height() <= 0.0 {
                continue;
            }
            let tint = tint.to_rgba();
            // Most original terrain/background/UI batches copy unscaled atlas
            // rectangles at integer coordinates. Preserve the generic sampler
            // for scaled, tinted, fractional or flipped sprites.
            if !flipx
                && !flipy
                && tint == (255, 255, 255, 255)
                && src.width() == dest.width()
                && src.height() == dest.height()
                && src.left.fract() == 0.0
                && src.top.fract() == 0.0
                && dest.left.fract() == 0.0
                && dest.top.fract() == 0.0
            {
                let left = (dest.left as i32).max(0);
                let top = (dest.top as i32).max(0);
                let right = (dest.right as i32).min(raster.width as i32);
                let bottom = (dest.bottom as i32).min(raster.height as i32);
                for y in top..bottom {
                    let sy = src.top as i32 + y - dest.top as i32;
                    if sy < 0 || sy >= self.height as i32 {
                        continue;
                    }
                    for x in left..right {
                        let sx = src.left as i32 + x - dest.left as i32;
                        if sx < 0 || sx >= self.width as i32 {
                            continue;
                        }
                        let i = (sy as usize * self.width as usize + sx as usize) * 4;
                        let c = [
                            self.data[i],
                            self.data[i + 1],
                            self.data[i + 2],
                            self.data[i + 3],
                        ];
                        raster.pixel(x, y, c);
                    }
                }
                continue;
            }
            for y in (dest.top.round() as i32).max(0)
                ..(dest.bottom.round() as i32).min(raster.height as i32)
            {
                for x in (dest.left.round() as i32).max(0)
                    ..(dest.right.round() as i32).min(raster.width as i32)
                {
                    let mut u = ((x as f32 - dest.left) / dest.width()).clamp(0.0, 0.999999);
                    let mut v = ((y as f32 - dest.top) / dest.height()).clamp(0.0, 0.999999);
                    if flipx {
                        u = 1.0 - u;
                    }
                    if flipy {
                        v = 1.0 - v;
                    }
                    let sx = (src.left + u * src.width()).floor() as i32;
                    let sy = (src.top + v * src.height()).floor() as i32;
                    if sx < 0 || sy < 0 || sx >= self.width as i32 || sy >= self.height as i32 {
                        continue;
                    }
                    let i = (sy as usize * self.width as usize + sx as usize) * 4;
                    let c = [
                        (self.data[i] as u16 * tint.0 as u16 / 255) as u8,
                        (self.data[i + 1] as u16 * tint.1 as u16 / 255) as u8,
                        (self.data[i + 2] as u16 * tint.2 as u16 / 255) as u8,
                        (self.data[i + 3] as u16 * tint.3 as u16 / 255) as u8,
                    ];
                    raster.pixel(x, y, c);
                }
            }
        }
        Ok(())
    }
    fn as_any(&self) -> &dyn Any {
        self
    }
}
struct Renderer {
    raster: Rc<RefCell<Raster>>,
    #[cfg(feature = "imgui")]
    imgui: RefCell<imgui::Context>,
}
impl BackendRenderer for Renderer {
    fn renderer_name(&self) -> String {
        "Caverarria capture".into()
    }
    fn clear(&mut self, c: Color) {
        self.raster.borrow_mut().reset(c);
    }
    fn present(&mut self) -> GameResult {
        Ok(())
    }
    fn create_texture_mutable(&mut self, w: u16, h: u16) -> GameResult<Box<dyn BackendTexture>> {
        self.create_texture(w, h, &vec![0; w as usize * h as usize * 4])
    }
    fn create_texture(
        &mut self,
        w: u16,
        h: u16,
        data: &[u8],
    ) -> GameResult<Box<dyn BackendTexture>> {
        Ok(Box::new(Texture {
            width: w,
            height: h,
            data: data.to_vec(),
            commands: Vec::new(),
            raster: self.raster.clone(),
        }))
    }
    fn set_blend_mode(&mut self, b: BlendMode) -> GameResult {
        self.raster.borrow_mut().blend = b;
        Ok(())
    }
    fn set_render_target(&mut self, _: Option<&Box<dyn BackendTexture>>) -> GameResult {
        Ok(())
    }
    fn draw_rect(&mut self, r: Rect, c: Color) -> GameResult {
        self.raster.borrow_mut().rect(r, c);
        Ok(())
    }
    fn draw_outline_rect(&mut self, r: Rect, w: usize, c: Color) -> GameResult {
        let w = w as isize;
        let mut raster = self.raster.borrow_mut();
        raster.rect(Rect::new(r.left, r.top, r.right, r.top + w), c);
        raster.rect(Rect::new(r.left, r.bottom - w, r.right, r.bottom), c);
        raster.rect(Rect::new(r.left, r.top, r.left + w, r.bottom), c);
        raster.rect(Rect::new(r.right - w, r.top, r.right, r.bottom), c);
        Ok(())
    }
    fn set_clip_rect(&mut self, r: Option<Rect>) -> GameResult {
        self.raster.borrow_mut().clip = r;
        Ok(())
    }
    #[cfg(feature = "imgui")]
    fn imgui(&self) -> GameResult<&mut imgui::Context> {
        unsafe { Ok(&mut *self.imgui.as_ptr()) }
    }
    #[cfg(feature = "imgui")]
    fn imgui_texture_id(&self, _: &Box<dyn BackendTexture>) -> GameResult<imgui::TextureId> {
        Ok(imgui::TextureId::from(0))
    }
    #[cfg(feature = "imgui")]
    fn prepare_imgui(&mut self, _: &imgui::Ui) -> GameResult {
        Ok(())
    }
    #[cfg(feature = "imgui")]
    fn render_imgui(&mut self, _: &imgui::DrawData) -> GameResult {
        Ok(())
    }
    fn draw_triangle_list(
        &mut self,
        _: &[VertexData],
        _: Option<&Box<dyn BackendTexture>>,
        _: BackendShader,
    ) -> GameResult {
        Ok(())
    }
    fn as_any(&self) -> &dyn Any {
        self
    }
}
