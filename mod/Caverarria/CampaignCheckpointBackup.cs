namespace Caverarria;

/// <summary>Archive the selected campaign's checkpoint and terrain together.</summary>
internal static class CampaignCheckpointBackup
{
    public static string? Archive(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var profiles = Directory.EnumerateFiles(directory)
            .Where(path => new[] { "Profile.dat", "Terrain.json", "TerrainBlocks.json" }
                .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Equals("Profile.dat", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        if (profiles.Length == 0) return null;
        string suffix = ".reset-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        var moved = new List<(string original, string backup)>();
        try
        {
            foreach (string profile in profiles)
            {
                string backup = profile + suffix;
                File.Move(profile, backup);
                moved.Add((profile, backup));
            }
        }
        catch
        {
            foreach (var (original, backup) in moved.AsEnumerable().Reverse()) File.Move(backup, original);
            throw;
        }
        return moved[0].backup;
    }
}
