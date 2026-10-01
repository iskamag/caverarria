using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.Generation;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using Terraria.WorldBuilding;

namespace Caverarria;

public sealed class CampaignSystem : ModSystem
{
    public override void UpdateUI(GameTime gameTime)
    {
        if (Main.gameMenu && !Main.dedServ) { CampaignBootstrap.AutoStart(); Automation.WriteMenuState(); }
    }
    public override void PostWorldLoad() { if (CampaignBootstrap.IsCampaignWorld) CampaignRuntime.Start(); }
    public override void OnWorldUnload() { CampaignRuntime.Save(); CampaignRuntime.Dispose(); }
    public override void ClearWorld() => CampaignBootstrap.ClearWorldMarker();
    public override void ModifyWorldGenTasks(List<GenPass> tasks, ref double totalWeight)
    {
        if (!string.Equals(Main.ActiveWorldFileData.SeedText, "caverarria", StringComparison.OrdinalIgnoreCase)) return;
        tasks.Clear();
        tasks.Add(new PassLegacy("Cave Story — The Island", (progress, configuration) =>
        {
            progress.Message = "Preparing the Cave Story campaign";
            CampaignBootstrap.PrepareWorld();
            progress.Set(1);
        }, 1));
        totalWeight = 1;
    }
    public override void PostUpdatePlayers() => CampaignRuntime.Tick();
    public override void PostDrawTiles() => CampaignRuntime.DrawWorld();
    public override void ModifyTransformMatrix(ref Terraria.Graphics.SpriteViewMatrix transform)
    {
        if (CampaignRuntime.Active) transform.Zoom = new Vector2(CampaignView.HostZoom);
    }
    public override void ModifyScreenPosition()
    {
        if (!CampaignRuntime.Active) return;
        var camera = CampaignRuntime.Snapshot.Field("camera");
        Main.screenPosition = CampaignView.WorldScreenPosition(new Vector2(camera.Number("x"), camera.Number("y")),
            CampaignRuntime.Engine!.Width, CampaignRuntime.Engine.Height);
    }
    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if (!CampaignRuntime.Active) return;
        layers.Insert(0, new LegacyGameInterfaceLayer("Caverarria: Campaign foreground", CampaignRuntime.DrawForeground, InterfaceScaleType.None));
        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        layers.Insert(index < 0 ? layers.Count : index, new LegacyGameInterfaceLayer("Caverarria: Campaign overlay", CampaignRuntime.DrawOverlay, InterfaceScaleType.None));
    }
    public override void ModifyTimeRate(ref double timeRate, ref double tileUpdateRate, ref double eventUpdateRate)
    {
        if (!CampaignRuntime.Active) return;
        timeRate = tileUpdateRate = eventUpdateRate = 0;
    }
    public override void LoadWorldData(TagCompound tag)
    {
        if (tag.GetBool("caverarriaCampaign")) CampaignBootstrap.MarkCampaignWorld(tag.GetString("campaignId"));
    }
    public override void SaveWorldData(TagCompound tag)
    {
        if (!CampaignBootstrap.IsCampaignWorld) return;
        tag["caverarriaCampaign"] = true;
        tag["campaignId"] = CampaignBootstrap.CampaignId;
    }
    public override void SaveWorldHeader(TagCompound tag)
    {
        if (CampaignBootstrap.IsCampaignWorld) tag["caverarriaCampaign"] = true;
    }
    public override void PreSaveAndQuit()
    {
        CampaignRuntime.Save();
        Main.LocalPlayer.GetModPlayer<CampaignPlayer>().RestoreOutsideHealth();
    }
}

public sealed class CampaignPlayer : ModPlayer
{
    private readonly Dictionary<string, int> savedCampaignHealth = new();
    private bool baselineCaptured, leaving, saving;
    private int outsideLife, outsideMaximum, savingLife, savingMaximum;
    private bool outsideDead, outsideGhost, savingDead, savingGhost;
    private int outsideRespawn, savingRespawn;
    private byte outsideDifficulty, savingDifficulty;
    public bool UsingCampaignHealth => baselineCaptured && !leaving;
    internal byte OriginalDifficulty => baselineCaptured ? outsideDifficulty : Player.difficulty;

    internal void BeginCampaign(int life, int maximum, bool load)
    {
        if (!baselineCaptured)
        {
            outsideLife = Player.statLife;
            outsideMaximum = Player.statLifeMax2;
            outsideDead = Player.dead; outsideGhost = Player.ghost; outsideRespawn = Player.respawnTimer;
            outsideDifficulty = Player.difficulty;
            baselineCaptured = true;
        }
        leaving = false;
        // Campaign deaths use the original checkpoint retry for every character.
        // Journey retains its mode; Mediumcore and Hardcore keep their gear/file.
        Player.difficulty = outsideDifficulty is 1 or 2 ? (byte)0 : outsideDifficulty;
        int campaignLife = Math.Max(1, life * 10);
        if (load && savedCampaignHealth.TryGetValue(CampaignBootstrap.HealthKey, out int saved)
            && saved > 0 && (saved + 9) / 10 == life)
            campaignLife = saved;
        Player.statLife = Math.Min(campaignLife, maximum * 10);
        Player.statLifeMax2 = Math.Max(1, maximum * 10);
    }
    private void RememberCampaignHealth()
    {
        if (UsingCampaignHealth && !saving && !string.IsNullOrEmpty(CampaignBootstrap.HealthKey))
            savedCampaignHealth[CampaignBootstrap.HealthKey] = Player.statLife;
    }
    internal void RestoreOutsideHealth()
    {
        if (!baselineCaptured) return;
        RememberCampaignHealth();
        leaving = true;
        ApplyOutsideState();
    }
    private void ApplyOutsideState()
    {
        Player.statLife = Math.Clamp(outsideLife, outsideDead ? 0 : 1, Math.Max(1, Player.statLifeMax));
        Player.statLifeMax2 = Math.Max(Player.statLifeMax, outsideMaximum);
        Player.dead = outsideDead; Player.ghost = outsideGhost; Player.respawnTimer = outsideRespawn;
        Player.difficulty = outsideDifficulty;
    }
    internal void AddOutsideHealth(int amount)
    {
        if (!baselineCaptured || amount <= 0) return;
        outsideMaximum += amount;
        outsideLife += amount;
    }
    internal void FinishCampaign()
    {
        RestoreOutsideHealth();
        baselineCaptured = saving = false;
    }
    public override void PreSavePlayer()
    {
        if (!CampaignRuntime.Active || !baselineCaptured || saving) return;
        RememberCampaignHealth();
        savingLife = Player.statLife; savingMaximum = Player.statLifeMax2;
        savingDead = Player.dead; savingGhost = Player.ghost; savingRespawn = Player.respawnTimer;
        savingDifficulty = Player.difficulty;
        ApplyOutsideState();
        saving = true;
    }
    public override void PostSavePlayer()
    {
        if (!saving) return;
        if (!leaving)
        {
            Player.statLife = savingLife; Player.statLifeMax2 = savingMaximum;
            Player.dead = savingDead; Player.ghost = savingGhost; Player.respawnTimer = savingRespawn;
            Player.difficulty = savingDifficulty;
        }
        saving = false;
    }
    public override void SaveData(TagCompound tag)
    {
        RememberCampaignHealth();
        var health = new TagCompound();
        foreach (var record in savedCampaignHealth) health[record.Key] = record.Value;
        tag["campaignHealth"] = health;
    }
    public override void LoadData(TagCompound tag)
    {
        savedCampaignHealth.Clear();
        foreach (var record in tag.GetCompound("campaignHealth"))
            if (record.Value is int value) savedCampaignHealth[record.Key] = value;
    }
    public override void SetControls()
    {
        if (!CampaignRuntime.Active || Player.whoAmI != Main.myPlayer) return;
        Automation.ReadInput();
        Automation.Apply(Player);
        CampaignRuntime.CaptureControls(Player);
        if (!CampaignRuntime.ControlsEnabled)
        {
            Player.controlLeft = Player.controlRight = Player.controlJump = Player.controlUseItem = Player.controlUseTile = false;
            Player.velocity = Vector2.Zero;
        }
        else if (CampaignRuntime.IronheadMovement)
        {
            // The authored swimming mode integrates motion in the native engine.
            // Raw arrows were captured above; Terraria still handles held-item use.
            Player.controlLeft = Player.controlRight = Player.controlJump = false;
            Player.velocity = Vector2.Zero;
        }
    }
    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        // Terraria's UpdateDead copies fresh triggers but skips SetControls.
        // The native Restart menu must still receive direction/jump edges while
        // the host respawn timer is running, rather than the last living input.
        if (!CampaignRuntime.Active || Player.whoAmI != Main.myPlayer || !Player.dead) return;
        Automation.ReadInput();
        Automation.Apply(Player);
        CampaignRuntime.CaptureControls(Player);
    }
    public override void OnEnterWorld()
    {
        if (!CampaignRuntime.Active) return;
        CampaignRuntime.PlacePlayerAtCampaignPosition();
        Player.noFallDmg = true;
        Player.fallStart = Player.fallStart2 = (int)(Player.position.Y / 16);
    }
    public override void PostUpdateMiscEffects()
    {
        if (!CampaignRuntime.Active || !UsingCampaignHealth) return;
        Player.statLifeMax2 = Math.Max(1, CampaignRuntime.Snapshot.Field("player").Integer("max_life", 3) * 10);
        Player.statLife = Math.Min(Player.statLife, Player.statLifeMax2);
        Player.noFallDmg = true;
        Player.jumpSpeedBoost += 2.5f;
        Player.gravity = CampaignRuntime.IronheadMovement || CampaignRuntime.Snapshot.Field("player").Boolean("booster_active") ? 0 : .325f;
        Player.breath = Player.breathMax; Player.breathCD = 0;
    }
    public override void PostUpdateRunSpeeds()
    {
        if (!CampaignRuntime.Active) return;
        // Compensate for 3x authored pixels while retaining Terraria acceleration/jump integration.
        Player.maxRunSpeed = Math.Max(Player.maxRunSpeed, 4f);
        Player.accRunSpeed = Math.Max(Player.accRunSpeed, 4f);
        Player.runAcceleration *= 1.25f;
    }
    public override void PreUpdateMovement()
    {
        if (!CampaignRuntime.Active) return;
        if (!CampaignRuntime.ControlsEnabled || CampaignRuntime.IronheadMovement) { Player.velocity = Vector2.Zero; return; }
        Player.breath = Player.breathMax;
        Player.breathCD = 0;
        if ((CampaignRuntime.Snapshot.Field("player").Integer("flags") & 256) != 0) Player.wet = true;
        Player.maxFallSpeed = Math.Max(Player.maxFallSpeed, 7.5f);
        Player.noFallDmg = true;
    }
    public override void PostUpdate()
    {
        if (!CampaignRuntime.Active) return;
        Player.fallStart = Player.fallStart2 = (int)(Player.position.Y / 16);
        Player.noFallDmg = true;
    }
    public override void HideDrawLayers(PlayerDrawSet drawInfo)
    {
        if (!CampaignRuntime.Active || CampaignRuntime.Snapshot.Text("scene") == "game" && !CampaignRuntime.Snapshot.Field("player").Boolean("hidden") && CampaignRuntime.Snapshot.Field("player").Boolean("alive", true)) return;
        foreach (var layer in PlayerDrawLayerLoader.Layers) layer.Hide();
    }
    public override void DrawEffects(PlayerDrawSet drawInfo, ref float r, ref float g, ref float b, ref float a, ref bool fullBright)
    {
        if (CampaignRuntime.Active) fullBright = true;
    }
    public override void TransformDrawData(ref PlayerDrawSet drawInfo)
    {
        if (!CampaignRuntime.Active || drawInfo.headOnlyRender) return;
        // Use the host's own all-layer transform about the avatar's feet. This
        // enlarges armor, hair and held items together without altering physics.
        PlayerDrawLayers.DrawPlayer_ScaleDrawData(ref drawInfo, CampaignView.PlayerScale);
    }
}

public sealed class CampaignSpawns : GlobalNPC
{
    public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
    {
        if (!CampaignRuntime.Active) return;
        spawnRate = int.MaxValue;
        maxSpawns = 0;
    }
}
