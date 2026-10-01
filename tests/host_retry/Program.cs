using System.Reflection;
using System.Text.Json;
using Caverarria;
using Terraria;
using Terraria.ModLoader.IO;
using Terraria.GameInput;
using Terraria.ModLoader;

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
            CheckCapsules();
            Console.WriteLine($"{checks} retry regression checks passed against actual CampaignRuntime snapshot/retry methods.");
        }
        finally { Directory.Delete(save, recursive: true); }
    }

    private static void RunChecks()
    {
        Terraria.Main.myPlayer = 0;
        Terraria.Main.player[0] = new Player();
        for (int slot = 0; slot < Terraria.Main.item.Length; slot++)
            Terraria.Main.item[slot] ??= new Item();
        CampaignBootstrap.MarkCampaignWorld("retry-regression");
        var engine = new FakeEngine();
        typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Engine))!.SetValue(null, engine);
        Player player = Terraria.Main.LocalPlayer;
        var hooks = new CampaignPlayer();
        var capsules = new LifeCapsulePlayer();
        RegisterPlayer(player, hooks, 0);
        RegisterPlayer(player, capsules, 1);
        typeof(Player).GetField("modPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(player, new ModPlayer[] { hooks, capsules });
        CheckDeadInput(player, hooks);

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

    private static void CheckCapsules()
    {
        var capsules = new LifeCapsulePlayer();
        Check(capsules.ObserveMaximum("island", 3) == 0, "starting health granted a capsule upgrade");
        Check(capsules.ObserveMaximum("island", 6) == 30, "capsule did not immediately grant 30 permanent HP");
        Check(capsules.ObserveMaximum("island", 6) == 0, "repeated snapshot duplicated capsule health");
        Check(capsules.ObserveMaximum("island", 3) == 0 && capsules.ObserveMaximum("island", 6) == 0,
            "checkpoint replay duplicated the permanent upgrade");
        Check(capsules.ObserveMaximum("island", 10) == 40, "later capsule upgrade was lost");
        var tag = new TagCompound();
        capsules.SaveData(tag);
        var reloaded = new LifeCapsulePlayer();
        reloaded.LoadData(tag);
        reloaded.ModifyMaxStats(out var health, out _);
        Check(health.Base == 70, "permanent capsule health did not survive player save/load");
        Check(reloaded.ObserveMaximum("island", 10) == 0, "player reload lost rewarded capsule milestones");
        Check(reloaded.ObserveMaximum("other-island", 6) == 30, "independent campaign's capsules were suppressed");
        var migrated = new LifeCapsulePlayer();
        Check(migrated.ObserveMaximum("old-alpha", 10) == 70, "old alpha campaign capacity did not migrate");
    }

    private static void RegisterPlayer<T>(Player player, T hooks, ushort index) where T : ModPlayer
    {
        typeof(ModPlayer).GetProperty("Entity")!.SetValue(hooks, player);
        typeof(ModPlayer).GetProperty("Index")!.SetValue(hooks, index);
        typeof(ContentInstance<T>).GetProperty("Instance")!.SetValue(null, hooks);
    }

    private static void CheckDeadInput(Player player, CampaignPlayer hooks)
    {
        // The real host calls ProcessTriggers after copying controls during
        // UpdateDead, but never calls SetControls on that path.
        var raw = typeof(CampaignRuntime).GetField("rawControls", BindingFlags.Static | BindingFlags.NonPublic)!;
        var mod = new CaverarriaMod();
        var fileType = typeof(Mod).Assembly.GetType("Terraria.ModLoader.Core.TmodFile")!;
        object file = Activator.CreateInstance(fileType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { "inert-retry-fixture.tmod", "Caverarria", new Version(0, 1) }, null)!;
        typeof(Mod).GetProperty("File", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mod, file);
        foreach (string name in new[] { "NextWeapon", "PreviousWeapon" })
        {
            var keybind = (ModKeybind)Activator.CreateInstance(typeof(ModKeybind), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { mod, name, "" }, null)!;
            typeof(CaverarriaMod).GetProperty(name)!.SetValue(null, keybind);
            string fullName = (string)typeof(ModKeybind).GetProperty("FullName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(keybind)!;
            PlayerInput.Triggers.Current.KeyStatus[fullName] = false;
        }
        snapshot.SetValue(null, State(0, false, life: 0));
        player.dead = true; player.statLife = 0; player.respawnTimer = 600;
        player.controlLeft = true; player.controlJump = true;
        hooks.ProcessTriggers(PlayerInput.Triggers.Current);
        Check((int)raw.GetValue(null)! == (1 | 64), "dead player could not send fresh native restart selection/confirmation");
        Check(player.dead && player.respawnTimer == 600 && player.statLife == 0, "capturing death-menu input changed the death state");
        player.controlLeft = player.controlJump = false;
        hooks.ProcessTriggers(PlayerInput.Triggers.Current);
        Check((int)raw.GetValue(null)! == 0, "released death-menu buttons stayed held");
        player.whoAmI = 1; player.controlRight = true;
        hooks.ProcessTriggers(PlayerInput.Triggers.Current);
        Check((int)raw.GetValue(null)! == 0, "remote player's death-menu input reached the local campaign");
        player.whoAmI = 0; player.dead = false;
        hooks.ProcessTriggers(PlayerInput.Triggers.Current);
        Check((int)raw.GetValue(null)! == 0, "living player bypassed the normal SetControls ownership path");
        player.controlRight = false;
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
