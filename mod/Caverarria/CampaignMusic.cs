using Terraria;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Cave Story's Organya player supplies the campaign music.</summary>
public sealed class CampaignMusic : ModSceneEffect
{
    public override int Music => 0;
    public override SceneEffectPriority Priority => SceneEffectPriority.BossHigh;
    public override bool IsSceneEffectActive(Player player) => CampaignRuntime.Active;
}
