using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Invisible Terraria combat target for an authoritative original-engine NPC.</summary>
public sealed class CaveEntity : ModNPC
{
    public int NativeId;
    public int Epoch;
    public ulong Generation;
    public bool NativeBoss;
    public override string Texture => "Terraria/Images/NPC_0";
    public override void SetDefaults()
    {
        NPC.width = NPC.height = 20;
        NPC.lifeMax = 1000;
        NPC.damage = 0;
        NPC.defense = 0;
        NPC.noGravity = true;
        NPC.noTileCollide = true;
        NPC.knockBackResist = 0;
        NPC.aiStyle = -1;
        NPC.dontCountMe = true;
        NPC.chaseable = true;
        NPC.npcSlots = 0;
        NPC.dontTakeDamage = false;
    }
    public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor) => false;
    public override void AI() { NPC.velocity = Vector2.Zero; NPC.timeLeft = 60; }
    public override bool CheckDead() { NPC.life = 1; return false; }
    public override bool CanHitPlayer(Player target, ref int cooldownSlot) => false;
    public override void OnHitByItem(Player player, Item item, NPC.HitInfo hit, int damageDone) => CampaignRuntime.QueueHit(this, damageDone);
    public override void OnHitByProjectile(Projectile projectile, NPC.HitInfo hit, int damageDone) => CampaignRuntime.QueueHit(this, damageDone);
}
