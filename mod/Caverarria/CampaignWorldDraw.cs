using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Draw Terraria actors over the campaign art without drawing the hidden Terraria terrain.</summary>
public sealed class CampaignWorldDraw : ModSystem
{
    private readonly List<Hook> hooks = new();
    private struct DrawState
    {
        public bool Saved;
        public bool PreviousDrawToScreen;
    }
    private static readonly List<DrawState> drawScopes = new();
    private static DrawState unscopedDraw;

    private delegate void OriginalWorldPass(Main main);
    private delegate void OriginalWaterPass(Main main, bool isBackground);
    private delegate void OriginalMapSection(Main main, int sectionX, int sectionY);

    private static bool SkipWorld => !Main.gameMenu && CampaignRuntime.Active;

    public override void Load()
    {
        if (Main.dedServ) return;
        try
        {
            // These wrappers return with the same SpriteBatch state they receive.
            // Keep DoDraw_WallsTilesNPCs itself: it prepares and draws the real actor caches.
            foreach (string name in new[] {
                "DrawBG", "DrawBackgroundBlackFill", "DrawBackground",
                "DoDraw_WallsAndBlacks", "DoDraw_Tiles_NonSolid", "DoDraw_Tiles_Solid",
                "DoDraw_Waterfalls"
            })
                hooks.Add(new Hook(FindMethod(name), (Action<OriginalWorldPass, Main>)DrawWorldPass));

            hooks.Add(new Hook(FindMethod("DrawWaters", typeof(bool)),
                (Action<OriginalWaterPass, Main, bool>)DrawWaterPass));
            // Main's queued section loop is not guarded by mapEnabled, unlike DrawToMap.
            hooks.Add(new Hook(FindMethod("DrawToMap_Section", typeof(int), typeof(int)),
                (Action<OriginalMapSection, Main, int, int>)DrawMapSection));

            Main.OnPreDraw += BeforeDraw;
            Main.OnPostDraw += AfterDraw;
        }
        catch
        {
            DisposeHooks();
            throw;
        }
    }

    private static MethodInfo FindMethod(string name, params Type[] parameters)
    {
        MethodInfo? method = typeof(Main).GetMethod(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: parameters, modifiers: null);
        if (method == null || method.ReturnType != typeof(void))
            throw new MissingMethodException(typeof(Main).FullName, name);
        return method;
    }

    private static void DrawWorldPass(OriginalWorldPass original, Main main)
    {
        if (!SkipWorld) original(main);
    }

    private static void DrawWaterPass(OriginalWaterPass original, Main main, bool isBackground)
    {
        if (!SkipWorld) original(main, isBackground);
    }

    private static void DrawMapSection(OriginalMapSection original, Main main, int sectionX, int sectionY)
    {
        if (!SkipWorld) original(main, sectionX, sectionY);
    }

    // Called by the whole Main.Draw hook. The finally counterpart also covers draw exceptions.
    public static void BeginDraw() => drawScopes.Add(default);

    private static void BeforeDraw(GameTime time)
    {
        if (!SkipWorld) return;
        int index = drawScopes.Count - 1;
        DrawState state = index >= 0 ? drawScopes[index] : unscopedDraw;
        if (!state.Saved)
        {
            state.PreviousDrawToScreen = Main.drawToScreen;
            state.Saved = true;
        }
        if (index >= 0) drawScopes[index] = state;
        else unscopedDraw = state;
        // Main assigns this from Lighting.UpdateEveryFrame before OnPreDraw. Direct mode makes
        // the terrain render-target builders return early and avoids compositing stale targets.
        Main.drawToScreen = true;
    }

    private static void AfterDraw(GameTime time) => RestoreCurrentDrawMode();

    public static void EndDraw()
    {
        RestoreCurrentDrawMode();
        if (drawScopes.Count > 0) drawScopes.RemoveAt(drawScopes.Count - 1);
    }

    private static void RestoreCurrentDrawMode()
    {
        int index = drawScopes.Count - 1;
        DrawState state = index >= 0 ? drawScopes[index] : unscopedDraw;
        if (!state.Saved) return;
        Main.drawToScreen = state.PreviousDrawToScreen;
        state.Saved = false;
        if (index >= 0) drawScopes[index] = state;
        else unscopedDraw = state;
    }

    public override void Unload()
    {
        if (Main.dedServ) return;
        Main.OnPreDraw -= BeforeDraw;
        Main.OnPostDraw -= AfterDraw;
        while (drawScopes.Count > 0) EndDraw();
        EndDraw();
        DisposeHooks();
    }

    private void DisposeHooks()
    {
        for (int i = hooks.Count - 1; i >= 0; i--) hooks[i].Dispose();
        hooks.Clear();
    }
}
