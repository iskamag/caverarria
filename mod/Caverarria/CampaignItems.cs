using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace Caverarria;

/// <summary>A real inventory representation of the original campaign's story item.</summary>
public abstract class CampaignItem : ModItem
{
    public abstract int NativeType { get; }
    public string CampaignId { get; set; } = "";
    public override string Texture => "Caverarria/Assets/Items/" + GetType().Name;
    public override void SetDefaults()
    {
        Item.width = 32; Item.height = 16;
        Item.maxStack = 9999;
        Item.useTime = Item.useAnimation = 20;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.rare = ItemRarityID.Blue;
        Item.value = 0;
    }
    public bool BelongsToCampaign => CampaignRuntime.Active && CampaignId == CampaignBootstrap.CampaignId;
    public override bool CanUseItem(Player player) => BelongsToCampaign && CampaignRuntime.ControlsEnabled && !player.dead;
    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer) return false;
        CampaignRuntime.UseStoryItem(NativeType);
        return true;
    }
    public override bool CanRightClick() => BelongsToCampaign && CampaignRuntime.ControlsEnabled && !Main.LocalPlayer.dead;
    public override void RightClick(Player player) => CampaignRuntime.UseStoryItem(NativeType);
    public override bool ConsumeItem(Player player) => false;
    public override bool CanStack(Item source) => source.ModItem is CampaignItem item && item.CampaignId == CampaignId;
    public override void SaveData(TagCompound tag) => tag["campaignId"] = CampaignId;
    public override void LoadData(TagCompound tag) => CampaignId = tag.GetString("campaignId");
    public override void NetSend(BinaryWriter writer) => writer.Write(CampaignId);
    public override void NetReceive(BinaryReader reader) => CampaignId = reader.ReadString();
    public static int ItemFor(int id) => id switch
    {
        1 => ModContent.ItemType<StoryArthursKey>(),
        2 => ModContent.ItemType<StoryMapSystem>(),
        3 => ModContent.ItemType<StorySantasKey>(),
        4 => ModContent.ItemType<StorySilverLocket>(),
        5 => ModContent.ItemType<StoryBeastFang>(),
        6 => ModContent.ItemType<StoryLifeCapsule>(),
        7 => ModContent.ItemType<StoryIDCard>(),
        8 => ModContent.ItemType<StoryJellyfishJuice>(),
        9 => ModContent.ItemType<StoryRustyKey>(),
        10 => ModContent.ItemType<StoryGumKey>(),
        11 => ModContent.ItemType<StoryGumBase>(),
        12 => ModContent.ItemType<StoryCharcoal>(),
        13 => ModContent.ItemType<StoryExplosive>(),
        14 => ModContent.ItemType<StoryPuppy>(),
        15 => ModContent.ItemType<StoryLifePot>(),
        16 => ModContent.ItemType<StoryCureAll>(),
        17 => ModContent.ItemType<StoryClinicKey>(),
        18 => ModContent.ItemType<StoryBoosterv08>(),
        19 => ModContent.ItemType<StoryArmsBarrier>(),
        20 => ModContent.ItemType<StoryTurbocharge>(),
        21 => ModContent.ItemType<StoryCurlysAirTank>(),
        22 => ModContent.ItemType<StoryNikumaruCounter>(),
        23 => ModContent.ItemType<StoryBoosterv20>(),
        24 => ModContent.ItemType<StoryMimigaMask>(),
        25 => ModContent.ItemType<StoryTeleporterRoomKey>(),
        26 => ModContent.ItemType<StorySuesLetter>(),
        27 => ModContent.ItemType<StoryController>(),
        28 => ModContent.ItemType<StoryBrokenSprinkler>(),
        29 => ModContent.ItemType<StorySprinkler>(),
        30 => ModContent.ItemType<StoryTowRope>(),
        31 => ModContent.ItemType<StoryClayFigureMedal>(),
        32 => ModContent.ItemType<StoryLittleMan>(),
        33 => ModContent.ItemType<StoryMushroomBadge>(),
        34 => ModContent.ItemType<StoryMaPignon>(),
        35 => ModContent.ItemType<StoryCurlysUnderwear>(),
        36 => ModContent.ItemType<StoryAlienMedal>(),
        37 => ModContent.ItemType<StoryChacosLipstick>(),
        38 => ModContent.ItemType<StoryWhimsicalStar>(),
        39 => ModContent.ItemType<StoryIronBond>(),
        _ => 0
    };
}
public sealed class StoryArthursKey : CampaignItem { public override int NativeType => 1; }
public sealed class StoryMapSystem : CampaignItem { public override int NativeType => 2; }
public sealed class StorySantasKey : CampaignItem { public override int NativeType => 3; }
public sealed class StorySilverLocket : CampaignItem { public override int NativeType => 4; }
public sealed class StoryBeastFang : CampaignItem { public override int NativeType => 5; }
public sealed class StoryLifeCapsule : CampaignItem { public override int NativeType => 6; }
public sealed class StoryIDCard : CampaignItem { public override int NativeType => 7; }
public sealed class StoryJellyfishJuice : CampaignItem { public override int NativeType => 8; }
public sealed class StoryRustyKey : CampaignItem { public override int NativeType => 9; }
public sealed class StoryGumKey : CampaignItem { public override int NativeType => 10; }
public sealed class StoryGumBase : CampaignItem { public override int NativeType => 11; }
public sealed class StoryCharcoal : CampaignItem { public override int NativeType => 12; }
public sealed class StoryExplosive : CampaignItem { public override int NativeType => 13; }
public sealed class StoryPuppy : CampaignItem { public override int NativeType => 14; }
public sealed class StoryLifePot : CampaignItem { public override int NativeType => 15; }
public sealed class StoryCureAll : CampaignItem { public override int NativeType => 16; }
public sealed class StoryClinicKey : CampaignItem { public override int NativeType => 17; }
public sealed class StoryBoosterv08 : CampaignItem { public override int NativeType => 18; }
public sealed class StoryArmsBarrier : CampaignItem { public override int NativeType => 19; }
public sealed class StoryTurbocharge : CampaignItem { public override int NativeType => 20; }
public sealed class StoryCurlysAirTank : CampaignItem { public override int NativeType => 21; }
public sealed class StoryNikumaruCounter : CampaignItem { public override int NativeType => 22; }
public sealed class StoryBoosterv20 : CampaignItem { public override int NativeType => 23; }
public sealed class StoryMimigaMask : CampaignItem { public override int NativeType => 24; }
public sealed class StoryTeleporterRoomKey : CampaignItem { public override int NativeType => 25; }
public sealed class StorySuesLetter : CampaignItem { public override int NativeType => 26; }
public sealed class StoryController : CampaignItem { public override int NativeType => 27; }
public sealed class StoryBrokenSprinkler : CampaignItem { public override int NativeType => 28; }
public sealed class StorySprinkler : CampaignItem { public override int NativeType => 29; }
public sealed class StoryTowRope : CampaignItem { public override int NativeType => 30; }
public sealed class StoryClayFigureMedal : CampaignItem { public override int NativeType => 31; }
public sealed class StoryLittleMan : CampaignItem { public override int NativeType => 32; }
public sealed class StoryMushroomBadge : CampaignItem { public override int NativeType => 33; }
public sealed class StoryMaPignon : CampaignItem { public override int NativeType => 34; }
public sealed class StoryCurlysUnderwear : CampaignItem { public override int NativeType => 35; }
public sealed class StoryAlienMedal : CampaignItem { public override int NativeType => 36; }
public sealed class StoryChacosLipstick : CampaignItem { public override int NativeType => 37; }
public sealed class StoryWhimsicalStar : CampaignItem { public override int NativeType => 38; }
public sealed class StoryIronBond : CampaignItem { public override int NativeType => 39; }
