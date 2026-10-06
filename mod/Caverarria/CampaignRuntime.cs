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
    public const float Scale = 2f;
    public const int OriginTileX = 200, OriginTileY = 200;
    public static Vector2 Origin => new(OriginTileX * 16 + 16, OriginTileY * 16 + 16);
    public static bool Active => Engine != null && CampaignBootstrap.IsCampaignWorld;
    public static ICampaignEngine? Engine { get; private set; }
    private static JsonElement snapshot, pendingMap;
    public static JsonElement Snapshot
    {
        get => snapshot;
        private set
        {
            snapshot = value;
            // Map deltas are emitted once. Audio/hit commands can replace the
            // snapshot before reconciliation, but must not discard that delta.
            var map = value.Field("map");
            if (map.ValueKind == JsonValueKind.Object) pendingMap = map.Clone();
        }
    }
    public static JsonElement CurrentMap { get; private set; }
    public static long Frame { get; private set; }
    public static string? Failure { get; private set; }
    public static bool ControlsEnabled => !Active || Snapshot.Boolean("control_enabled", true) && Snapshot.Text("script_mode", "Map") == "Map";
    public static bool IronheadMovement => Active && Snapshot.Field("player").Boolean("ironhead");
    private static byte[]? pixels;
    private static byte[]? interfacePixels;
    private static Texture2D? background, overlay, interfaceImage;
    private static bool SeparateInterface => Snapshot.Integer("render_layers", 2) >= 3;
    private static bool imageDirty;
    private static float musicVolume = float.NaN, soundVolume = float.NaN;
    private static TerrainPersistence? terrainPersistence;
    private static CampaignAudio? audio;
    public static bool HostAudioPlaying => audio?.Playing == true;
    public static long HostAudioFrames => audio?.SubmittedFrames ?? 0;
    public static int HostAudioBuffers => audio?.PendingBuffers ?? 0;
    private static int projectedWidth, projectedHeight;
    private static int previousEpoch;
    private static bool pendingPlacementClearance;
    private static bool nativeDeathPending;
    private static readonly Queue<(int id, int epoch, ulong generation, bool boss, int damage)> hits = new();
    private static readonly Dictionary<(int id, bool boss), int> proxySlots = new();
    private static int rawControls;
    private static bool nextWeaponHeld, previousWeaponHeld, inventoryHeld;
    private static int pendingStoryItem;
    private static bool outsideMapEnabled;
    private static int shotFrames;
    private static readonly Queue<object> terrainHits = new();
    private static readonly HashSet<int> weaponIds = new();

    public static void Start(bool loadProfile = true)
    {
        if (Engine != null || !CampaignBootstrap.IsCampaignWorld || Main.dedServ) return;
        if (!CampaignDataInstaller.EnsureReady()) return;
        try
        {
            string data = Environment.GetEnvironmentVariable("CAVERARRIA_DATA") ?? Path.Combine(CampaignBootstrap.AssetsPath, "data");
            string save = CampaignBootstrap.SavePath;
            bool load = loadProfile && CampaignBootstrap.WantsLoad;
            int width = CampaignView.ViewportWidth, height = CampaignView.ViewportHeight;
            string? wasmPath = Environment.GetEnvironmentVariable("CAVERARRIA_WASM");
            byte[] module = wasmPath != null ? File.ReadAllBytes(wasmPath)
                : ModContent.GetInstance<CaverarriaMod>().GetFileBytes("Assets/Engine/caverarria_bridge.wasm");
            Engine = new WasmEngine(module, data, save, width, height);
            Engine.Send(new { op = "ui_resize", width = CampaignView.InterfaceViewportWidth, height = CampaignView.InterfaceViewportHeight });
            outsideMapEnabled = Main.mapEnabled;
            Main.mapEnabled = false;
            Main.mapFullscreen = false;
            pixels = new byte[width * height * 4];
            bool fresh = !loadProfile || Environment.GetEnvironmentVariable("CAVERARRIA_LOAD") == "0";
            Snapshot = Engine.Send(new { op = fresh ? "new" : load ? "load" : "snapshot" });
            if (fresh) { CampaignTerrainEdits.ClearNewGame(); CampaignFurniture.ClearNewGame(); }
            SyncTerrainPersistence();
            if (Environment.GetEnvironmentVariable("CAVERARRIA_AUDIO") == "0")
                Snapshot = Engine.Send(new { op = "audio", enabled = false });
            var nativePlayer = Snapshot.Field("player");
            Main.LocalPlayer.GetModPlayer<CampaignPlayer>().BeginCampaign(nativePlayer.Integer("life", 3), nativePlayer.Integer("max_life", 3), load);
            ApplySnapshot(true);
            SyncAudio();
            // World loading can run on a worker. The main-thread audio hook
            // owns construction and playback of the FNA stream.
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
            SyncTerrainPersistence();
            if (pendingStoryItem > 0)
            {
                int id = pendingStoryItem; pendingStoryItem = 0;
                Main.playerInventory = false;
                Snapshot = Engine!.Send(new { op = "use_item", id });
                ApplySnapshot(false);
            }
            while (terrainHits.TryDequeue(out var terrainHit)) { Snapshot = Engine!.Send(terrainHit); ApplySnapshot(false); }
            while (hits.TryDequeue(out var hit))
                Snapshot = Engine!.Send(new { op = "hit", id = hit.id, epoch = hit.epoch, generation = hit.generation, boss = hit.boss, damage = hit.damage });
            Player player = Main.LocalPlayer;
            Vector2 p = ToCave(player.Center), velocity = player.velocity / Scale;
            var (life, maxLife) = CaptureCampaignHealth(player);
            int controls = rawControls;
            var gun = player.HeldItem.ModItem as CaveGun;
            int nativeWeapon = Snapshot.Text("script_mode", "Map") == "Map" ? gun?.NativeType ?? 0 : 0;
            if (gun != null && (gun.ContinuousFire ? player.controlUseItem : shotFrames > 0)) controls |= 128;
            if (shotFrames > 0) shotFrames--;
            bool interact = Automation.Holding && Automation.Input.Interact || CaverarriaMod.Interact.Current && ControlsEnabled;
            bool confirm = Automation.Holding && Automation.Input.Confirm || CaverarriaMod.Interact.Current && !ControlsEnabled;
            if (interact) controls |= 8;
            if (confirm) controls |= 64 | 2048;
            if (CaverarriaMod.SkipCutscene.Current) controls |= 4096;
            bool inventory = Automation.Holding && Automation.Input.Inventory || CaverarriaMod.Inventory.Current;
            if (Snapshot.Text("script_mode", "Map") == "Inventory")
            {
                if (inventory || Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Escape)) controls |= 32;
            }
            else if (inventory && !inventoryHeld && ControlsEnabled) Main.playerInventory = !Main.playerInventory;
            inventoryHeld = inventory;
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
                op = "tick", controls, weapon = nativeWeapon, external = true, host_inventory_open = Main.playerInventory,
                host_ammo_in_slots = ModContent.GetInstance<CampaignViewConfig>().AmmoInInventorySlots,
                player = new { x = p.X, y = p.Y, vx = velocity.X, vy = velocity.Y, width = player.width / Scale, height = player.height / Scale, direction = player.direction, life, max_life = maxLife, grounded = player.velocity.Y == 0, jump_started = player.justJumped, wet = player.wet }
            });
            FramePerformance.End("engine", tickStarted);
            var nativePlayer = Snapshot.Field("player");
            int newMax = nativePlayer.Integer("max_life", maxLife);
            if (newMax != maxLife) player.statLifeMax2 = newMax * 10;
            int rawDamage = nativePlayer.Integer("pending_damage_raw");
            int scaledDamage = ScaleIncomingDamage(rawDamage, ModContent.GetInstance<CampaignViewConfig>().EnemyDamageScale);
            if (scaledDamage > 0 && !player.dead)
                player.Hurt(PlayerDeathReason.ByCustomReason(Terraria.Localization.NetworkText.FromLiteral(player.name + " fell on the island.")), scaledDamage, -player.direction);
            int healing = nativePlayer.Integer("life_delta");
            if (healing > 0) player.statLife = Math.Min(newMax * 10, player.statLife + healing * 10);
            ApplySnapshot(false);
            imageDirty = true;
            if (CaverarriaMod.Retry.JustPressed) Retry();
            Automation.WriteState();
        }
        catch (Exception exception) { RecordFailure(exception); }
    }

    internal static (int life, int maximum) CaptureCampaignHealth(Player player)
    {
        // Native scripts own capacity. ResetEffects and player-file saves can
        // briefly expose outside HP; feeding that back grants fake capsules.
        int maximum = Math.Max(1, Snapshot.Field("player").Integer("max_life", 3));
        player.statLifeMax2 = maximum * 10;
        player.statLife = Math.Min(player.statLife, player.statLifeMax2);
        return (Math.Clamp((player.statLife + 9) / 10, 0, maximum), maximum);
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
        ProjectPendingMap();
        int epoch = Snapshot.Integer("epoch");
        var p = Snapshot.Field("player");
        if (Snapshot.Text("scene") == "game" && p.ValueKind == JsonValueKind.Object)
        {
            int upgrade = Main.LocalPlayer.GetModPlayer<LifeCapsulePlayer>()
                .ObserveMaximum(CampaignBootstrap.HealthKey, p.Integer("max_life", 3));
            if (upgrade > 0) Main.LocalPlayer.GetModPlayer<CampaignPlayer>().AddOutsideHealth(upgrade);
        }
        ObserveNativeDeath();
        if (Snapshot.Text("scene") == "game" && p.Boolean("alive")
            && (checkpointReload || nativeDeathPending || epoch != previousEpoch && Main.LocalPlayer.dead))
        {
            Main.LocalPlayer.dead = false; Main.LocalPlayer.ghost = false; Main.LocalPlayer.respawnTimer = 0;
            // UpdateDead separates and rotates the actual player draw parts.
            // Native retry bypasses Terraria's Spawn, which normally resets them.
            // Restore the pose here without invoking host spawn/healing/teleports.
            Main.LocalPlayer.headPosition = Main.LocalPlayer.bodyPosition = Main.LocalPlayer.legPosition = Vector2.Zero;
            Main.LocalPlayer.headVelocity = Main.LocalPlayer.bodyVelocity = Main.LocalPlayer.legVelocity = Vector2.Zero;
            Main.LocalPlayer.headRotation = Main.LocalPlayer.bodyRotation = Main.LocalPlayer.legRotation = 0f;
            Main.LocalPlayer.immuneAlpha = 0;
            Main.LocalPlayer.statLifeMax2 = Math.Max(1, p.Integer("max_life", 3) * 10);
            Main.LocalPlayer.statLife = Math.Clamp(p.Integer("life", 3) * 10, 1, Main.LocalPlayer.statLifeMax2);
            nativeDeathPending = false;
        }
        if (initial || epoch != previousEpoch || p.Boolean("force_position") || !ControlsEnabled || p.Boolean("ironhead"))
        {
            Main.LocalPlayer.Center = ToWorld(p.Number("x"), p.Number("y"));
            Main.LocalPlayer.fallStart = (int)(Main.LocalPlayer.position.Y / 16);
            Main.LocalPlayer.fallStart2 = Main.LocalPlayer.fallStart;
            pendingPlacementClearance = true;
        }
        if (pendingPlacementClearance && ControlsEnabled && !p.Boolean("ironhead")
            && Snapshot.Text("scene") == "game" && p.Boolean("alive", true))
        {
            Player player = Main.LocalPlayer;
            player.position = CampaignBody.ClearPlacement(player.position, player.width, player.height,
                (position, width, height) => Collision.SolidCollision(position, width, height));
            player.fallStart = player.fallStart2 = (int)(player.position.Y / 16);
            pendingPlacementClearance = false;
        }
        if (initial || p.Boolean("force_velocity") || !ControlsEnabled || p.Boolean("ironhead"))
            Main.LocalPlayer.velocity = new Vector2(p.Number("vx"), p.Number("vy")) * Scale;
        previousEpoch = epoch;
        SynchronizeEntities();
        SynchronizeWeapons();
        SynchronizeInventory();
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

    public static void UseStoryItem(int id)
    {
        if (Active && ControlsEnabled && !Main.LocalPlayer.dead) pendingStoryItem = id;
    }

    private static void SynchronizeInventory()
    {
        var desired = Snapshot.Field("items").Elements()
            .Where(item => item.Integer("id") > 0 && item.Integer("id") < 0x8000)
            .ToDictionary(item => item.Integer("id"), item => item.Integer("amount"));
        // Reconcile only representations issued by this campaign. Other campaigns'
        // belongings remain intact; the original runtime owns progression and use.
        var slots = StoryItemSlots().ToList();
        foreach (var item in slots)
        {
            if (item.ModItem is not CampaignItem story || story.CampaignId != CampaignBootstrap.CampaignId) continue;
            int remaining = desired.GetValueOrDefault(story.NativeType);
            if (remaining <= 0) item.TurnToAir();
            else
            {
                item.stack = Math.Min(remaining, item.maxStack);
                desired[story.NativeType] = remaining - item.stack;
            }
        }
        foreach (var pair in desired)
        {
            int remaining = pair.Value, type = CampaignItem.ItemFor(pair.Key);
            if (type == 0) continue;
            while (remaining > 0)
            {
                // Leave the authoritative item in the campaign when inventory is
                // full instead of spawning repeated world drops every update.
                var empty = Main.LocalPlayer.inventory.Take(50).FirstOrDefault(item => item.IsAir);
                if (empty == null) break;
                empty.SetDefaults(type);
                ((CampaignItem)empty.ModItem).CampaignId = CampaignBootstrap.CampaignId;
                empty.stack = Math.Min(remaining, empty.maxStack);
                remaining -= empty.stack;
            }
        }
    }

    private static IEnumerable<Item> StoryItemSlots()
    {
        var player = Main.LocalPlayer;
        foreach (var item in player.inventory.Take(58)) yield return item;
        yield return Main.mouseItem;
        yield return player.trashItem;
        foreach (var bank in new[] { player.bank, player.bank2, player.bank3, player.bank4 })
            foreach (var item in bank.item) yield return item;
        foreach (var chest in Main.chest)
            if (chest != null)
                foreach (var item in chest.item) yield return item;
        foreach (var item in Main.item)
            if (item.active) yield return item;
    }

    private static Point SavedProjectionFootprint(int width, int height)
    {
        // A saved host world can contain projected tiles from a larger room.
        // Clear the current 2x2 campaign footprint on first entry.
        string data = Environment.GetEnvironmentVariable("CAVERARRIA_DATA") ?? Path.Combine(CampaignBootstrap.AssetsPath, "data");
        string stageDirectory = Path.Combine(data, "Stage");
        if (Directory.Exists(stageDirectory))
            foreach (string path in Directory.EnumerateFiles(stageDirectory).Where(path => Path.GetExtension(path).Equals(".pxm", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new BinaryReader(File.OpenRead(path));
                if (reader.BaseStream.Length < 8 || reader.ReadByte() != 'P' || reader.ReadByte() != 'X' || reader.ReadByte() != 'M') continue;
                reader.ReadByte();
                width = Math.Max(width, reader.ReadUInt16()); height = Math.Max(height, reader.ReadUInt16());
            }
        return new Point(Math.Min(Main.maxTilesX - OriginTileX, width * 2 + 2),
            Math.Min(Main.maxTilesY - OriginTileY, height * 2 + 2));
    }

    private static void ProjectMap(JsonElement map)
    {
        CurrentMap = map.Clone();
        var stage = Snapshot.Field("stage");
        int width = stage.Integer("width"), height = stage.Integer("height");
        int[] tiles = map.Field("tiles").Elements().Select(x => x.GetInt32()).ToArray();
        int[] attributes = map.Field("attributes").Elements().Select(x => x.GetInt32()).ToArray();
        int[] cellAttributes = map.Field("cell_attributes").Elements().Select(x => x.GetInt32()).ToArray();
        if (width <= 0 || height <= 0 || tiles.Length != width * height || attributes.Length < 256) return;
        Point clear = projectedWidth == 0 && projectedHeight == 0 ? SavedProjectionFootprint(width, height)
            : new Point(Math.Min(Main.maxTilesX - OriginTileX, Math.Max(width, projectedWidth) * 2 + 2),
                Math.Min(Main.maxTilesY - OriginTileY, Math.Max(height, projectedHeight) * 2 + 2));
        for (int y = 0; y < clear.Y; y++)
            for (int x = 0; x < clear.X; x++)
                Main.tile[OriginTileX + x, OriginTileY + y].ClearEverything();
        var editMasks = map.Field("terrain_edits").Elements().ToDictionary(edit => (edit.Integer("x"), edit.Integer("y")),
            edit => edit.GetProperty("mask").GetInt32());
        ushort tileType = (ushort)ModContent.TileType<CampaignSolid>();
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int attribute = cellAttributes.Length == tiles.Length ? cellAttributes[y * width + x] : attributes[tiles[y * width + x]];
            bool water = attribute is 0x02 or 0x60 or 0x61 or 0x62 || attribute >= 0x70 && attribute <= 0x77;
            for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++)
            {
                var tile = Main.tile[OriginTileX + x * 2 + sx, OriginTileY + y * 2 + sy];
                if (water) { tile.LiquidType = LiquidID.Water; tile.LiquidAmount = 255; }
                bool solid = attribute is 0x05 or 0x41 or 0x43 or 0x44 or 0x46 or 0x61;
                // Project the eight half-height Cave Story slopes as protected stair collision.
                int slope = attribute >= 0x70 && attribute <= 0x77 ? attribute - 0x20 : attribute;
                if (slope >= 0x50 && slope <= 0x57)
                {
                    float cx = (sx + .5f) / 2f, cy = (sy + .5f) / 2f;
                    float boundary = slope switch { 0x50 => 1 - cx / 2, 0x51 => .5f - cx / 2, 0x52 => cx / 2, 0x53 => .5f + cx / 2, 0x54 => cx / 2, 0x55 => .5f + cx / 2, 0x56 => 1 - cx / 2, _ => .5f - cx / 2 };
                    solid = slope < 0x54 ? cy < boundary : cy > boundary;
                }
                if (editMasks.TryGetValue((x, y), out int mask)) solid = CampaignTerrainEdits.MaskHasTile(mask, sx, sy);
                if (solid) { tile.HasTile = true; tile.TileType = tileType; }
            }
        }
        projectedWidth = width; projectedHeight = height;
        CampaignTerrainEdits.ProjectPlacedTiles(stage.Integer("id"));
        CampaignFurniture.Project(stage.Integer("id"));
    }

    private static void ProjectPendingMap()
    {
        if (pendingMap.ValueKind != JsonValueKind.Object) return;
        ProjectMap(pendingMap);
        pendingMap = default;
    }

    public static bool EditTerrain(int x, int y, bool solid, int subX = -1, int subY = -1)
    {
        if (!Active || !ControlsEnabled || Main.LocalPlayer.dead || Snapshot.Text("scene") != "game") return false;
        Snapshot = Engine!.Send(new { op = "terrain_edit", epoch = Snapshot.Integer("epoch"), stage = Snapshot.Field("stage").Integer("id"), x, y, solid, sub_x = subX >= 0 ? (int?)subX : null, sub_y = subY >= 0 ? (int?)subY : null });
        bool accepted = Snapshot.Boolean("terrain_edit_accepted");
        // A synchronous mining hook must immediately update the real collision.
        ProjectPendingMap();
        imageDirty |= accepted;
        return accepted;
    }

    private static void SynchronizeEntities()
    {
        int epoch = Snapshot.Integer("epoch");
        var actors = Snapshot.Field("npcs").Elements().Concat(Snapshot.Field("bosses").Elements())
            .Where(CaveEntity.IsCombatTarget).ToArray();
        var identities = actors.ToDictionary(entity => (entity.Integer("id"), entity.Boolean("boss")),
            entity => entity.Field("generation").ValueKind == JsonValueKind.Number ? entity.Field("generation").GetUInt64() : 0);
        // Retire old identities before any NewNPC call can reuse their slots.
        // Never deactivate a slot now owned by another actor or another mod.
        foreach (var pair in proxySlots.ToArray())
        {
            var npc = Main.npc[pair.Value];
            if (npc.ModNPC is CaveEntity proxy && proxy.NativeId == pair.Key.id && proxy.NativeBoss == pair.Key.boss)
            {
                if (npc.active && identities.TryGetValue(pair.Key, out ulong generation)
                    && proxy.Owns(pair.Key.id, pair.Key.boss, epoch, generation)) continue;
                npc.active = false;
            }
            proxySlots.Remove(pair.Key);
        }
        foreach (var entity in actors)
        {
            var key = (id: entity.Integer("id"), boss: entity.Boolean("boss"));
            ulong generation = identities[key];
            Rectangle bounds = CaveEntity.CombatBounds(entity);
            if (!proxySlots.TryGetValue(key, out int index))
            {
                index = NPC.NewNPC(new EntitySource_Misc("CaveStoryEntity"), bounds.Center.X, bounds.Bottom, ModContent.NPCType<CaveEntity>());
                if (index >= Main.maxNPCs) continue;
                proxySlots[key] = index;
            }
            var npc = Main.npc[index];
            var proxy = (CaveEntity)npc.ModNPC;
            proxy.Generation = generation;
            proxy.NativeId = key.id; proxy.NativeBoss = key.boss; proxy.Epoch = epoch;
            npc.width = bounds.Width; npc.height = bounds.Height;
            npc.position = new Vector2(bounds.X, bounds.Y);
            npc.life = Math.Max(1, entity.Integer("life") * 10);
            npc.lifeMax = Math.Max(npc.lifeMax, npc.life);
            npc.velocity = Vector2.Zero;
            npc.dontTakeDamage = false;
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
    internal static int ScaleWeaponDamage(int damage, float multiplier)
    {
        double scale = Math.Clamp(multiplier, 0f, 10f);
        return damage <= 0 || scale <= 0 ? 0
            : (int)Math.Clamp(Math.Floor(damage * scale / 10 + .5), 1, 32767);
    }
    internal static int ScaleIncomingDamage(int damage, float multiplier)
        => (int)Math.Clamp(Math.Floor(Math.Max(0, damage) * 10d * Math.Clamp(multiplier, 0f, 10f) + .5), 0, int.MaxValue);

    public static void QueueHit(CaveEntity entity, int damage)
    {
        int nativeDamage = ScaleWeaponDamage(damage, ModContent.GetInstance<CampaignViewConfig>().TerrariaWeaponDamageScale);
        if (nativeDamage == 0) return;
        hits.Enqueue((entity.NativeId, entity.Epoch, entity.Generation, entity.NativeBoss, nativeDamage));
    }
    public static Vector2 ToWorld(float x, float y) => Origin + new Vector2(x, y) * Scale;
    public static Vector2 ToCave(Vector2 world) => (world - Origin) / Scale;
    private static void SyncViewport()
    {
        if (!Active) return;
        int width = CampaignView.ViewportWidth, height = CampaignView.ViewportHeight;
        int uiWidth = CampaignView.InterfaceViewportWidth, uiHeight = CampaignView.InterfaceViewportHeight;
        bool worldChanged = Engine!.Width != width || Engine.Height != height;
        bool interfaceChanged = Snapshot.Field("ui_viewport").Integer("width") != uiWidth
            || Snapshot.Field("ui_viewport").Integer("height") != uiHeight;
        if (!worldChanged && !interfaceChanged) return;
        if (worldChanged) Snapshot = Engine.Resize(width, height);
        // World resize has its own legacy UI defaults; always reapply the host's
        // screen-sized interface canvas so zoom cannot move HUD/fade edges.
        Snapshot = Engine.Send(new { op = "ui_resize", width = uiWidth, height = uiHeight });
        pixels = new byte[Engine.Width * Engine.Height * 4];
        ReleaseImages();
        ApplySnapshot(false); imageDirty = true;
    }
    private static void SyncTerrainPersistence()
    {
        TerrainPersistence mode = ModContent.GetInstance<CampaignViewConfig>().TerrainEdits;
        if (terrainPersistence == mode) return;
        Snapshot = Engine!.Send(new { op = "terrain_persistence", mode = (int)mode });
        CampaignTerrainEdits.SyncPersistence(mode);
        CampaignFurniture.SyncPersistence(mode);
        terrainPersistence = mode;
    }

    private static void SyncAudio()
    {
        if (!Active) return;
        var config = ModContent.GetInstance<CampaignViewConfig>();
        float music = config.MuteCampaignAudio ? 0 : Main.musicVolume * Math.Clamp(config.CampaignMusicVolume, 0f, 1f);
        float sound = config.MuteCampaignAudio ? 0 : Main.soundVolume * Math.Clamp(config.CampaignSoundVolume, 0f, 1f);
        if (musicVolume == music && soundVolume == sound) return;
        musicVolume = music; soundVolume = sound;
        Snapshot = Engine!.Send(new { op = "audio", music_volume = musicVolume, sfx_volume = soundVolume });
        ApplySnapshot(false);
    }
    public static void UpdateAudio()
    {
        if (Main.gameMenu || !Active || Environment.GetEnvironmentVariable("CAVERARRIA_AUDIO") == "0") return;
        long started = FramePerformance.Begin();
        try
        {
            SyncAudio();
            if (audio == null && Engine!.HasPcmAudio && Snapshot.Boolean("audio_ready"))
                audio = new CampaignAudio(Engine.AudioSampleRate, Engine.ReadAudio, Main.QueueMainThreadAction);
            audio?.Update();
            FramePerformance.End("audio", started);
        }
        catch (Exception exception)
        {
            audio?.Dispose(); audio = null;
            RecordFailure(exception);
        }
    }
    public static void Save()
    {
        if (!Active) return;
        if (Snapshot.Text("scene") == "game" && Snapshot.Field("player").Boolean("alive") && !Main.LocalPlayer.dead)
            Snapshot = Engine!.Send(new { op = "save" });
        Player.SavePlayer(Main.ActivePlayerFileData, true);
        if (terrainPersistence == TerrainPersistence.Session) { CampaignTerrainEdits.MarkSaved(); CampaignFurniture.MarkSaved(); }
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
        if (terrainPersistence != TerrainPersistence.Persistent) { CampaignTerrainEdits.ReloadSaved(); CampaignFurniture.ReloadSaved(); }
        Snapshot = Engine!.Send(new { op = "retry" });
        ApplySnapshot(true, checkpointReload: true); imageDirty = true;
    }
    /// <summary>Restores the authored room, discarding edits to the current stage.</summary>
    public static bool RepairRoom()
    {
        if (!Active) return false;
        Snapshot = Engine!.Send(new { op = "repair_room" });
        CampaignTerrainEdits.ForgetStage(Snapshot.Field("stage").Integer("id"));
        CampaignFurniture.ForgetStage(Snapshot.Field("stage").Integer("id"));
        ApplySnapshot(false); imageDirty = true;
        return true;
    }
    /// <summary>Re-enters the current room as if arriving through its door.</summary>
    public static bool ReloadRoom()
    {
        if (!Active) return false;
        Snapshot = Engine!.Send(new { op = "reload_room" });
        ApplySnapshot(true); imageDirty = true;
        return true;
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
            int width = Snapshot.Field("ui_viewport").Integer("width", Engine.Width);
            int height = Snapshot.Field("ui_viewport").Integer("height", Engine.Height);
            interfacePixels ??= new byte[width * height * 4];
            interfaceImage ??= new Texture2D(Main.instance.GraphicsDevice, width, height);
            Engine.CopyPixels(2, interfacePixels); interfaceImage.SetData(interfacePixels);
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
        Main.spriteBatch.Draw(background, CampaignView.OutputRectangle(Engine!.Width, Engine.Height), Color.White);
        Main.spriteBatch.End();
        var camera = Snapshot.Field("camera");
        Vector2 nativeCamera = new(camera.Number("x"), camera.Number("y"));
        CampaignFurniture.Prepare(nativeCamera);
        CampaignTerrainEdits.Prepare();
        CampaignActorPixels.Begin();
        Rectangle output = CampaignView.OutputRectangle(Engine.Width, Engine.Height);
        Matrix grid = Matrix.CreateScale(CampaignView.PixelScale, CampaignView.PixelScale, 1)
            * Matrix.CreateTranslation(output.X, output.Y, 0);
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, grid);
        try
        {
            CampaignTerrainEdits.Draw(Main.spriteBatch, nativeCamera);
            CampaignFurniture.Draw(Main.spriteBatch);
        }
        finally { Main.spriteBatch.End(); }
    }
    public static bool DrawForeground()
    {
        if (Active && SeparateInterface) DrawInterfaceImage(overlay, worldLayer: true);
        return true;
    }
    public static bool DrawOverlay()
    {
        if (Active) DrawInterfaceImage(SeparateInterface ? interfaceImage : overlay, worldLayer: !SeparateInterface);
        return true;
    }
    private static void DrawInterfaceImage(Texture2D? image, bool worldLayer)
    {
        if (image == null) return;
        Main.spriteBatch.End();
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        Rectangle output = worldLayer ? CampaignView.OutputRectangle(Engine!.Width, Engine.Height)
            : CampaignView.InterfaceRectangle(image.Width, image.Height);
        Main.spriteBatch.Draw(image, output, Color.White);
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
        // Detach the session first. Save & Quit runs on a worker; a teardown
        // failure must not leave an old engine active in the menu or next world.
        var previousEngine = Engine;
        var previousAudio = audio;
        Engine = null; audio = null;
        try
        {
            previousAudio?.Dispose();
            if (previousEngine != null)
            {
                Main.LocalPlayer.GetModPlayer<CampaignPlayer>().FinishCampaign();
                Main.mapEnabled = outsideMapEnabled;
                Main.Configuration.Put("MapEnabled", outsideMapEnabled);
            }
        }
        finally
        {
            try { previousEngine?.Dispose(); }
            finally
            {
                ReleaseImages();
                CampaignActorPixels.DisposeTargets();
                CampaignTerrainEdits.ClearSession(); CampaignFurniture.ClearSession();
                pixels = interfacePixels = null; Snapshot = default; CurrentMap = pendingMap = default;
                pendingPlacementClearance = false; terrainPersistence = null;
                foreach (var pair in proxySlots)
                {
                    var npc = Main.npc[pair.Value];
                    if (npc.ModNPC is CaveEntity proxy && proxy.NativeId == pair.Key.id && proxy.NativeBoss == pair.Key.boss)
                        npc.active = false;
                }
                proxySlots.Clear(); hits.Clear(); terrainHits.Clear(); weaponIds.Clear(); rawControls = shotFrames = pendingStoryItem = 0; nextWeaponHeld = previousWeaponHeld = inventoryHeld = false;
                musicVolume = soundVolume = float.NaN;
                Frame = 0; projectedWidth = projectedHeight = previousEpoch = 0;
                nativeDeathPending = false;
            }
        }
    }

    private static void ReleaseImages()
    {
        var previousBackground = background;
        var previousOverlay = overlay;
        var previousInterface = interfaceImage;
        background = overlay = interfaceImage = null;
        interfacePixels = null;
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
