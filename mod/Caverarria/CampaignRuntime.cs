using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace Caverarria;

internal static class CampaignRuntime
{
    public const float Scale = 3f;
    public const int OriginTileX = 200, OriginTileY = 200;
    public static Vector2 Origin => new(OriginTileX * 16 + 24, OriginTileY * 16 + 24);
    public static bool Active => Engine != null && CampaignBootstrap.IsCampaignWorld;
    public static ICampaignEngine? Engine { get; private set; }
    public static JsonElement Snapshot { get; private set; }
    public static JsonElement CurrentMap { get; private set; }
    public static long Frame { get; private set; }
    public static string? Failure { get; private set; }
    public static bool ControlsEnabled => !Active || Snapshot.Boolean("control_enabled", true) && Snapshot.Text("script_mode", "Map") == "Map";
    public static bool IronheadMovement => Active && Snapshot.Field("player").Boolean("ironhead");
    private static byte[]? pixels;
    private static Texture2D? background, overlay, interfaceImage;
    private static bool SeparateInterface => Snapshot.Integer("render_layers", 2) >= 3;
    private static bool imageDirty;
    private static float musicVolume = float.NaN, soundVolume = float.NaN;
    private static CampaignAudio? audio;
    public static bool HostAudioPlaying => audio?.Playing == true;
    public static long HostAudioFrames => audio?.SubmittedFrames ?? 0;
    public static int HostAudioBuffers => audio?.PendingBuffers ?? 0;
    private static int projectedWidth, projectedHeight;
    private static int previousEpoch;
    private static bool nativeDeathPending;
    private static readonly Queue<(int id, int epoch, ulong generation, bool boss, int damage)> hits = new();
    private static readonly Dictionary<(int id, bool boss), int> proxySlots = new();
    private static int rawControls;
    private static bool nextWeaponHeld, previousWeaponHeld;
    private static bool outsideMapEnabled;
    private static int shotFrames;
    private static readonly Queue<object> terrainHits = new();
    private static readonly HashSet<int> weaponIds = new();

    public static void Start()
    {
        if (Engine != null || !CampaignBootstrap.IsCampaignWorld || Main.dedServ) return;
        if (!CampaignDataInstaller.EnsureReady()) return;
        try
        {
            string data = Environment.GetEnvironmentVariable("CAVERARRIA_DATA") ?? Path.Combine(CampaignBootstrap.AssetsPath, "data");
            string save = CampaignBootstrap.SavePath;
            bool load = CampaignBootstrap.WantsLoad;
            int width = Math.Max(160, Main.screenWidth / 3), height = Math.Max(120, Main.screenHeight / 3);
            string? wasmPath = Environment.GetEnvironmentVariable("CAVERARRIA_WASM");
            byte[] module = wasmPath != null ? File.ReadAllBytes(wasmPath)
                : ModContent.GetInstance<CaverarriaMod>().GetFileBytes("Assets/Engine/caverarria_bridge.wasm");
            Engine = new WasmEngine(module, data, save, width, height);
            outsideMapEnabled = Main.mapEnabled;
            Main.mapEnabled = false;
            Main.mapFullscreen = false;
            pixels = new byte[width * height * 4];
            Snapshot = Engine.Send(new { op = Environment.GetEnvironmentVariable("CAVERARRIA_LOAD") == "0" ? "new" : load ? "load" : "snapshot" });
            if (Environment.GetEnvironmentVariable("CAVERARRIA_AUDIO") == "0")
                Snapshot = Engine.Send(new { op = "audio", enabled = false });
            var nativePlayer = Snapshot.Field("player");
            Main.LocalPlayer.GetModPlayer<CampaignPlayer>().BeginCampaign(nativePlayer.Integer("life", 3), nativePlayer.Integer("max_life", 3), load);
            ApplySnapshot(true);
            SyncAudio();
            if (Engine.HasPcmAudio && Snapshot.Boolean("audio_ready") && Environment.GetEnvironmentVariable("CAVERARRIA_AUDIO") != "0")
                audio = new CampaignAudio(Engine.AudioSampleRate, Engine.ReadAudio);
            imageDirty = true;
            Failure = null;
        }
        catch (Exception exception)
        {
            try { Dispose(); }
            catch (Exception cleanup) { ModContent.GetInstance<CaverarriaMod>().Logger.Warn("Campaign startup cleanup failed", cleanup); }
            CampaignDataInstaller.ReportStartupError(exception);
            RecordFailure(exception);
        }
    }

    public static void Tick()
    {
        if (!Active) return;
        Frame++;
        Automation.ReadInput();
        // Use doukutsu-rs' built-in 60 Hz mode alongside Terraria's update loop.
        try
        {
            SyncViewport();
            SyncAudio();
            while (terrainHits.TryDequeue(out var terrainHit)) { Snapshot = Engine!.Send(terrainHit); ApplySnapshot(false); }
            while (hits.TryDequeue(out var hit))
                Snapshot = Engine!.Send(new { op = "hit", id = hit.id, epoch = hit.epoch, generation = hit.generation, boss = hit.boss, damage = hit.damage });
            Player player = Main.LocalPlayer;
            Vector2 p = ToCave(player.Center), velocity = player.velocity / Scale;
            int life = Math.Max(0, (player.statLife + 9) / 10), maxLife = Math.Max(1, player.statLifeMax2 / 10);
            int controls = rawControls;
            var gun = player.HeldItem.ModItem as CaveGun;
            int nativeWeapon = Snapshot.Text("script_mode", "Map") == "Map" ? gun?.NativeType ?? 0 : 0;
            if (gun != null && (gun.ContinuousFire ? player.controlUseItem : shotFrames > 0)) controls |= 128;
            if (shotFrames > 0) shotFrames--;
            bool interact = Automation.Holding && Automation.Input.Interact || CaverarriaMod.Interact.Current && ControlsEnabled;
            bool confirm = Automation.Holding && Automation.Input.Confirm || CaverarriaMod.Interact.Current && !ControlsEnabled;
            if (interact) controls |= 8;
            if (confirm) controls |= 64 | 2048 | 4096;
            if (Automation.Holding && Automation.Input.Inventory || CaverarriaMod.Inventory.Current) controls |= 32;
            if (Automation.Holding && Automation.Input.Map || CaverarriaMod.Map.Current) controls |= 16;
            // Gameplay selection follows the real held item; native cycling here
            // would advance again after the bridge selected that same weapon.
            if (!ControlsEnabled)
            {
                if (Automation.Holding && Automation.Input.NextWeapon || CaverarriaMod.NextWeapon.Current) controls |= 256;
                if (Automation.Holding && Automation.Input.PreviousWeapon || CaverarriaMod.PreviousWeapon.Current) controls |= 512;
            }
            if (player.dead && Snapshot.Field("player").Boolean("alive", true))
                Snapshot = Engine!.Send(new { op = "death" });
            ObserveNativeDeath();
            long tickStarted = FramePerformance.Begin();
            Snapshot = Engine!.Send(new
            {
                op = "tick", controls, weapon = nativeWeapon, external = true,
                player = new { x = p.X, y = p.Y, vx = velocity.X, vy = velocity.Y, width = player.width / Scale, height = player.height / Scale, direction = player.direction, life, max_life = maxLife, grounded = player.velocity.Y == 0, jump_started = player.justJumped, wet = player.wet }
            });
            FramePerformance.End("engine", tickStarted);
            var nativePlayer = Snapshot.Field("player");
            int newMax = nativePlayer.Integer("max_life", maxLife);
            if (newMax != maxLife) player.statLifeMax2 = newMax * 10;
            int rawDamage = nativePlayer.Integer("pending_damage_raw");
            if (rawDamage > 0 && !player.dead)
                player.Hurt(PlayerDeathReason.ByCustomReason(Terraria.Localization.NetworkText.FromLiteral(player.name + " fell on the island.")), rawDamage * 10, -player.direction);
            int healing = nativePlayer.Integer("life_delta");
            if (healing > 0) player.statLife = Math.Min(newMax * 10, player.statLife + healing * 10);
            ApplySnapshot(false);
            imageDirty = true;
            if (CaverarriaMod.Retry.JustPressed) Retry();
            Automation.WriteState();
        }
        catch (Exception exception) { RecordFailure(exception); }
    }

    private static void ObserveNativeDeath()
    {
        // Drowning and the Core rescue hide an alive player. Only an actual native
        // death must wait for a checkpoint reload, independent of Terraria's respawn.
        if (Snapshot.Text("scene") == "game" && Snapshot.Field("player") is { ValueKind: JsonValueKind.Object } p
            && !p.Boolean("alive", true))
            nativeDeathPending = true;
    }

    private static void ApplySnapshot(bool initial, bool checkpointReload = false)
    {
        if (!Active) return;
        var map = Snapshot.Field("map");
        if (map.ValueKind == JsonValueKind.Object) ProjectMap(map);
        int epoch = Snapshot.Integer("epoch");
        var p = Snapshot.Field("player");
        ObserveNativeDeath();
        if (Snapshot.Text("scene") == "game" && p.Boolean("alive")
            && (checkpointReload || nativeDeathPending || epoch != previousEpoch && Main.LocalPlayer.dead))
        {
            Main.LocalPlayer.dead = false; Main.LocalPlayer.ghost = false; Main.LocalPlayer.respawnTimer = 0;
            Main.LocalPlayer.statLifeMax2 = Math.Max(1, p.Integer("max_life", 3) * 10);
            Main.LocalPlayer.statLife = Math.Clamp(p.Integer("life", 3) * 10, 1, Main.LocalPlayer.statLifeMax2);
            nativeDeathPending = false;
        }
        if (initial || epoch != previousEpoch || p.Boolean("force_position") || !ControlsEnabled || p.Boolean("ironhead"))
        {
            Main.LocalPlayer.Center = ToWorld(p.Number("x"), p.Number("y"));
            Main.LocalPlayer.fallStart = (int)(Main.LocalPlayer.position.Y / 16);
            Main.LocalPlayer.fallStart2 = Main.LocalPlayer.fallStart;
        }
        if (initial || p.Boolean("force_velocity") || !ControlsEnabled || p.Boolean("ironhead"))
            Main.LocalPlayer.velocity = new Vector2(p.Number("vx"), p.Number("vy")) * Scale;
        previousEpoch = epoch;
        SynchronizeEntities();
        SynchronizeWeapons();
    }

    public static void PlacePlayerAtCampaignPosition()
    {
        if (Active) ApplySnapshot(true);
    }

    private static void SynchronizeWeapons()
    {
        var nativeWeapons = Snapshot.Field("weapons").Elements().ToArray();
        var present = nativeWeapons.Select(weapon => weapon.Integer("id")).ToHashSet();
        foreach (var item in Main.LocalPlayer.inventory)
            if (item.ModItem is CaveGun gun && !present.Contains(gun.NativeType)) item.TurnToAir();
        weaponIds.RemoveWhere(id => !present.Contains(id));
        foreach (var weapon in nativeWeapons)
        {
            int id = weapon.Integer("id");
            if (!weaponIds.Add(id)) continue;
            int type = CaveGun.ItemFor(id);
            if (type > 0 && !Main.LocalPlayer.HasItem(type))
            {
                var item = Main.LocalPlayer.QuickSpawnItemDirect(Main.LocalPlayer.GetSource_Misc("CaveStoryWeapon"), type);
            }
        }
    }

    private static void ProjectMap(JsonElement map)
    {
        CurrentMap = map.Clone();
        var stage = Snapshot.Field("stage");
        int width = stage.Integer("width"), height = stage.Integer("height");
        int[] tiles = map.Field("tiles").Elements().Select(x => x.GetInt32()).ToArray();
        int[] attributes = map.Field("attributes").Elements().Select(x => x.GetInt32()).ToArray();
        if (width <= 0 || height <= 0 || tiles.Length != width * height || attributes.Length < 256) return;
        for (int y = 0; y < Math.Max(height, projectedHeight) * 3 + 3; y++)
            for (int x = 0; x < Math.Max(width, projectedWidth) * 3 + 3; x++)
                Main.tile[OriginTileX + x, OriginTileY + y].ClearEverything();
        ushort tileType = (ushort)ModContent.TileType<CampaignSolid>();
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int attribute = attributes[tiles[y * width + x]];
            bool water = attribute is 0x02 or 0x60 or 0x61 or 0x62 || attribute >= 0x70 && attribute <= 0x77;
            for (int sy = 0; sy < 3; sy++) for (int sx = 0; sx < 3; sx++)
            {
                var tile = Main.tile[OriginTileX + x * 3 + sx, OriginTileY + y * 3 + sy];
                if (water) { tile.LiquidType = LiquidID.Water; tile.LiquidAmount = 255; }
                bool solid = attribute is 0x05 or 0x41 or 0x43 or 0x44 or 0x46 or 0x61;
                // Project the eight half-height Cave Story slopes as protected stair collision.
                int slope = attribute >= 0x70 && attribute <= 0x77 ? attribute - 0x20 : attribute;
                if (slope >= 0x50 && slope <= 0x57)
                {
                    float cx = (sx + .5f) / 3f, cy = (sy + .5f) / 3f;
                    float boundary = slope switch { 0x50 => 1 - cx / 2, 0x51 => .5f - cx / 2, 0x52 => cx / 2, 0x53 => .5f + cx / 2, 0x54 => cx / 2, 0x55 => .5f + cx / 2, 0x56 => 1 - cx / 2, _ => .5f - cx / 2 };
                    solid = slope < 0x54 ? cy < boundary : cy > boundary;
                }
                if (solid) { tile.HasTile = true; tile.TileType = tileType; }
            }
        }
        projectedWidth = width; projectedHeight = height;
    }

    private static void SynchronizeEntities()
    {
        var present = new HashSet<(int, bool)>();
        foreach (var entity in Snapshot.Field("npcs").Elements().Concat(Snapshot.Field("bosses").Elements()))
        {
            if (!entity.Boolean("shootable") || entity.Integer("life") <= 0) continue;
            var key = (entity.Integer("id"), entity.Boolean("boss"));
            present.Add(key);
            if (!proxySlots.TryGetValue(key, out int index) || !Main.npc[index].active || Main.npc[index].ModNPC is not CaveEntity)
            {
                index = NPC.NewNPC(new EntitySource_Misc("CaveStoryEntity"), 0, 0, ModContent.NPCType<CaveEntity>());
                if (index >= Main.maxNPCs) continue;
                proxySlots[key] = index;
            }
            var npc = Main.npc[index];
            var proxy = (CaveEntity)npc.ModNPC;
            proxy.Generation = entity.Field("generation").ValueKind == JsonValueKind.Number ? entity.Field("generation").GetUInt64() : 0;
            proxy.NativeId = key.Item1; proxy.NativeBoss = key.Item2; proxy.Epoch = Snapshot.Integer("epoch");
            npc.width = Math.Max(4, (int)((entity.Number("left") + entity.Number("right")) * Scale));
            npc.height = Math.Max(4, (int)((entity.Number("top") + entity.Number("bottom")) * Scale));
            npc.position = ToWorld(entity.Number("x") - entity.Number("left"), entity.Number("y") - entity.Number("top"));
            npc.life = Math.Max(1, entity.Integer("life") * 10);
            npc.lifeMax = Math.Max(npc.lifeMax, npc.life);
            npc.velocity = Vector2.Zero;
            npc.dontTakeDamage = false;
        }
        foreach (var key in proxySlots.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            Main.npc[proxySlots[key]].active = false;
            proxySlots.Remove(key);
        }
    }

    public static void CaptureControls(Player player)
    {
        bool next = Automation.Holding && Automation.Input.NextWeapon || CaverarriaMod.NextWeapon.Current;
        bool previous = Automation.Holding && Automation.Input.PreviousWeapon || CaverarriaMod.PreviousWeapon.Current;
        if (ControlsEnabled && next && !nextWeaponHeld) CycleWeapon(player, 1);
        else if (ControlsEnabled && previous && !previousWeaponHeld) CycleWeapon(player, -1);
        nextWeaponHeld = next; previousWeaponHeld = previous;
        rawControls = (player.controlLeft ? 1 : 0) | (player.controlRight ? 2 : 0) | (player.controlUp ? 4 : 0) | (player.controlDown ? 8 : 0) | (player.controlJump ? 64 : 0);
        if (player.HeldItem.ModItem is CaveGun && player.controlUseItem)
        {
            Vector2 aim = Main.MouseWorld - player.Center;
            if (Math.Abs(aim.Y) > Math.Abs(aim.X)) rawControls |= aim.Y < 0 ? 4 : 8;
        }
    }
    private static void CycleWeapon(Player player, int direction)
    {
        int[] slots = Enumerable.Range(0, Math.Min(58, player.inventory.Length)).Where(index => player.inventory[index].ModItem is CaveGun).ToArray();
        if (slots.Length == 0) return;
        int current = Array.IndexOf(slots, player.selectedItem);
        int target = current < 0 ? direction > 0 ? 0 : slots.Length - 1 : (current + direction + slots.Length) % slots.Length;
        int slot = slots[target];
        if (slot >= 10)
        {
            int hotbar = player.selectedItem < 10 ? player.selectedItem : 0;
            (player.inventory[hotbar], player.inventory[slot]) = (player.inventory[slot], player.inventory[hotbar]);
            slot = hotbar;
        }
        player.selectedItem = slot;
    }
    public static void Shot() => shotFrames = 1;
    public static void QueueTerrainHit(Projectile projectile, Vector2 oldVelocity)
    {
        if (!Active || !projectile.friendly || projectile.owner != Main.myPlayer) return;
        var point = ToCave(projectile.Center + oldVelocity / 2);
        terrainHits.Enqueue(new { op = "tile_hit", x = point.X, y = point.Y, width = projectile.width / Scale + 2, height = projectile.height / Scale + 2 });
    }
    public static void QueueHit(CaveEntity entity, int damage) => hits.Enqueue((entity.NativeId, entity.Epoch, entity.Generation, entity.NativeBoss, Math.Max(1, (damage + 5) / 10)));
    public static Vector2 ToWorld(float x, float y) => Origin + new Vector2(x, y) * Scale;
    public static Vector2 ToCave(Vector2 world) => (world - Origin) / Scale;
    private static void SyncViewport()
    {
        if (!Active) return;
        int width = Math.Max(160, Main.screenWidth / 3), height = Math.Max(120, Main.screenHeight / 3);
        if (Engine!.Width == width && Engine.Height == height) return;
        Snapshot = Engine.Resize(width, height);
        pixels = new byte[Engine.Width * Engine.Height * 4];
        ReleaseImages();
        ApplySnapshot(false); imageDirty = true;
    }
    private static void SyncAudio()
    {
        if (!Active || musicVolume == Main.musicVolume && soundVolume == Main.soundVolume) return;
        musicVolume = Main.musicVolume; soundVolume = Main.soundVolume;
        Snapshot = Engine!.Send(new { op = "audio", music_volume = musicVolume, sfx_volume = soundVolume });
        ApplySnapshot(false);
    }
    public static void UpdateAudio()
    {
        if (Main.gameMenu || !Active || audio == null) return;
        long started = FramePerformance.Begin();
        try { SyncAudio(); audio.Update(); FramePerformance.End("audio", started); }
        catch (Exception exception)
        {
            audio.Dispose(); audio = null;
            RecordFailure(exception);
        }
    }
    public static void Save()
    {
        if (!Active) return;
        if (Snapshot.Text("scene") == "game" && Snapshot.Field("player").Boolean("alive") && !Main.LocalPlayer.dead)
            Snapshot = Engine!.Send(new { op = "save" });
        Player.SavePlayer(Main.ActivePlayerFileData, true);
        WriteStatus();
    }

    public static string Status()
    {
        if (!Active) return "The campaign is not running.";
        var stage = Snapshot.Field("stage");
        string weapons = string.Join(", ", Snapshot.Field("weapons").Elements().Select(weapon => weapon.Integer("id")));
        WriteStatus();
        return $"{stage.Text("name")} (stage {stage.Integer("id")}), {Snapshot.Text("script_mode")}: {Snapshot.Text("script")}; HP {Main.LocalPlayer.statLife}/{Main.LocalPlayer.statLifeMax2}; weapons [{weapons}].";
    }

    private static void WriteStatus()
    {
        string? save = CampaignBootstrap.SavePath;
        if (!Active || string.IsNullOrWhiteSpace(save)) return;
        Directory.CreateDirectory(save);
        string path = Path.Combine(save, "host-status.json");
        var player = Main.LocalPlayer;
        var position = ToCave(player.Center);
        var report = new
        {
            native = Snapshot, frame = Frame,
            host = new
            {
                x = position.X, y = position.Y, life = player.statLife, maxLife = player.statLifeMax2,
                selectedItem = player.selectedItem,
                inventory = player.inventory.Select((item, slot) => new { slot, item.type, item.stack, item.prefix }).Where(item => item.type != 0).ToArray()
            }
        };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report));
        File.Move(path + ".tmp", path, true);
    }
    public static void Retry()
    {
        if (!Active) return;
        Snapshot = Engine!.Send(new { op = "retry" });
        ApplySnapshot(true, checkpointReload: true); imageDirty = true;
    }
    public static void TestCommand(string command)
    {
        if (!Active) return;
        Snapshot = Engine!.Send(command); ApplySnapshot(true); imageDirty = true;
    }

    public static void UploadImages()
    {
        if (!Active || !imageDirty || pixels == null) return;
        long started = FramePerformance.Begin();
        background ??= new Texture2D(Main.instance.GraphicsDevice, Engine!.Width, Engine.Height);
        overlay ??= new Texture2D(Main.instance.GraphicsDevice, Engine!.Width, Engine.Height);
        Engine!.CopyPixels(0, pixels); background.SetData(pixels);
        Engine.CopyPixels(1, pixels); overlay.SetData(pixels);
        if (SeparateInterface)
        {
            interfaceImage ??= new Texture2D(Main.instance.GraphicsDevice, Engine.Width, Engine.Height);
            Engine.CopyPixels(2, pixels); interfaceImage.SetData(pixels);
        }
        imageDirty = false;
        FramePerformance.End("images", started);
    }

    public static void DrawWorld()
    {
        if (!Active) return;
        UploadImages();
        if (background == null) return;
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        Main.spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black);
        Main.spriteBatch.Draw(background, new Rectangle(0, 0, Engine!.Width * 3, Engine.Height * 3), Color.White);
        Main.spriteBatch.End();
    }
    public static bool DrawForeground()
    {
        if (Active && SeparateInterface) DrawInterfaceImage(overlay);
        return true;
    }
    public static bool DrawOverlay()
    {
        if (Active) DrawInterfaceImage(SeparateInterface ? interfaceImage : overlay);
        return true;
    }
    private static void DrawInterfaceImage(Texture2D? image)
    {
        if (image == null) return;
        Main.spriteBatch.End();
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        Main.spriteBatch.Draw(image, new Rectangle(0, 0, Engine!.Width * 3, Engine.Height * 3), Color.White);
        Main.spriteBatch.End();
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, null, Main.UIScaleMatrix);
    }

    internal static void RecordFailure(Exception exception)
    {
        Failure = exception.ToString();
        ModContent.GetInstance<CaverarriaMod>().Logger.Error("Cave Story campaign failure", exception);
        Main.NewText("Caverarria: " + exception.Message, Color.OrangeRed);
        Automation.WriteState();
    }

    public static void Dispose()
    {
        audio?.Dispose(); audio = null;
        if (Engine != null)
        {
            Main.LocalPlayer.GetModPlayer<CampaignPlayer>().FinishCampaign();
            Main.mapEnabled = outsideMapEnabled;
            Main.Configuration.Put("MapEnabled", outsideMapEnabled);
        }
        try { Engine?.Dispose(); }
        finally
        {
            Engine = null;
            ReleaseImages();
            pixels = null; Snapshot = default; CurrentMap = default;
            proxySlots.Clear(); hits.Clear(); terrainHits.Clear(); weaponIds.Clear(); rawControls = shotFrames = 0; nextWeaponHeld = previousWeaponHeld = false;
            musicVolume = soundVolume = float.NaN;
            Frame = 0; projectedWidth = projectedHeight = previousEpoch = 0;
            nativeDeathPending = false;
        }
    }

    private static void ReleaseImages()
    {
        var previousBackground = background;
        var previousOverlay = overlay;
        var previousInterface = interfaceImage;
        background = overlay = interfaceImage = null;
        if (previousBackground == null && previousOverlay == null && previousInterface == null) return;
        // Save & Quit unloads on a worker thread. FNA graphics resources belong
        // to the main thread; detach them now and release those captured objects there.
        Main.QueueMainThreadAction(() =>
        {
            previousBackground?.Dispose();
            previousOverlay?.Dispose();
            previousInterface?.Dispose();
        });
    }
}
