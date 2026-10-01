using System.ComponentModel;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
using Terraria.ModLoader.Config.UI;

namespace Caverarria;

// Keep the original config name so existing camera preferences still load.
public sealed class CampaignViewConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;
    [Header("View"), DefaultValue(4), Range(2, 6)]
    public int CameraPixelScale = 4;

    [DefaultValue(1f), Range(.75f, 1.5f), Increment(.25f)]
    public float PlayerBodyScale = 1f;

    [Header("Combat"), DefaultValue(1f), Range(0f, 10f), Increment(.1f)]
    public float TerrariaWeaponDamageScale = 1f;

    [DefaultValue(1f), Range(0f, 10f), Increment(.1f)]
    public float EnemyDamageScale = 1f;

    [Header("Audio"), DefaultValue(false)]
    public bool MuteCampaignAudio;

    [DefaultValue(1f), Range(0f, 1f), Increment(.05f)]
    public float CampaignMusicVolume = 1f;

    [DefaultValue(1f), Range(0f, 1f), Increment(.05f)]
    public float CampaignSoundVolume = 1f;

    [DefaultValue(false)]
    public bool TerrariaSizedBlocks;

    [Header("Save"), DefaultValue(true)]
    public bool PersistentTerrainEdits = true;

    [JsonIgnore, ShowDespiteJsonIgnore, CustomModConfigItem(typeof(CampaignResetElement))]
    public bool ResetCurrentCampaign;
}

/// <summary>An immediate action, never a saved configuration value.</summary>
public sealed class CampaignResetElement : ConfigElement
{
    private UITextPanel<string> reset = null!, cancel = null!;
    private UIText description = null!, status = null!;
    private string? armedCampaign;
    private string? armedPath;
    private static string Text(string key) => Language.GetTextValue("Mods.Caverarria.ConfigReset." + key);

    public override void OnBind()
    {
        base.OnBind();
        DrawLabel = false;
        Height.Set(162, 0);
        var title = new UIText(Text("Title"), .85f);
        title.Left.Set(12, 0); title.Top.Set(10, 0); Append(title);
        description = new UIText(Text("Description"), .75f) { IsWrapped = true };
        description.Left.Set(12, 0); description.Top.Set(36, 0); description.Width.Set(-24, 1);
        Append(description);
        reset = new UITextPanel<string>(Text("Reset"), .75f);
        reset.Left.Set(12, 0); reset.Top.Set(90, 0); reset.Width.Set(180, 0); reset.Height.Set(30, 0);
        reset.OnLeftClick += (_, _) => ClickReset(); Append(reset);
        cancel = new UITextPanel<string>(Text("Cancel"), .75f);
        cancel.Left.Set(202, 0); cancel.Top.Set(90, 0); cancel.Width.Set(100, 0); cancel.Height.Set(30, 0);
        cancel.OnLeftClick += (_, _) => Disarm();
        status = new UIText("", .7f) { IsWrapped = true };
        status.Left.Set(12, 0); status.Top.Set(130, 0); status.Width.Set(-24, 1); Append(status);
    }

    private void ClickReset()
    {
        if (!CampaignSaveReset.Available) return;
        if (armedCampaign == null)
        {
            armedCampaign = CampaignBootstrap.CampaignId;
            armedPath = CampaignBootstrap.SavePath;
            reset.SetText(Text("Confirm"));
            Append(cancel);
        }
        else
        {
            CampaignSaveReset.Request(armedCampaign, armedPath!);
            Disarm();
        }
    }

    private void Disarm()
    {
        armedCampaign = armedPath = null;
        reset.SetText(Text("Reset")); cancel.Remove();
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (armedCampaign != null && (!CampaignSaveReset.Available || armedCampaign != CampaignBootstrap.CampaignId
            || armedPath != CampaignBootstrap.SavePath)) Disarm();
        bool available = CampaignSaveReset.Available;
        reset.BackgroundColor = available ? new Color(110, 45, 55) : new Color(55, 55, 65);
        reset.TextColor = available ? Color.White : Color.Gray;
        description.SetText(Text(armedCampaign != null ? "Confirmation" : available ? "Description" : "Unavailable"));
        status.SetText(CampaignSaveReset.Status);
    }
}

internal static class CampaignSaveReset
{
    private static bool pending;
    public static string Status { get; private set; } = "";
    public static bool Available => !pending && !Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer
        && CampaignBootstrap.IsCampaignWorld && CampaignRuntime.Active;

    public static void Request(string campaign, string path)
    {
        if (!Available || campaign != CampaignBootstrap.CampaignId || path != CampaignBootstrap.SavePath) return;
        pending = true;
        Status = Language.GetTextValue("Mods.Caverarria.ConfigReset.Resetting");
        Main.QueueMainThreadAction(() => Execute(campaign, path));
    }

    private static void Execute(string campaign, string path)
    {
        try
        {
            if (Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer || !CampaignBootstrap.IsCampaignWorld
                || campaign != CampaignBootstrap.CampaignId || path != CampaignBootstrap.SavePath)
            {
                Status = Language.GetTextValue("Mods.Caverarria.ConfigReset.Cancelled");
                return;
            }
            // The adapter flushes its virtual filesystem on disposal. Archive
            // afterwards so an old cached checkpoint cannot reappear on restart.
            CampaignRuntime.Dispose();
            string? backup = CampaignCheckpointBackup.Archive(path);
            CampaignRuntime.Start(loadProfile: false);
            string result = !CampaignRuntime.Active ? "StartFailed" : backup != null ? "Complete" : "FreshComplete";
            Status = Language.GetTextValue("Mods.Caverarria.ConfigReset." + result);
            if (backup != null) ModContent.GetInstance<CaverarriaMod>().Logger.Info($"Campaign reset; checkpoint backup: {backup}");
        }
        catch (Exception exception)
        {
            Status = Language.GetTextValue("Mods.Caverarria.ConfigReset.Failed");
            ModContent.GetInstance<CaverarriaMod>().Logger.Error("Campaign reset failed; checkpoint backup retained", exception);
            if (!CampaignRuntime.Active) CampaignRuntime.Start();
        }
        finally { pending = false; }
    }
}
