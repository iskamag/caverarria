using System.Reflection;
using System.Text.Json;
using Caverarria;
using Terraria;

internal static class Program
{
    private static readonly MethodInfo apply = typeof(CampaignRuntime).GetMethod("ApplySnapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo observeDeath = typeof(CampaignRuntime).GetMethod("ObserveNativeDeath", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo snapshot = typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Snapshot))!;
    private static readonly FieldInfo pending = typeof(CampaignRuntime).GetField("nativeDeathPending", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static int checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static JsonElement State(int epoch, bool alive, int life = 2, int maxLife = 5, bool hidden = false, string scene = "game")
        => JsonSerializer.SerializeToElement(new {
            scene, epoch, control_enabled = true, script_mode = "Map",
            player = new { alive, hidden, life, max_life = maxLife, x = 160, y = 128, vx = 0, vy = 0 },
            npcs = Array.Empty<object>(), bosses = Array.Empty<object>(), weapons = Array.Empty<object>()
        });

    private static void Apply(JsonElement value)
    {
        snapshot.SetValue(null, value);
        apply.Invoke(null, new object[] { false, false });
    }

    public static void Main()
    {
        string save = Path.Combine(Path.GetTempPath(), "caverarria-retry-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(save);
        try
        {
            Terraria.Program.SavePath = save;
            RunChecks();
            Console.WriteLine($"{checks} retry regression checks passed against actual CampaignRuntime snapshot/retry methods.");
        }
        finally { Directory.Delete(save, recursive: true); }
    }

    private static void RunChecks()
    {
        Terraria.Main.myPlayer = 0;
        Terraria.Main.player[0] = new Player();
        CampaignBootstrap.MarkCampaignWorld("retry-regression");
        var engine = new FakeEngine();
        typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Engine))!.SetValue(null, engine);
        Player player = Terraria.Main.LocalPlayer;

        Apply(State(1, true));
        player.statLife = 17; player.statLifeMax2 = 50;
        Apply(State(2, true));
        Check(player.statLife == 17, "ordinary room transfer overwrote fractional host HP");
        Apply(State(2, true, hidden: true));
        Apply(State(3, true));
        Check(player.statLife == 17, "hidden-but-alive drowning/rescue was treated as lethal death");

        player.dead = true; player.statLife = 0;
        Apply(State(3, false, life: 0));
        Check((bool)pending.GetValue(null)!, "native lethal death was not recorded");
        // Reproduce vanilla Spawn clearing dead and filling HP before native retry.
        player.dead = false; player.ghost = true; player.respawnTimer = 42; player.statLife = 50;
        Apply(State(3, false, life: 0));
        Check((bool)pending.GetValue(null)!, "host respawn discarded the pending native death");
        Apply(State(4, true));
        Check(player.statLife == 20 && player.statLifeMax2 == 50, "checkpoint retry retained vanilla full HP");
        Check(!player.dead && !player.ghost && player.respawnTimer == 0, "checkpoint retry retained death flags");
        Check(!(bool)pending.GetValue(null)!, "checkpoint retry did not clear pending death");
        player.statLife = 19;
        Apply(State(4, true));
        Check(player.statLife == 19, "repeated snapshot healed after retry");

        player.dead = true; player.statLife = 0;
        Apply(State(4, false, life: 0));
        Apply(State(5, true, life: 3, maxLife: 6));
        Check(player.statLife == 30 && player.statLifeMax2 == 60 && !player.dead, "retry while Terraria was still dead failed");

        player.statLife = 57;
        engine.RetrySnapshot = State(6, true, life: 2, maxLife: 6);
        CampaignRuntime.Retry();
        Check(player.statLife == 20 && player.statLifeMax2 == 60, "explicit R did not restore exact native checkpoint HP");

        player.statLife = 18;
        Apply(State(7, false, scene: "title"));
        Apply(State(8, true));
        Check(player.statLife == 18, "non-game scene was mistaken for a native death");

        // The death command and the first retry response can be reconciled in one host tick.
        snapshot.SetValue(null, State(8, false, life: 0));
        observeDeath.Invoke(null, null);
        player.statLife = 50;
        Apply(State(9, true));
        Check(player.statLife == 20, "death observed before the native tick was lost");
    }

    private sealed class FakeEngine : ICampaignEngine
    {
        public JsonElement RetrySnapshot;
        public int Width => 320;
        public int Height => 240;
        public bool HasPcmAudio => false;
        public int AudioSampleRate => 48000;
        public JsonElement Send(object request)
        {
            if (JsonSerializer.SerializeToElement(request).GetProperty("op").GetString() == "retry") return RetrySnapshot;
            throw new NotSupportedException();
        }
        public JsonElement Resize(int width, int height) => throw new NotSupportedException();
        public void CopyPixels(int layer, byte[] destination) => throw new NotSupportedException();
        public void ReadAudio(byte[] destination) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
