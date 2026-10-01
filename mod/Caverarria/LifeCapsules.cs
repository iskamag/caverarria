using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace Caverarria;

/// <summary>Capsules upgrade the character immediately, once per campaign milestone.</summary>
public sealed class LifeCapsulePlayer : ModPlayer
{
    private int bonusHealth;
    private readonly Dictionary<string, int> rewardedMaximums = new();

    public override void ModifyMaxStats(out StatModifier health, out StatModifier mana)
    {
        base.ModifyMaxStats(out health, out mana);
        health.Base += bonusHealth;
    }

    internal int ObserveMaximum(string campaign, int maximum)
    {
        if (string.IsNullOrEmpty(campaign)) return 0;
        // Original Cave Story starts at 3 HP. Restoring an older checkpoint must
        // not award an already collected capsule again. Old alpha saves migrate
        // their earned capacity on first entry with this version.
        int previous = rewardedMaximums.GetValueOrDefault(campaign, 3);
        if (maximum <= previous) return 0;
        int upgrade = checked((maximum - previous) * 10);
        rewardedMaximums[campaign] = maximum;
        bonusHealth = checked(bonusHealth + upgrade);
        return upgrade;
    }

    public override void SaveData(TagCompound tag)
    {
        tag["capsuleBonusHealth"] = bonusHealth;
        var campaigns = new TagCompound();
        foreach (var record in rewardedMaximums) campaigns[record.Key] = record.Value;
        tag["capsuleCampaignMaximums"] = campaigns;
    }

    public override void LoadData(TagCompound tag)
    {
        bonusHealth = Math.Max(0, tag.GetInt("capsuleBonusHealth"));
        rewardedMaximums.Clear();
        foreach (var record in tag.GetCompound("capsuleCampaignMaximums"))
            if (record.Value is int value && value >= 3) rewardedMaximums[record.Key] = value;
    }
}
