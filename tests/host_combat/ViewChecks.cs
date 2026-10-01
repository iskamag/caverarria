using System.Reflection;
using System.Text.Json;
using Caverarria;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

internal static class ViewChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string savePath = Path.Combine(Path.GetTempPath(), "caverarria-view-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(savePath);
        Terraria.Program.SavePath = savePath;
        try { RunChecks(check); }
        finally { Directory.Delete(savePath, recursive: true); }
    }

    private static void RunChecks(Action<bool, string> check)
    {
        SnapshotMapChecks.Run(check);
        for (int scale = 2; scale <= 6; scale++)
        {
            check(CampaignView.EffectivePixelScale(scale, 1f) == scale, "100% vanilla zoom changed the base camera scale");
            check(CampaignView.EffectivePixelScale(scale, 2f) == scale * 2, "200% vanilla zoom did not magnify the campaign");
            check(CampaignView.EffectivePixelScale(scale, 1.5f) >= scale, "vanilla zoom reduced campaign pixel size");
        }
        var previousEngine = CampaignRuntime.Engine;
        var fitted = CampaignView.InterfaceRectangle(1280, 720, 160, 120, 8);
        check(fitted.Width <= 1280 && fitted.Height <= 720 && fitted.X >= 0 && fitted.Y >= 0,
            "high world zoom cropped native dialogue");
        check(fitted.Width % 160 == 0 && fitted.Height % 120 == 0,
            "native dialogue lost whole-pixel scaling at high zoom");
        bool previousMenu = Main.gameMenu;
        int previousPlayerIndex = Main.myPlayer;
        Player previousPlayer = Main.player[0];
        var hooks = new CampaignPlayer();
        var player = new Player { whoAmI = 0, statLife = 100, statLifeMax = 100, statLifeMax2 = 100 };
        typeof(ModPlayer).GetProperty("Entity")!.SetValue(hooks, player);
        typeof(ModPlayer).GetProperty("Index")!.SetValue(hooks, (ushort)0);
        typeof(ContentInstance<CampaignPlayer>).GetProperty("Instance")!.SetValue(null, hooks);
        typeof(Player).GetField("modPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(player, new ModPlayer[] { hooks });
        typeof(ContentInstance<CampaignViewConfig>).GetProperty("Instance")!
            .SetValue(null, new CampaignViewConfig { PlayerBodyScale = 1.5f });
        int savedWidth = Main.screenWidth, savedHeight = Main.screenHeight;
        float savedZoom = Main.GameZoomTarget;
        try
        {
            foreach (var screen in new[] { new Point(1280, 720), new Point(1920, 1080), new Point(1365, 767) })
            {
                Main.screenWidth = screen.X; Main.screenHeight = screen.Y;
                Main.GameZoomTarget = 1f;
                int uiWidth = CampaignView.InterfaceViewportWidth, uiHeight = CampaignView.InterfaceViewportHeight;
                var expected = CampaignView.InterfaceRectangle(uiWidth, uiHeight);
                for (float zoom = 1f; zoom <= 2f; zoom += .25f)
                {
                    Main.GameZoomTarget = zoom;
                    check(CampaignView.InterfaceViewportWidth == uiWidth && CampaignView.InterfaceViewportHeight == uiHeight,
                        "world zoom changed native interface canvas");
                    var output = CampaignView.InterfaceRectangle(uiWidth, uiHeight);
                    check(output == expected && output.X < CampaignView.InterfacePixelScale && output.Y < CampaignView.InterfacePixelScale,
                        "world zoom moved native XP/fade edge away from screen edge");
                }
            }
        }
        finally { Main.screenWidth = savedWidth; Main.screenHeight = savedHeight; Main.GameZoomTarget = savedZoom; }
        check(new CampaignViewConfig().PlayerBodyScale == 1f && CampaignRuntime.Scale == 2f,
            "default avatar no longer has ten by twenty-one logical pixels");
        try
        {
            Main.myPlayer = 0;
            Main.player[0] = player;
            CampaignBootstrap.MarkCampaignWorld("view-regression");
            typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Engine))!.SetValue(null, new InertEngine());
            hooks.BeginCampaign(3, 3, false);
            Main.gameMenu = false;
            check(CampaignView.IsCampaignAvatar(player), "actual campaign avatar lost visual effects");
            check(player.width == 30 && player.height == 63, "visible 1.5 body still has a 1.0 collision box");
            Vector2 feet = player.Bottom;
            CampaignBody.Apply(player);
            check(player.Bottom == feet, "reapplying body size displaced planted feet");
            check(CampaignBody.Size(1f) == new Point(20, 42) && CampaignBody.Size(2f) == new Point(40, 84),
                "body collision disagrees with configured visual dimensions");
            BodyChecks.Run(player, check);
            CheckScale(player, hooks, 1.5f, check, "campaign avatar scale was not applied");
            Main.gameMenu = true;
            check(CampaignRuntime.Active && !CampaignView.InWorld, "stale engine during menu remained drawable");
            CheckScale(player, hooks, 1f, check, "campaign scale leaked into menu render");
            Main.gameMenu = false;
            var preview = new Player { whoAmI = player.whoAmI };
            check(!CampaignView.IsCampaignAvatar(preview), "preview clone inherited avatar effects from matching whoAmI");
            CheckScale(preview, hooks, 1f, check, "campaign scale leaked into inventory/dresser preview");
            hooks.RestoreOutsideHealth();
            check(player.width == 20 && player.height == 42 && player.Bottom == feet,
                "leaving campaign retained enlarged collision or moved feet");
            CheckScale(player, hooks, 1f, check, "leaving campaign retained visual scale");
            hooks.BeginCampaign(3, 3, false);
            CampaignBootstrap.ClearWorldMarker();
            CheckScale(player, hooks, 1f, check, "noncampaign world retained visual scale");
            CampaignBootstrap.MarkCampaignWorld("view-regression");
            hooks.FinishCampaign();
            CheckScale(player, hooks, 1f, check, "completed campaign lifecycle retained visual scale");
        }
        finally
        {
            CampaignBootstrap.ClearWorldMarker();
            typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Engine))!.SetValue(null, previousEngine);
            Main.gameMenu = previousMenu;
            Main.myPlayer = previousPlayerIndex;
            Main.player[0] = previousPlayer;
        }
    }

    private static void CheckScale(Player player, CampaignPlayer hooks, float expected, Action<bool, string> check, string message)
    {
        var draw = new PlayerDrawSet {
            drawPlayer = player, Position = player.position,
            DrawDataCache = new List<DrawData> { new DrawData { scale = Vector2.One, position = Vector2.Zero } }
        };
        hooks.TransformDrawData(ref draw);
        check(draw.DrawDataCache[0].scale == new Vector2(expected), message);
    }

    private sealed class InertEngine : ICampaignEngine
    {
        public int Width => 320;
        public int Height => 240;
        public bool HasPcmAudio => false;
        public int AudioSampleRate => 48000;
        public JsonElement Send(object request) => default;
        public JsonElement Resize(int width, int height) => default;
        public void CopyPixels(int layer, byte[] destination) { }
        public void ReadAudio(byte[] destination) { }
        public void Dispose() { }
    }
}
