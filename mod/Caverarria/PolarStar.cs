using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Real Terraria held items and use animations trigger the original campaign weapons.</summary>
public abstract class CaveGun : ModItem
{
    public abstract int NativeType { get; }
    public virtual bool ContinuousFire => NativeType is 4 or 7 or 13;
    public override string Texture => "Caverarria/Assets/Weapons/" + GetType().Name;
    public override void SetDefaults()
    {
        Item.width = 32; Item.height = 20;
        Item.damage = 10;
        Item.DamageType = DamageClass.Ranged;
        Item.useTime = Item.useAnimation = 10;
        Item.useStyle = ItemUseStyleID.Shoot;
        Item.noMelee = true;
        Item.knockBack = 1;
        Item.value = 0;
        Item.rare = ItemRarityID.Blue;
        Item.autoReuse = true;
        Item.shoot = ProjectileID.Bullet;
        Item.shootSpeed = 14f;
    }
    public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position, Vector2 velocity, int type, int damage, float knockback)
    {
        CampaignRuntime.Shot();
        return false;
    }
    public override bool CanUseItem(Player player) => CampaignRuntime.Active && CampaignRuntime.ControlsEnabled;
    public override Vector2? HoldoutOffset() => new Vector2(-3, 0);
    public override void ModifyTooltips(List<TooltipLine> tooltips)
    {
        tooltips.Add(new TooltipLine(Mod, "CampaignWeapon", "Fires only in a Cave Story campaign"));
        var weapon = CampaignRuntime.Snapshot.Field("weapons").Elements().FirstOrDefault(value => value.Integer("id") == NativeType);
        if (weapon.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            tooltips.Add(new TooltipLine(Mod, "WeaponLevel", $"Level {weapon.Integer("level")}"));
            tooltips.Add(new TooltipLine(Mod, "WeaponExperience", $"XP: {weapon.Integer("experience")}"));
            if (weapon.Integer("max_ammo") > 0) tooltips.Add(new TooltipLine(Mod, "WeaponAmmo", $"Ammo: {weapon.Integer("ammo")} / {weapon.Integer("max_ammo")}"));
        }
    }
    public static int ItemFor(int id) => id switch
    {
        1 => ModContent.ItemType<Snake>(), 2 => ModContent.ItemType<PolarStar>(), 3 => ModContent.ItemType<Fireball>(),
        4 => ModContent.ItemType<MachineGun>(), 5 => ModContent.ItemType<MissileLauncher>(), 7 => ModContent.ItemType<Bubbler>(),
        9 => ModContent.ItemType<Blade>(), 10 => ModContent.ItemType<SuperMissileLauncher>(),
        12 => ModContent.ItemType<Nemesis>(), 13 => ModContent.ItemType<Spur>(), _ => 0
    };
}
public sealed class Snake : CaveGun { public override int NativeType => 1; }
public sealed class PolarStar : CaveGun { public override int NativeType => 2; }
public sealed class Fireball : CaveGun { public override int NativeType => 3; }
public sealed class MachineGun : CaveGun { public override int NativeType => 4; }
public sealed class MissileLauncher : CaveGun { public override int NativeType => 5; }
public sealed class Bubbler : CaveGun { public override int NativeType => 7; }
public sealed class Blade : CaveGun { public override int NativeType => 9; }
public sealed class SuperMissileLauncher : CaveGun { public override int NativeType => 10; }
public sealed class Nemesis : CaveGun { public override int NativeType => 12; }
public sealed class Spur : CaveGun { public override int NativeType => 13; }

public sealed class CampaignProjectiles : GlobalProjectile
{
    public override bool OnTileCollide(Projectile projectile, Vector2 oldVelocity)
    {
        // Bomb bounces are not native bullet impacts (which play a hit sound).
        // Vanilla owns their fuse, bounce physics and explosion tile sweep.
        if (!ProjectileID.Sets.Explosive[projectile.type] && projectile.aiStyle != ProjAIStyleID.Explosive)
            CampaignRuntime.QueueTerrainHit(projectile, oldVelocity);
        return true;
    }
}
