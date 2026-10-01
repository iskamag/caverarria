using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Text.Json;
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
    // Local HP is not a targeting predicate: vulnerable boss parts can have
    // zero HP and forward their damage to the boss core in the guest engine.
    internal static bool IsCombatTarget(JsonElement entity) => entity.Boolean("shootable");

    internal static Rectangle CombatBounds(JsonElement entity)
    {
        // Original bullet collision uses hit_bounds.right on BOTH sides. The
        // left extent belongs to terrain/player collision and may be zero.
        float radius = entity.Number("right");
        Vector2 position = CampaignRuntime.ToWorld(entity.Number("x") - radius, entity.Number("y") - entity.Number("top"));
        return new Rectangle((int)position.X, (int)position.Y,
            Math.Max(4, (int)(radius * 2 * CampaignRuntime.Scale)),
            Math.Max(4, (int)((entity.Number("top") + entity.Number("bottom")) * CampaignRuntime.Scale)));
    }
    public override string Texture => "Terraria/Images/NPC_0";
    public override void SetDefaults()
    {
        NPC.width = NPC.height = 20;
        NPC.lifeMax = 10;
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
    internal bool Owns(int id, bool boss, int epoch, ulong generation)
        => NativeId == id && NativeBoss == boss && Epoch == epoch && Generation == generation;
    // The original simulation owns despawning. Vanilla distance despawning can
    // recycle a proxy slot while its guest actor is still alive off screen.
    public override bool CheckActive() => false;
    public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor) => false;
    public override void AI() { NPC.velocity = Vector2.Zero; NPC.timeLeft = 60; }
    public override bool CheckDead() { NPC.life = 1; return false; }
    public override bool CanHitPlayer(Player target, ref int cooldownSlot) => false;
    public override void OnHitByItem(Player player, Item item, NPC.HitInfo hit, int damageDone) => CampaignRuntime.QueueHit(this, damageDone);
    public override void OnHitByProjectile(Projectile projectile, NPC.HitInfo hit, int damageDone) => CampaignRuntime.QueueHit(this, damageDone);
}
