using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.Graphics;
using Terraria.Graphics.Renderers;
using Terraria.ModLoader;

namespace Caverarria;

public sealed class CampaignView : ModSystem
{
    private readonly List<Hook> samplingHooks = new();
    public static bool InWorld => !Main.gameMenu && CampaignRuntime.Active;
    public static int BasePixelScale => Math.Clamp(ModContent.GetInstance<CampaignViewConfig>().CameraPixelScale, 2, 6);
    // Keep native pixels crisp while honoring Terraria's world zoom slider.
    // The slider selects whole-pixel steps instead of stretching individual pixels.
    public static int PixelScale => EffectivePixelScale(BasePixelScale, Main.GameZoomTarget);
    internal static int EffectivePixelScale(int baseScale, float worldZoom)
        => (int)MathF.Round(Math.Clamp(baseScale, 2, 6) * Math.Clamp(worldZoom, 1f, 2f));
    public static float HostZoom => PixelScale / CampaignRuntime.Scale;
    public static int ViewportWidth => Math.Max(160, Main.screenWidth / PixelScale);
    public static int ViewportHeight => Math.Max(120, Main.screenHeight / PixelScale);
    public static float PlayerScale => Math.Clamp(ModContent.GetInstance<CampaignViewConfig>().PlayerVisualScale, 1f, 2f);
    internal static bool IsCampaignAvatar(Player player)
        => InWorld && IsWorldPlayer(player, Main.player) && player.GetModPlayer<CampaignPlayer>().UsingCampaignHealth;
    internal static bool IsWorldPlayer(Player player, Player[] players)
        => player.whoAmI >= 0 && player.whoAmI < players.Length && ReferenceEquals(players[player.whoAmI], player);

    public static Rectangle OutputRectangle(int width, int height)
        => OutputRectangle(Main.screenWidth, Main.screenHeight, width, height, PixelScale);
    public static Rectangle InterfaceRectangle(int width, int height)
        => InterfaceRectangle(Main.screenWidth, Main.screenHeight, width, height, PixelScale);
    internal static Rectangle InterfaceRectangle(int screenWidth, int screenHeight, int width, int height, int worldScale)
    {
        int scale = Math.Max(1, Math.Min(worldScale, Math.Min(screenWidth / width, screenHeight / height)));
        return OutputRectangle(screenWidth, screenHeight, width, height, scale);
    }
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
        if (!InWorld) { original(buffer, index, count); return; }
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
                (Func<CameraSampler, Camera, SamplerState>)((original, camera) => InWorld ? SamplerState.PointClamp : original(camera))));
            samplingHooks.Add(new Hook(typeof(LegacyPlayerRenderer).GetProperty(nameof(LegacyPlayerRenderer.MountedSamplerState))!.GetMethod!,
                (Func<MountedSampler, SamplerState>)(original => InWorld ? SamplerState.PointClamp : original())));
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
