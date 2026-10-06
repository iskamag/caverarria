using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

public sealed class CaverarriaMod : Mod
{
    private Hook? menuUpdateHook;
    private Hook? audioUpdateHook;
    private Hook? drawHook;
    private delegate void OriginalDraw(Main main, GameTime time);
    private void DrawMeasured(OriginalDraw original, Main main, GameTime time)
    {
        long started = FramePerformance.Begin();
        CampaignWorldDraw.BeginDraw();
        try { original(main, time); }
        finally { CampaignActorPixels.Abort(); CampaignWorldDraw.EndDraw(); }
        if (CampaignRuntime.Active) FramePerformance.End("draw", started);
    }
    private delegate void OriginalAudioUpdate(Main main);
    private void UpdateAudio(OriginalAudioUpdate original, Main main)
    {
        if (!CampaignRuntime.Active) { original(main); return; }
        float volume = Main.musicVolume, ambient = Main.ambientVolume;
        try { Main.musicVolume = Main.ambientVolume = 0; original(main); }
        finally { Main.musicVolume = volume; Main.ambientVolume = ambient; }
        CampaignRuntime.UpdateAudio();
    }
    private delegate void OriginalUpdate(Main main, GameTime time);
    private void UpdateAfter(OriginalUpdate original, Main main, GameTime time)
    {
        long started = FramePerformance.Begin();
        long previousFrame = CampaignRuntime.Frame;
        original(main, time);
        if (CampaignRuntime.Active && CampaignRuntime.Frame != previousFrame) FramePerformance.End("update", started);
        if (Main.gameMenu) Automation.WriteMenuState();
        if (Main.gameMenu && Main.menuMode == 0)
        {
            try { CampaignBootstrap.AutoStart(); }
            catch (Exception exception) { CampaignRuntime.RecordFailure(exception); }
        }
    }
    public static ModKeybind Interact { get; private set; } = null!;
    public static ModKeybind Inventory { get; private set; } = null!;
    public static ModKeybind Map { get; private set; } = null!;
    public static ModKeybind NextWeapon { get; private set; } = null!;
    public static ModKeybind PreviousWeapon { get; private set; } = null!;
    public static ModKeybind Retry { get; private set; } = null!;
    public static ModKeybind SkipCutscene { get; private set; } = null!;

    public override void Load()
    {
        if (Main.dedServ) return;
        WeaponIcons.Load();
        Interact = KeybindLoader.RegisterKeybind(this, "Interact", "E");
        var update = typeof(Main).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        menuUpdateHook = new Hook(update, UpdateAfter);
        var draw = typeof(Main).GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic)!;
        drawHook = new Hook(draw, DrawMeasured);
        var audioUpdate = typeof(Main).GetMethod("UpdateAudio", BindingFlags.Instance | BindingFlags.NonPublic)!;
        audioUpdateHook = new Hook(audioUpdate, UpdateAudio);
        Retry = KeybindLoader.RegisterKeybind(this, "Retry", "R");
        SkipCutscene = KeybindLoader.RegisterKeybind(this, "SkipCutscene", "X");
        Inventory = KeybindLoader.RegisterKeybind(this, "Inventory", "I");
        Map = KeybindLoader.RegisterKeybind(this, "Map", "M");
        NextWeapon = KeybindLoader.RegisterKeybind(this, "NextWeapon", "V");
        PreviousWeapon = KeybindLoader.RegisterKeybind(this, "PreviousWeapon", "C");
    }

    public override void Unload()
    {
        WeaponIcons.Unload();
        audioUpdateHook?.Dispose(); audioUpdateHook = null;
        drawHook?.Dispose(); drawHook = null;
        menuUpdateHook?.Dispose();
        menuUpdateHook = null;
        CampaignRuntime.Dispose();
        Interact = null!;
        Retry = SkipCutscene = Inventory = Map = NextWeapon = PreviousWeapon = null!;
    }
}

public sealed class CampaignCommand : ModCommand
{
    public override string Command => "caverarria";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/caverarria [enter|save|retry|status]";
    public override string Description => "Enter the Cave Story campaign or save your progress.";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        switch (args.Length == 0 ? "enter" : args[0])
        {
            case "enter": CampaignBootstrap.EnterWithCurrentPlayer(); break;
            case "save": CampaignRuntime.Save(); caller.Reply("Campaign saved."); break;
            case "retry": CampaignRuntime.Retry(); break;
            default: caller.Reply(CampaignRuntime.Status()); break;
        }
    }
}

/// <summary>Restores the authored current room, discarding its terrain edits.</summary>
public sealed class RepairRoomCommand : ModCommand
{
    public override string Command => "repair_room";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/repair_room";
    public override string Description => "Restore the current Cave Story room to its authored terrain.";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (!CampaignRuntime.Active) { caller.Reply("The campaign is not running."); return; }
        caller.Reply(CampaignRuntime.RepairRoom() ? "Room repaired." : "No room to repair.");
    }
}
