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
    public override bool CanKillTile(int i, int j, ref bool blockDamaged) => CampaignTerrainEdits.CanMine(i, j);
    public override bool CanExplode(int i, int j) => false;
}

public sealed class CampaignTerrainProtection : GlobalTile
{
    public override bool CanPlace(int i, int j, int type) => !CampaignRuntime.Active || CampaignTerrainEdits.CanPlace(i, j, type);
    public override bool CanReplace(int i, int j, int type, int tileTypeBeingPlaced) => !CampaignRuntime.Active;
    public override bool CanKillTile(int i, int j, int type, ref bool blockDamaged)
        => !CampaignRuntime.Active || CampaignTerrainEdits.CanMine(i, j);
    public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem)
    {
        if (CampaignRuntime.Active) CampaignTerrainEdits.Mine(i, j, ref fail, effectOnly, ref noItem);
    }
    public override void PlaceInWorld(int i, int j, int type, Item item)
    {
        if (CampaignRuntime.Active) CampaignTerrainEdits.Place(i, j, type, item);
    }
    public override bool Slope(int i, int j, int type) => !CampaignRuntime.Active;
    public override bool CanExplode(int i, int j, int type) => !CampaignRuntime.Active;
}
