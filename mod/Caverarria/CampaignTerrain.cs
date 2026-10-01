using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Collision geometry only. Original Cave Story art is rendered by its engine.</summary>
public sealed class CampaignSolid : ModTile
{
    public override string Texture => "Terraria/Images/Tiles_0";
    public override void SetStaticDefaults()
    {
        Main.tileSolid[Type] = true;
        Main.tileBlockLight[Type] = false;
        Main.tileLighted[Type] = true;
    }
    public override bool PreDraw(int i, int j, SpriteBatch spriteBatch) => false;
    public override bool CanKillTile(int i, int j, ref bool blockDamaged) { blockDamaged = false; return false; }
    public override bool CanExplode(int i, int j) => false;
}

public sealed class CampaignTerrainProtection : GlobalTile
{
    public override bool CanPlace(int i, int j, int type) => !CampaignRuntime.Active;
    public override bool CanKillTile(int i, int j, int type, ref bool blockDamaged)
    {
        if (!CampaignRuntime.Active) return true;
        blockDamaged = false;
        return false;
    }
    public override bool CanExplode(int i, int j, int type) => !CampaignRuntime.Active;
}
