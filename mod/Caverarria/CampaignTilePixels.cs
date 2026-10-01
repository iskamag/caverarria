using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Caverarria;

/// <summary>Sample Terraria's doubled sprite texels once, before camera or zoom transforms.</summary>
internal static class CampaignTilePixels
{
    private static readonly Dictionary<(Texture2D texture, Rectangle source), RenderTarget2D> frames = new();
    private static readonly MethodInfo usageSetter = typeof(RenderTarget2D)
        .GetProperty(nameof(RenderTarget2D.RenderTargetUsage))!.GetSetMethod(nonPublic: true)!;
    internal static Point LogicalSize(Rectangle source) => new((source.Width + 1) / 2, (source.Height + 1) / 2);
    internal static Vector2 CameraOrigin(Vector2 nativeCamera)
        => new(MathF.Floor(nativeCamera.X), MathF.Floor(nativeCamera.Y));
    internal static Vector2 Position(Vector2 hostWorldPosition, Vector2 nativeCamera)
    {
        Vector2 world = (hostWorldPosition - CampaignRuntime.Origin) / CampaignRuntime.Scale;
        // Guest fix9_scale floors the camera to its raster grid. Snap the static
        // material independently so moving the camera never resamples its texels.
        return new Vector2(MathF.Floor(world.X), MathF.Floor(world.Y))
            - CameraOrigin(nativeCamera);
    }
    internal static Texture2D Get(Texture2D texture, Rectangle source) => frames[(texture, source)];
    // No caller SpriteBatch may be open. Frame data remains on the GPU.
    internal static Texture2D Prepare(Texture2D texture, Rectangle source)
    {
        var key = (texture, source);
        if (frames.TryGetValue(key, out var cached) && !cached.IsDisposed) return cached;
        GraphicsDevice device = texture.GraphicsDevice;
        Point size = LogicalSize(source);
        var target = new RenderTarget2D(device, size.X, size.Y, false, SurfaceFormat.Color,
            DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        var targets = device.GetRenderTargets();
        var usage = targets.Select(binding => ((RenderTarget2D)binding.RenderTarget).RenderTargetUsage).ToArray();
        var backbufferUsage = device.PresentationParameters.RenderTargetUsage;
        var viewport = device.Viewport; var scissor = device.ScissorRectangle;
        var blend = device.BlendState; var depth = device.DepthStencilState;
        var rasterizer = device.RasterizerState; var sampler = device.SamplerStates[0];
        bool complete = false;
        try
        {
            if (targets.Length == 0) device.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            else foreach (var binding in targets) SetUsage((RenderTarget2D)binding.RenderTarget, RenderTargetUsage.PreserveContents);
            device.SetRenderTarget(target); device.Clear(Color.Transparent);
            // Reuse the host batch while it is closed. Disposing a temporary
            // batch would leave its effect and buffers bound on the shared
            // device until another draw rebinds them.
            SpriteBatch batch = Main.spriteBatch;
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            try { batch.Draw(texture, new Rectangle(0, 0, size.X, size.Y), source, Color.White); }
            finally { batch.End(); }
            frames[key] = target;
            complete = true;
            return target;
        }
        finally
        {
            try { device.SetRenderTargets(targets); device.Viewport = viewport; device.ScissorRectangle = scissor; }
            finally
            {
                device.PresentationParameters.RenderTargetUsage = backbufferUsage;
                for (int i = 0; i < targets.Length; i++) SetUsage((RenderTarget2D)targets[i].RenderTarget, usage[i]);
                device.BlendState = blend; device.DepthStencilState = depth;
                device.RasterizerState = rasterizer; device.SamplerStates[0] = sampler;
                if (!complete) target.Dispose();
            }
        }
    }
    private static void SetUsage(RenderTarget2D target, RenderTargetUsage usage) => usageSetter.Invoke(target, new object[] { usage });
    internal static void Capture(string directory)
    {
        int index = 0;
        foreach (var pair in frames)
        {
            var frame = pair.Value;
            var pixels = new Color[frame.Width * frame.Height]; frame.GetData(pixels);
            File.WriteAllBytes(Path.Combine(directory, $"tile-frame-{index}-{frame.Width}x{frame.Height}.rgba"),
                pixels.SelectMany(pixel => new[] { pixel.R, pixel.G, pixel.B, pixel.A }).ToArray());
            index++;
        }
    }
    public static void DisposeFrames()
    {
        var old = frames.Values.ToArray(); frames.Clear();
        if (old.Length == 0) return;
        Main.QueueMainThreadAction(() => { foreach (var frame in old) frame.Dispose(); });
    }
}
