using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>The visible body and Terraria's ordinary collision share one feet-anchored scale.</summary>
public sealed class CampaignBody : ModSystem
{
    private readonly List<Hook> hooks = new();
    private delegate void Resize(Player player);
    private delegate Vector2 GetCenter(Player player);
    private delegate void SetCenter(Player player, Vector2 center);
    private delegate void ItemStyle(Player player, float mountOffset, Item item, Rectangle frame);
    private delegate float ItemScale(Player player, Item item);
    private delegate Vector2 HandPosition(Player player, Player.CompositeArmStretchAmount stretch, float rotation);
    private delegate void OriginalHeldLayer(ref PlayerDrawSet drawInfo);
    private delegate void HeldLayer(OriginalHeldLayer original, ref PlayerDrawSet drawInfo);
    [ThreadStatic] private static Player? canonicalPlayer;
    [ThreadStatic] private static Player? heldDrawPlayer;

    internal static Vector2 ScalePoint(Vector2 point, Vector2 feet, float scale) => feet + (point - feet) * scale;
    internal static Vector2 UnscalePoint(Vector2 point, Vector2 feet, float scale) => feet + (point - feet) / scale;
    internal static float MountedOffset(float scale, int height, int heightBoost, float centerOffset)
        => height - scale * (42 + heightBoost) + (scale - 1f) * (21f + centerOffset);
    private static bool ScaledBody(Player player) => player != canonicalPlayer && CampaignView.IsCampaignAvatar(player);
    private static float CenterCorrection(Player player) => ScaledBody(player)
        ? MountedOffset(CampaignView.PlayerScale, player.height, player.HeightOffsetBoost, player.HeightOffsetHitboxCenter) : 0;


    internal static Point Size(float scale, int heightBoost = 0)
        => new((int)MathF.Round(20 * scale), (int)MathF.Round((42 + heightBoost) * scale));

    internal static void SetSize(Player player, int width, int height)
    {
        // Feet stay planted when entering, changing settings, mounting or leaving.
        Vector2 feet = player.Bottom;
        player.width = width; player.height = height;
        player.Bottom = feet;
    }

    internal static void Apply(Player player, bool entering = false)
    {
        if (!(entering ? CampaignRuntime.Active && CampaignView.IsWorldPlayer(player, Main.player)
                && player.GetModPlayer<CampaignPlayer>().UsingCampaignHealth : CampaignView.IsCampaignAvatar(player))) return;
        Point size = Size(CampaignView.PlayerScale, player.HeightOffsetBoost);
        SetSize(player, size.X, size.Y);
    }

    internal static Vector2 ClearPlacement(Vector2 position, int width, int height, Func<Vector2, int, int, bool> overlaps)
    {
        if (!overlaps(position, width, height)) return position;
        // Authored Quote door coordinates can put the enlarged host's feet
        // inside the projected floor. Find the nearest clearance directly above;
        // do not move sideways into another door or search across the room.
        int limit = height + (int)(16 * CampaignRuntime.Scale);
        for (int rise = 1; rise <= limit; rise++)
        {
            Vector2 candidate = position - new Vector2(0, rise);
            if (!overlaps(candidate, width, height)) return candidate;
        }
        return position;
    }

    public override void Load()
    {
        if (Main.dedServ) return;
        try
        {
            hooks.Add(new Hook(typeof(Player).GetMethod("ResizeHitbox", BindingFlags.Instance | BindingFlags.NonPublic)!,
                (Action<Resize, Player>)((original, player) => { original(player); Apply(player); })));
            // Use the same feet-anchored transform for weapon origins and hand queries.
            var center = typeof(Player).GetProperty(nameof(Player.MountedCenter))!;
            hooks.Add(new Hook(center.GetMethod!, (Func<GetCenter, Player, Vector2>)((original, player) =>
                original(player) + new Vector2(0, CenterCorrection(player)))));
            hooks.Add(new Hook(center.SetMethod!, (Action<SetCenter, Player, Vector2>)((original, player, value) =>
                original(player, value - new Vector2(0, CenterCorrection(player))))));
            foreach (string name in new[] { "GetFrontHandPosition", "GetBackHandPosition" })
                hooks.Add(new Hook(typeof(Player).GetMethod(name)!, (Func<HandPosition, Player, Player.CompositeArmStretchAmount, float, Vector2>)ScaledHand));
            foreach (string name in new[] { "ItemCheck_ApplyUseStyle", "ItemCheck_ApplyHoldStyle" })
                hooks.Add(new Hook(typeof(Player).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!,
                    (Action<ItemStyle, Player, float, Item, Rectangle>)ApplyItemStyle));
            hooks.Add(new Hook(typeof(Player).GetMethod(nameof(Player.GetAdjustedItemScale))!,
                (Func<ItemScale, Player, Item, float>)AdjustedItemScale));
            hooks.Add(new Hook(typeof(PlayerDrawLayers).GetMethod(nameof(PlayerDrawLayers.DrawPlayer_27_HeldItem))!,
                (HeldLayer)DrawHeldItem));
        }
        catch { Unload(); throw; }
    }

    private static Vector2 ScaledHand(HandPosition original, Player player, Player.CompositeArmStretchAmount stretch, float rotation)
    {
        Vector2 point = original(player, stretch, rotation);
        return ScaledBody(player) ? ScalePoint(point, player.MountedCenter, CampaignView.PlayerScale) : point;
    }
    private static float AdjustedItemScale(ItemScale original, Player player, Item item)
    {
        float scale = original(player, item);
        return ScaledBody(player) && player != heldDrawPlayer ? scale * CampaignView.PlayerScale : scale;
    }
    private static void ApplyItemStyle(ItemStyle original, Player player, float mountOffset, Item item, Rectangle frame)
    {
        if (!ScaledBody(player)) { original(player, mountOffset, item, frame); return; }
        WithOrdinaryItemBody(player, CampaignView.PlayerScale, () => original(player, mountOffset, item, frame));
    }
    internal static void WithOrdinaryItemBody(Player player, float scale, Action style)
    {
        Vector2 feet = player.Bottom, oldPosition = player.position;
        int width = player.width, height = player.height;
        int mouseX = Main.mouseX, mouseY = Main.mouseY;
        Player? previous = canonicalPlayer;
        Vector2 previousItemLocation = player.itemLocation;
        bool completed = false;
        canonicalPlayer = player;
        try
        {
            // Vanilla styles contain many fixed 24/38/40-pixel body offsets. Give
            // the ordinary style its ordinary body, then transform its result.
            // Inverse-transform its aiming target too, preserving the aim angle.
            SetSize(player, 20, 42 + player.HeightOffsetBoost);
            Vector2 aim = UnscalePoint(new Vector2(mouseX, mouseY) + Main.screenPosition, feet, scale) - Main.screenPosition;
            Main.mouseX = (int)MathF.Round(aim.X); Main.mouseY = (int)MathF.Round(aim.Y);
            style();
            player.itemLocation = ScalePoint(player.itemLocation, feet, scale);
            completed = true;
        }
        finally
        {
            player.width = width; player.height = height; player.position = oldPosition;
            Main.mouseX = mouseX; Main.mouseY = mouseY;
            canonicalPlayer = previous;
            if (!completed) player.itemLocation = previousItemLocation;
        }
    }
    private static void DrawHeldItem(OriginalHeldLayer original, ref PlayerDrawSet drawInfo)
    {
        Player player = drawInfo.drawPlayer;
        if (drawInfo.headOnlyRender || !CampaignView.IsCampaignAvatar(player)) { original(ref drawInfo); return; }
        Vector2 location = drawInfo.ItemLocation;
        Vector2 feet = drawInfo.Position + player.Size * new Vector2(.5f, 1f);
        Player? previous = heldDrawPlayer;
        heldDrawPlayer = player;
        try
        {
            // itemLocation and melee reach are already physical. The final all-
            // layer draw transform must apply their enlargement exactly once.
            drawInfo.ItemLocation = UnscalePoint(location, feet, CampaignView.PlayerScale);
            original(ref drawInfo);
        }
        finally { drawInfo.ItemLocation = location; heldDrawPlayer = previous; }
    }

    public override void Unload()
    {
        foreach (Hook hook in hooks) hook.Dispose();
        hooks.Clear();
    }
}
