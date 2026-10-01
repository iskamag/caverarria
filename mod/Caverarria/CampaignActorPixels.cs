using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Rasterize the host world actors onto the same pixel grid as the guest scene.</summary>
public sealed class CampaignActorPixels : ModSystem
{
    private Hook? finishHook;
    private static RenderTarget2D? fullImage, pixelImage;
    private static CaptureState? capture;
    private static readonly MethodInfo usageSetter = typeof(RenderTarget2D)
        .GetProperty(nameof(RenderTarget2D.RenderTargetUsage))!.GetSetMethod(nonPublic: true)!;
    private sealed class CaptureState
    {
        public required GraphicsDevice Device;
        public required RenderTarget2D FullImage, PixelImage;
        public required RenderTargetBinding[] Targets;
        public required RenderTargetUsage[] TargetUsage;
        public RenderTargetUsage BackbufferUsage;
        public Viewport Viewport;
        public Rectangle Scissor, Output;
        public required BlendState Blend;
        public required DepthStencilState Depth;
        public required RasterizerState Rasterizer;
        public required SamplerState Sampler;
    }
    private delegate void OriginalFinish(Main main);
    public override void Load()
    {
        if (Main.dedServ) return;
        MethodInfo method = typeof(Main).GetMethod("DrawInfernoRings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        finishHook = new Hook(method, (Action<OriginalFinish, Main>)FinishWorld);
    }
    private static void FinishWorld(OriginalFinish original, Main main)
    {
        original(main);
        if (capture == null) return;
        // Main calls this with its deferred world batch open, before UI batches.
        Main.spriteBatch.End();
        try { Composite(); }
        finally
        {
            Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, Main.DefaultSamplerState,
                DepthStencilState.None, Main.Rasterizer, null, Main.GameViewMatrix.TransformationMatrix);
        }
    }
    public static void Begin()
    {
        Abort();
        if (!CampaignView.InWorld || CampaignRuntime.Engine == null) return;
        GraphicsDevice device = Main.graphics.GraphicsDevice;
        int width = Main.screenWidth, height = Main.screenHeight;
        int pixelsWide = CampaignRuntime.Engine.Width, pixelsHigh = CampaignRuntime.Engine.Height;
        EnsureTarget(ref fullImage, device, width, height);
        EnsureTarget(ref pixelImage, device, pixelsWide, pixelsHigh);
        var targets = device.GetRenderTargets();
        var state = new CaptureState
        {
            Device = device, FullImage = fullImage!, PixelImage = pixelImage!, Targets = targets,
            TargetUsage = targets.Select(binding => ((RenderTarget2D)binding.RenderTarget).RenderTargetUsage).ToArray(),
            BackbufferUsage = device.PresentationParameters.RenderTargetUsage,
            Viewport = device.Viewport, Scissor = device.ScissorRectangle,
            Output = CampaignView.OutputRectangle(pixelsWide, pixelsHigh),
            Blend = device.BlendState, Depth = device.DepthStencilState,
            Rasterizer = device.RasterizerState, Sampler = device.SamplerStates[0]
        };
        capture = state;
        try
        {
            // FNA explicitly clears a DiscardContents destination when it is rebound.
            // Preserve only while temporarily switching away; restore its policy afterward.
            if (targets.Length == 0) device.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            else foreach (var binding in targets) SetUsage((RenderTarget2D)binding.RenderTarget, RenderTargetUsage.PreserveContents);
            device.SetRenderTarget(fullImage);
            device.Clear(Color.Transparent);
        }
        catch { Abort(); throw; }
    }
    private static void EnsureTarget(ref RenderTarget2D? target, GraphicsDevice device, int width, int height)
    {
        if (target != null && !target.IsDisposed && target.GraphicsDevice == device && target.Width == width && target.Height == height) return;
        target?.Dispose();
        target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
    }
    private static void SetUsage(RenderTarget2D target, RenderTargetUsage usage) => usageSetter.Invoke(target, new object[] { usage });
    // Caller must have ended its batch. Neither this nor Begin modifies world/input coordinates.
    public static void Composite()
    {
        CaptureState? state = capture;
        if (state == null) return;
        try
        {
            state.Device.SetRenderTarget(state.PixelImage);
            state.Device.Clear(Color.Transparent);
            Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            try { Main.spriteBatch.Draw(state.FullImage, new Rectangle(0, 0, state.PixelImage.Width, state.PixelImage.Height), state.Output, Color.White); }
            finally { Main.spriteBatch.End(); }
            RestoreDestination(state);
            Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            try { Main.spriteBatch.Draw(state.PixelImage, state.Output, Color.White); }
            finally { Main.spriteBatch.End(); }
        }
        finally { Abort(); }
    }
    private static void RestoreDestination(CaptureState state)
    {
        state.Device.SetRenderTargets(state.Targets);
        state.Device.Viewport = state.Viewport;
        state.Device.ScissorRectangle = state.Scissor;
    }
    public static void Abort()
    {
        CaptureState? state = capture;
        if (state == null) return;
        capture = null;
        try { RestoreDestination(state); }
        finally
        {
            state.Device.PresentationParameters.RenderTargetUsage = state.BackbufferUsage;
            for (int i = 0; i < state.Targets.Length; i++) SetUsage((RenderTarget2D)state.Targets[i].RenderTarget, state.TargetUsage[i]);
            state.Device.BlendState = state.Blend;
            state.Device.DepthStencilState = state.Depth;
            state.Device.RasterizerState = state.Rasterizer;
            state.Device.SamplerStates[0] = state.Sampler;
        }
    }
    public static void DisposeTargets()
    {
        // Detach now: delayed disposal must never touch a later session's targets.
        RenderTarget2D? oldFull = fullImage, oldPixels = pixelImage;
        fullImage = pixelImage = null;
        Main.QueueMainThreadAction(() => { oldFull?.Dispose(); oldPixels?.Dispose(); });
    }
    public override void Unload()
    {
        finishHook?.Dispose(); finishHook = null;
        Abort();
        DisposeTargets();
    }
}
