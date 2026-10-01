using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;

namespace Caverarria;

internal static class CampaignDataInstaller
{
    private static readonly object Gate = new();
    private static CaveDataProgress? progress;
    private static Task? operation;
    private static CancellationTokenSource? cancellation;
    private static string selectedDataPath = "";
    private static int resumePending;
    private static bool previousMouseLeft, leaving;
    private static long diagnosticTime;
    internal static CaveDataProgress? Progress => Volatile.Read(ref progress);
    private static bool Failed => Progress?.Phase is "error" or "startup_error";
    internal static bool Visible => !Main.dedServ && !Main.gameMenu && !leaving && CampaignBootstrap.IsCampaignWorld
        && !CampaignRuntime.Active && Progress is { Phase: not "ready" };

    /// <summary>Called at campaign entry; file/network work runs outside the game thread.</summary>
    internal static bool EnsureReady()
    {
        string dataPath = Environment.GetEnvironmentVariable("CAVERARRIA_DATA") ?? Path.Combine(CampaignBootstrap.AssetsPath, "data");
        lock (Gate)
        {
            leaving = false;
            if (selectedDataPath != dataPath)
            {
                cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
                operation = null; selectedDataPath = dataPath; progress = null;
                Interlocked.Exchange(ref resumePending, 0);
            }
            if (Progress?.Phase == "ready") return true;
            if (CaveStoryDataArchive.IsInstalled(dataPath))
            {
                progress = new("ready", "Cave Story is ready.");
                return true;
            }
            if (operation is { IsCompleted: false } || Failed) return false;
            if (Environment.GetEnvironmentVariable("CAVERARRIA_DATA") != null)
            {
                progress = new("error", "The selected Cave Story data folder is incomplete. Restore the original freeware data and retry.");
                return false;
            }
            progress = new("download", "Downloading the original Cave Story freeware data…");
            var attempt = new CancellationTokenSource();
            cancellation = attempt;
            string assetsPath = CampaignBootstrap.AssetsPath;
            operation = Task.Run(async () =>
            {
                try
                {
                    await CaveStoryDataArchive.DownloadAndInstallAsync(assetsPath, status => PublishStatus(attempt, status), attempt.Token).ConfigureAwait(false);
                    if (ReferenceEquals(cancellation, attempt)) Interlocked.Exchange(ref resumePending, 1);
                }
                catch (OperationCanceledException)
                {
                    PublishStatus(attempt, new("error", "The download was interrupted. Retry to continue."));
                }
                catch (Exception exception)
                {
                    PublishStatus(attempt, new("error", exception.Message));
                }
            });
            return false;
        }
    }

    private static void PublishStatus(CancellationTokenSource attempt, CaveDataProgress status)
    {
        lock (Gate)
            if (ReferenceEquals(cancellation, attempt)) Volatile.Write(ref progress, status);
    }

    internal static void ReportStartupError(Exception exception)
    {
        lock (Gate)
        {
            Interlocked.Exchange(ref resumePending, 0);
            Volatile.Write(ref progress, new("startup_error", exception.Message));
        }
    }

    private static void Retry()
    {
        lock (Gate)
        {
            if (operation is { IsCompleted: false }) return;
            cancellation?.Dispose(); cancellation = null; operation = null; progress = null;
        }
        if (EnsureReady()) Interlocked.Exchange(ref resumePending, 1);
    }

    internal static void Pump()
    {
        if (Main.dedServ) return;
        if (!leaving && !Main.gameMenu && CampaignBootstrap.IsCampaignWorld && !CampaignRuntime.Active
            && Interlocked.Exchange(ref resumePending, 0) != 0)
            CampaignRuntime.Start();
        if (Visible)
        {
            var (retry, leave) = ButtonBounds();
            Vector2 mouse = Main.MouseScreen / Main.UIScale;
            bool click = Main.mouseLeft && !previousMouseLeft;
            if (Failed && (CaverarriaMod.Retry.JustPressed || click && retry.Contains(mouse.ToPoint()))) Retry();
            else if (click && leave.Contains(mouse.ToPoint()))
            {
                leaving = true;
                Main.gameMenu = true; Main.menuMode = 10;
                WorldGen.SaveAndQuit();
            }
        }
        previousMouseLeft = Main.mouseLeft;
        WriteDiagnostic();
    }

    internal static void Stop()
    {
        lock (Gate)
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            operation = null; progress = null; selectedDataPath = "";
            Interlocked.Exchange(ref resumePending, 0);
        }
    }

    private static Rectangle PanelBounds()
    {
        int screenWidth = (int)(Main.screenWidth / Main.UIScale), screenHeight = (int)(Main.screenHeight / Main.UIScale);
        int width = Math.Min(640, screenWidth - 40);
        return new Rectangle((screenWidth - width) / 2, (screenHeight - 286) / 2, width, 286);
    }

    private static (Rectangle Retry, Rectangle Leave) ButtonBounds()
    {
        var panel = PanelBounds();
        return (new Rectangle(panel.X + 28, panel.Bottom - 62, 150, 36), new Rectangle(panel.Right - 178, panel.Bottom - 62, 150, 36));
    }

    internal static bool Draw()
    {
        if (!Visible) return true;
        var state = Progress!;
        var panel = PanelBounds();
        var spriteBatch = Main.spriteBatch;
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(0, 0, (int)(Main.screenWidth / Main.UIScale), (int)(Main.screenHeight / Main.UIScale)), new Color(8, 12, 22));
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, panel, new Color(23, 32, 48));
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(panel.X, panel.Y, panel.Width, 3), new Color(190, 64, 64));
        Vector2 center = new(panel.Center.X, panel.Y + 28);
        Utils.DrawBorderString(spriteBatch, "CAVERARRIA", center, new Color(232, 234, 224), 1.25f, .5f);
        string title = state.Phase == "startup_error" ? "Cave Story could not start" : state.Phase == "error" ? "Cave Story data could not be installed" : "Preparing Cave Story";
        Utils.DrawBorderString(spriteBatch, title, center + new Vector2(0, 36), Color.White, .9f, .5f);
        float textY = panel.Y + 102;
        foreach (string line in Wrap(state.Message, panel.Width - 56).Take(4))
        {
            Utils.DrawBorderString(spriteBatch, line, new Vector2(panel.X + 28, textY), Failed ? new Color(255, 181, 158) : new Color(199, 209, 218), .8f);
            textY += 21;
        }
        if (!Failed)
        {
            Rectangle track = new(panel.X + 28, panel.Y + 168, panel.Width - 56, 7);
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, track, new Color(11, 18, 30));
            float amount = state.Total > 0 ? Math.Clamp((float)state.Completed / state.Total, 0, 1) : .1f;
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(track.X, track.Y, (int)(track.Width * amount), track.Height), new Color(92, 174, 183));
            Utils.DrawBorderString(spriteBatch, "Original freeware · English translation", new Vector2(panel.Center.X, panel.Y + 187), new Color(150, 169, 184), .7f, .5f);
        }
        var (retry, leave) = ButtonBounds();
        if (Failed) DrawButton(retry, "Retry [R]");
        DrawButton(leave, "Leave world");
        Main.LocalPlayer.mouseInterface = true;
        return true;
    }

    private static void DrawButton(Rectangle bounds, string text)
    {
        bool hover = bounds.Contains((Main.MouseScreen / Main.UIScale).ToPoint());
        Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, bounds, hover ? new Color(61, 84, 104) : new Color(40, 57, 74));
        Utils.DrawBorderString(Main.spriteBatch, text, new Vector2(bounds.Center.X, bounds.Y + 7), Color.White, .8f, .5f);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        string line = "";
        foreach (string word in text.Replace('\n', ' ').Replace('\r', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && FontAssets.MouseText.Value.MeasureString(next).X * .8f > width) { yield return line; line = word; }
            else line = next;
        }
        if (line.Length != 0) yield return line;
    }

    private static void WriteDiagnostic()
    {
        string? testPath = Environment.GetEnvironmentVariable("CAVERARRIA_TEST_DIR");
        if (testPath == null || Progress == null || Environment.TickCount64 - diagnosticTime < 250) return;
        diagnosticTime = Environment.TickCount64;
        try
        {
            Directory.CreateDirectory(testPath);
            string path = Path.Combine(testPath, "setup-state.json");
            File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new { status = Progress, dataPath = selectedDataPath, active = CampaignRuntime.Active, visible = Visible }));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
    }
}

public sealed class CampaignDataSystem : ModSystem
{
    public override void PostUpdatePlayers() => CampaignDataInstaller.Pump();
    public override void Unload() => CampaignDataInstaller.Stop();
    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if (CampaignDataInstaller.Visible)
            layers.Add(new LegacyGameInterfaceLayer("Caverarria: First-use data", CampaignDataInstaller.Draw, InterfaceScaleType.UI));
    }
}

public sealed class CampaignSetupPlayer : ModPlayer
{
    public override void SetControls()
    {
        if (!CampaignDataInstaller.Visible || Player.whoAmI != Main.myPlayer) return;
        Player.controlLeft = Player.controlRight = Player.controlUp = Player.controlDown = Player.controlJump = Player.controlUseItem = Player.controlUseTile = false;
        Player.velocity = Vector2.Zero;
    }
    public override void PreUpdateMovement()
    {
        if (!CampaignDataInstaller.Visible || Player.whoAmI != Main.myPlayer) return;
        Player.velocity = Vector2.Zero;
        Player.noFallDmg = true; Player.breath = Player.breathMax;
        Player.fallStart = Player.fallStart2 = (int)(Player.position.Y / 16);
    }
}
