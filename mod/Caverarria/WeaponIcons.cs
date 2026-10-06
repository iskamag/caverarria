using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>
/// Original Cave Story weapon icons for the inventory slots. The guns keep
/// their held-gun sprite as their item texture, so the icon is drawn from a
/// separate 32x32 texture at the same scale vanilla uses for items.
/// </summary>
internal static class WeaponIcons
{
    private const string Root = "Caverarria/Assets/WeaponIcons/";
    private static readonly Dictionary<int, Asset<Texture2D>> assets = new();

    public static void Load()
    {
        foreach (int nativeType in new[] { 1, 2, 3, 4, 5, 7, 9, 10, 12, 13 })
            assets[nativeType] = ModContent.Request<Texture2D>(Root + Name(nativeType));
    }

    public static void Unload() => assets.Clear();

    private static string Name(int nativeType) => nativeType switch
    {
        1 => "Snake", 2 => "PolarStar", 3 => "Fireball", 4 => "MachineGun", 5 => "MissileLauncher",
        7 => "Bubbler", 9 => "Blade", 10 => "SuperMissileLauncher", 12 => "Nemesis", 13 => "Spur",
        _ => throw new ArgumentOutOfRangeException(nameof(nativeType), nativeType, "No campaign weapon icon"),
    };

    /// <summary>Draws the 32x32 icon centred on the slot at vanilla's item scale.</summary>
    public static bool Draw(SpriteBatch spriteBatch, Vector2 position, int nativeType, Color color)
    {
        if (!assets.TryGetValue(nativeType, out Asset<Texture2D>? asset)) return false;
        Texture2D texture = asset.Value;
        spriteBatch.Draw(texture, position, null, color, 0f, texture.Size() / 2f, Main.inventoryScale, SpriteEffects.None, 0f);
        return true;
    }
}
