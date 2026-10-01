using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;

namespace Caverarria;

internal sealed class AutomationInput
{
    public long Id { get; set; }
    public int Frames { get; set; }
    public bool Left { get; set; }
    public bool Right { get; set; }
    public bool Up { get; set; }
    public bool Down { get; set; }
    public bool Jump { get; set; }
    public bool Interact { get; set; }
    public bool Confirm { get; set; }
    public bool Retry { get; set; }
    public bool Inventory { get; set; }
    public bool Map { get; set; }
    public bool NextWeapon { get; set; }
    public bool PreviousWeapon { get; set; }
    public bool Shoot { get; set; }
    public bool UseTile { get; set; }
    public bool CapturePixels { get; set; }
    public float? AimX { get; set; }
    public float? AimY { get; set; }
    public int? Item { get; set; }
    public int? FixtureItem { get; set; }
    public JsonElement Engine { get; set; }
}

/// <summary>Test commands drive normal Terraria inputs. Native debug operations are explicitly recorded.</summary>
internal static class Automation
{
    private static readonly JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
    private static string? DirectoryPath => Environment.GetEnvironmentVariable("CAVERARRIA_TEST_DIR");
    public static AutomationInput Input { get; private set; } = new();
    private static int remaining;
    public static bool Holding => remaining > 0;
    private static long lastId;
    private static string? lastCommand;
    private static int debugCommands;
    private static long menuReportTime;
    public static void WriteMenuState()
    {
        string? directory = DirectoryPath;
        if (directory == null || !Main.gameMenu || Environment.TickCount64 - menuReportTime < 250) return;
        menuReportTime = Environment.TickCount64;
        try
        {
            Directory.CreateDirectory(directory);
            var state = new
            {
                menuMode = Main.menuMode,
                campaignActive = CampaignRuntime.Active, engineAttached = CampaignRuntime.Engine != null,
                audioPlaying = CampaignRuntime.HostAudioPlaying,
                players = Main.PlayerList.Select(data => new { name = data.Name, path = data.Path, life = data.Player.statLife, permanentMaxLife = data.Player.statLifeMax, effectiveMaxLife = data.Player.statLifeMax2, difficulty = data.Player.difficulty }).ToArray(),
                worlds = Main.WorldList.Select(data => new { name = data.Name, path = data.Path, id = data.UniqueId, campaign = data.TryGetHeaderData<CampaignSystem>(out var header) && header.GetBool("caverarriaCampaign") }).ToArray()
            };
            string path = Path.Combine(directory, "menu-state.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
    }
    public static void ReadInput()
    {
        string? directory = DirectoryPath;
        if (directory == null) return;
        try
        {
            string path = Path.Combine(directory, "input.json");
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path);
            var next = JsonSerializer.Deserialize<AutomationInput>(json, options);
            if (next == null || next.Id <= lastId) return;
            Input = next;
            lastId = next.Id;
            remaining = Math.Clamp(next.Frames, 1, 36000);
            lastCommand = json;
            if (next.FixtureItem.HasValue)
            {
                // Opt-in isolated combat fixture, recorded as a diagnostic.
                // This is never part of an ordinary-controls progression run.
                Main.LocalPlayer.inventory[0].SetDefaults(next.FixtureItem.Value);
                Main.LocalPlayer.selectedItem = 0;
                debugCommands++;
            }
            if (next.CapturePixels) { CampaignTilePixels.Capture(directory); debugCommands++; }
            if (next.Retry) CampaignRuntime.Retry();
            if (next.Engine.ValueKind == JsonValueKind.Object)
            {
                CampaignRuntime.TestCommand(next.Engine.GetRawText());
                debugCommands++;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
    }
    public static void Apply(Player player)
    {
        if (DirectoryPath == null || remaining <= 0) return;
        bool hold = true;
        player.controlLeft = hold && Input.Left;
        player.controlRight = hold && Input.Right;
        player.controlUp = hold && Input.Up;
        player.controlDown = hold && (Input.Down || Input.Interact);
        player.controlJump = hold && Input.Jump;
        player.controlUseItem = hold && Input.Shoot;
        player.controlUseTile = hold && Input.UseTile;
        if (Input.Item.HasValue) player.selectedItem = Math.Clamp(Input.Item.Value, 0, 9);
        if (Input.AimX.HasValue && Input.AimY.HasValue)
        {
            Vector2 aim = CampaignRuntime.ToWorld(Input.AimX.Value, Input.AimY.Value) - Main.screenPosition;
            // SetControls runs after PlayerInput.SetZoom_World: vanilla tile
            // targeting expects unzoomed host-local mouse coordinates here.
            Main.mouseX = (int)aim.X; Main.mouseY = (int)aim.Y;
            if (player.controlUseItem) player.direction = Input.AimX.Value < CampaignRuntime.ToCave(player.Center).X ? -1 : 1;
        }
    }
    public static void WriteState()
    {
        string? directory = DirectoryPath;
        if (directory == null) return;
        if (remaining > 0) remaining--;
        try
        {
            Directory.CreateDirectory(directory);
            Player player = Main.LocalPlayer;
            var state = new Dictionary<string, object?>();
            if (CampaignRuntime.Snapshot.ValueKind == JsonValueKind.Object)
                foreach (var field in CampaignRuntime.Snapshot.EnumerateObject()) state[field.Name] = field.Value.Clone();
            if (CampaignRuntime.CurrentMap.ValueKind == JsonValueKind.Object) state["map"] = CampaignRuntime.CurrentMap;
            Vector2 cave = CampaignRuntime.ToCave(player.Center);
            state["host"] = new
            {
                x = cave.X, y = cave.Y, worldX = player.position.X, worldY = player.position.Y,
                vx = player.velocity.X / CampaignRuntime.Scale, vy = player.velocity.Y / CampaignRuntime.Scale,
                life = player.statLife, maxLife = player.statLifeMax2, permanentMaxLife = player.statLifeMax, campaignId = CampaignBootstrap.CampaignId, dead = player.dead,
                retryPose = new {
                    head = new { x = player.headPosition.X, y = player.headPosition.Y, rotation = player.headRotation },
                    body = new { x = player.bodyPosition.X, y = player.bodyPosition.Y, rotation = player.bodyRotation },
                    legs = new { x = player.legPosition.X, y = player.legPosition.Y, rotation = player.legRotation },
                    player.immuneAlpha
                },
                difficulty = player.difficulty, originalDifficulty = player.GetModPlayer<CampaignPlayer>().OriginalDifficulty,
                width = player.width / CampaignRuntime.Scale, height = player.height / CampaignRuntime.Scale,
                grounded = player.velocity.Y == 0, selectedItem = player.selectedItem,
                itemAnimation = player.itemAnimation, itemTime = player.itemTime, toolTime = player.toolTime,
                skipCutsceneHeld = CaverarriaMod.SkipCutscene.Current, hasFocus = Main.hasFocus,
                skipAssignedKeys = CaverarriaMod.SkipCutscene.GetAssignedKeys().ToArray(),
                skipAssignedUiKeys = CaverarriaMod.SkipCutscene.GetAssignedKeys(Terraria.GameInput.InputMode.KeyboardUI).ToArray(),
                inputProfile = Terraria.GameInput.PlayerInput.CurrentProfile.Name, inputMode = Terraria.GameInput.PlayerInput.CurrentInputMode.ToString(),
                inputWritingText = Terraria.GameInput.PlayerInput.WritingText, inputBlockKey = Main.blockKey, inputRebinding = Terraria.GameInput.PlayerInput.CurrentlyRebinding,
                processedKeys = Main.keyState.GetPressedKeys().Select(key => key.ToString()).ToArray(),
                processedOldKeys = Main.oldKeyState.GetPressedKeys().Select(key => key.ToString()).ToArray(),
                pressedKeys = Microsoft.Xna.Framework.Input.Keyboard.GetState().GetPressedKeys().Select(key => key.ToString()).ToArray(),
                tileTargetX = Player.tileTargetX, tileTargetY = Player.tileTargetY, noBuilding = player.noBuilding,
                targetCell = CampaignTerrainEdits.Cell(Player.tileTargetX, Player.tileTargetY, out int cellX, out int cellY)
                    ? Enumerable.Range(0, 4).Select(index => {
                        var tile = Main.tile[CampaignRuntime.OriginTileX + cellX * 2 + index % 2, CampaignRuntime.OriginTileY + cellY * 2 + index / 2];
                        return new { tile.HasTile, tile.TileType };
                    }).ToArray() : null,
                heldItem = player.HeldItem.type, inventory = player.inventory.Take(10).Select(item => new { id = item.type, name = item.Name, stack = item.stack }).ToArray(),
                buffs = player.buffType.Select((type, index) => new { type, time = player.buffTime[index] }).Where(buff => buff.type != 0).ToArray(),
                projectiles = Main.projectile.Count(projectile => projectile.active && projectile.owner == player.whoAmI),
                controls = new { left = player.controlLeft, right = player.controlRight, jump = player.controlJump, shoot = player.controlUseItem },
                frame = CampaignRuntime.Frame, cameraX = Main.screenPosition.X, cameraY = Main.screenPosition.Y,
                screenWidth = Main.screenWidth, screenHeight = Main.screenHeight, musicVolume = Main.musicVolume, soundVolume = Main.soundVolume, vanillaMusic = Main.curMusic,
                worldZoom = Main.GameZoomTarget, campaignPixelScale = CampaignView.PixelScale,
                audioBackend = CampaignRuntime.Engine?.HasPcmAudio == true ? "Terraria/FNA" : "legacy",
                audioPlaying = CampaignRuntime.HostAudioPlaying, audioFrames = CampaignRuntime.HostAudioFrames, audioBuffers = CampaignRuntime.HostAudioBuffers
            };
            state["combatProxies"] = Main.npc.Where(npc => npc.active && npc.ModNPC is CaveEntity).Select(npc =>
            {
                var proxy = (CaveEntity)npc.ModNPC;
                return new { slot = npc.whoAmI, id = proxy.NativeId, boss = proxy.NativeBoss, epoch = proxy.Epoch,
                    generation = proxy.Generation, x = npc.position.X, y = npc.position.Y,
                    width = npc.width, height = npc.height, life = npc.life, maxLife = npc.lifeMax,
                    friendly = npc.friendly, chaseable = npc.chaseable, immune = npc.dontTakeDamage,
                    hittable = npc.CanBeChasedBy() };
            }).ToArray();
            state["lastCommandId"] = lastId;
            state["commandFramesRemaining"] = remaining;
            state["debugCommandsUsed"] = debugCommands;
            state["error"] = CampaignRuntime.Failure;
            state["performance"] = FramePerformance.Report();
            string pending = Path.Combine(directory, "state.pending");
            File.WriteAllText(pending, JsonSerializer.Serialize(state));
            File.Move(pending, Path.Combine(directory, "state.json"), true);
            if (lastCommand != null)
            {
                File.AppendAllText(Path.Combine(directory, "trace.jsonl"), JsonSerializer.Serialize(new { command = JsonDocument.Parse(lastCommand).RootElement, frame = CampaignRuntime.Frame, stage = CampaignRuntime.Snapshot.Field("stage") }) + "\n");
                lastCommand = null;
            }
        }
        catch (IOException) { }
    }
}
