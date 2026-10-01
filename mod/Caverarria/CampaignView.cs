using System.ComponentModel;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.Graphics;
using Terraria.Graphics.Renderers;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace Caverarria;

public sealed class CampaignViewConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;
    [DefaultValue(4), Range(2, 6)]
    public int CameraPixelScale = 4;
    [DefaultValue(1.5f), Range(1f, 2f), Increment(.25f)]
    public float PlayerVisualScale = 1.5f;
}

public sealed class CampaignView : ModSystem
{
    private readonly List<Hook> samplingHooks = new();
    public static int PixelScale => Math.Clamp(ModContent.GetInstance<CampaignViewConfig>().CameraPixelScale, 2, 6);
    public static float HostZoom => PixelScale / CampaignRuntime.Scale;
    public static int ViewportWidth => Math.Max(160, Main.screenWidth / PixelScale);
    public static int ViewportHeight => Math.Max(120, Main.screenHeight / PixelScale);
    public static float PlayerScale => Math.Clamp(ModContent.GetInstance<CampaignViewConfig>().PlayerVisualScale, 1f, 2f);
    public static void ChangeZoom(int delta)
        => ModContent.GetInstance<CampaignViewConfig>().CameraPixelScale = Math.Clamp(PixelScale + delta, 2, 6);

    public static Rectangle OutputRectangle(int width, int height)
        => OutputRectangle(Main.screenWidth, Main.screenHeight, width, height, PixelScale);
    internal static Rectangle OutputRectangle(int screenWidth, int screenHeight, int width, int height, int pixelScale)
        => new((screenWidth - width * pixelScale) / 2, (screenHeight - height * pixelScale) / 2, width * pixelScale, height * pixelScale);

    public static Vector2 WorldScreenPosition(Vector2 nativeCamera, int width, int height)
        => WorldScreenPosition(nativeCamera, Main.screenWidth, Main.screenHeight, OutputRectangle(width, height), HostZoom);
    internal static Vector2 WorldScreenPosition(Vector2 nativeCamera, int screenWidth, int screenHeight, Rectangle output, float zoom)
    {
        Vector2 center = new(screenWidth / 2f, screenHeight / 2f);
        // Terraria zooms around screen center. Retain fractional host pixels so
        // the transformed native origin lands on the exact integer output edge,
        // even at odd resolutions and fractional host zoom factors.
        // SpriteViewMatrix adds a 1/256 host-pixel pretranslation before zoom.
        return CampaignRuntime.Origin + nativeCamera * CampaignRuntime.Scale - center
            + (center - new Vector2(output.X, output.Y)) / zoom + new Vector2(1f / 256f);
    }

    private delegate SamplerState CameraSampler(Camera camera);
    private delegate SamplerState MountedSampler();
    private delegate void DrawSpriteRange(SpriteDrawBuffer buffer, int index, int count);
    private static void DrawPixelSprites(DrawSpriteRange original, SpriteDrawBuffer buffer, int index, int count)
    {
        if (!CampaignRuntime.Active) { original(buffer, index, count); return; }
        // Player layers issue direct GPU draws through SpriteDrawBuffer after
        // applying their armor/hair shaders. SpriteBatch.Begin's sampler never
        // reaches this path, so select nearest sampling at the actual draw.
        GraphicsDevice device = Main.graphics.GraphicsDevice;
        SamplerState previous = device.SamplerStates[0];
        try
        {
            device.SamplerStates[0] = SamplerState.PointClamp;
            original(buffer, index, count);
        }
        finally { device.SamplerStates[0] = previous; }
    }
    public override void Load()
    {
        if (Main.dedServ) return;
        try
        {
            samplingHooks.Add(new Hook(typeof(Camera).GetProperty(nameof(Camera.Sampler))!.GetMethod!,
                (Func<CameraSampler, Camera, SamplerState>)((original, camera) => CampaignRuntime.Active ? SamplerState.PointClamp : original(camera))));
            samplingHooks.Add(new Hook(typeof(LegacyPlayerRenderer).GetProperty(nameof(LegacyPlayerRenderer.MountedSamplerState))!.GetMethod!,
                (Func<MountedSampler, SamplerState>)(original => CampaignRuntime.Active ? SamplerState.PointClamp : original())));
            samplingHooks.Add(new Hook(typeof(SpriteDrawBuffer).GetMethod(nameof(SpriteDrawBuffer.DrawRange))!,
                (Action<DrawSpriteRange, SpriteDrawBuffer, int, int>)DrawPixelSprites));
        }
        catch { Unload(); throw; }
    }
    public override void Unload()
    {
        foreach (Hook hook in samplingHooks) hook.Dispose();
        samplingHooks.Clear();
    }
}
