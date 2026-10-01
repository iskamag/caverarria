using Caverarria;
using Microsoft.Xna.Framework;
using Terraria;

internal static class BodyChecks
{
    internal static void Run(Player player, Action<bool, string> check)
    {
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        foreach (int boost in new[] { 0, 10 })
        foreach (float offset in new[] { 0f, 4f })
        {
            Point size = CampaignBody.Size(scale, boost);
            Vector2 feet = new(160, 240);
            Vector2 vanillaTop = feet - new Vector2(10, 42 + boost);
            Vector2 physicalTop = feet - new Vector2(size.X / 2f, size.Y);
            Vector2 sourceCenter = vanillaTop + new Vector2(10, 21 + offset);
            Vector2 actualCenter = physicalTop + new Vector2(size.X / 2f, 21 + offset + CampaignBody.MountedOffset(scale, size.Y, boost, offset));
            check(Vector2.Distance(actualCenter, CampaignBody.ScalePoint(sourceCenter, feet, scale)) < .001f,
                "weapon origin disagrees with feet-anchored body at mount offset");
            Vector2 hand = sourceCenter + new Vector2(9, -4);
            Vector2 physicalHand = CampaignBody.ScalePoint(hand, feet, scale);
            check(Vector2.Distance(CampaignBody.UnscalePoint(physicalHand, feet, scale), hand) < .001f,
                "held-item render source double-scales physical hands");
            Vector2 target = new(440, 90);
            Vector2 canonicalTarget = CampaignBody.UnscalePoint(target, feet, scale);
            check(Vector2.Distance(Vector2.Normalize(canonicalTarget - sourceCenter), Vector2.Normalize(target - actualCenter)) < .001f,
                "ordinary item style changed the physical aim direction");
        }
        Vector2 position = player.position, planted = player.Bottom;
        int width = player.width, height = player.height, mouseX = Main.mouseX, mouseY = Main.mouseY;
        Vector2 previousItem = player.itemLocation;
        try
        {
            Main.mouseX = 220; Main.mouseY = 120;
            Vector2 sourceLocation = Vector2.Zero;
            CampaignBody.WithOrdinaryItemBody(player, 1.5f, () =>
            {
                check(player.width == 20 && player.height == 42 && player.Bottom == planted,
                    "ordinary weapon style did not receive its feet-anchored vanilla body");
                sourceLocation = player.position + new Vector2(17, 24);
                player.itemLocation = sourceLocation;
            });
            check(player.position == position && player.width == width && player.height == height,
                "ordinary item style retained its temporary collision geometry");
            check(Main.mouseX == 220 && Main.mouseY == 120,
                "ordinary item style leaked its inverse aiming coordinates");
            check(player.itemLocation == CampaignBody.ScalePoint(sourceLocation, planted, 1.5f),
                "fixed vanilla weapon offsets did not become actual scaled hand locations");
            Vector2 physicalLocation = player.itemLocation;
            try { CampaignBody.WithOrdinaryItemBody(player, 1.5f, () => { player.itemLocation = Vector2.Zero; throw new InvalidOperationException("style fixture"); }); }
            catch (InvalidOperationException) { }
            check(player.position == position && player.width == width && player.height == height && Main.mouseX == 220 && Main.mouseY == 120 && player.itemLocation == physicalLocation,
                "throwing ordinary item style leaked temporary body, mouse or item state");
        }
        finally { Main.mouseX = mouseX; Main.mouseY = mouseY; player.itemLocation = previousItem; }
    }
}
