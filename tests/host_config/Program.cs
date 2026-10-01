using Caverarria;

string root = Path.Combine(Path.GetTempPath(), "caverarria-config-" + Guid.NewGuid().ToString("N"));
try
{
    string current = Path.Combine(root, "current"), other = Path.Combine(root, "other");
    Directory.CreateDirectory(current); Directory.CreateDirectory(other);
    byte[] checkpoint = [0, 1, 42, 255];
    File.WriteAllBytes(Path.Combine(current, "Profile.dat"), checkpoint);
    File.WriteAllText(Path.Combine(current, "290.rec"), "record");
    File.WriteAllText(Path.Combine(other, "Profile.dat"), "other world");
    string? backup = CampaignCheckpointBackup.Archive(current);
    Check(backup != null && File.ReadAllBytes(backup).SequenceEqual(checkpoint), "checkpoint bytes preserved");
    Check(!File.Exists(Path.Combine(current, "Profile.dat")), "checkpoint no longer loads");
    Check(File.ReadAllText(Path.Combine(current, "290.rec")) == "record", "record preserved");
    Check(File.ReadAllText(Path.Combine(other, "Profile.dat")) == "other world", "other campaign preserved");
    Check(CampaignCheckpointBackup.Archive(current) == null, "already fresh reset leaves backups intact");
    Check(CampaignCheckpointBackup.Archive(Path.Combine(root, "missing")) == null, "missing checkpoint stays fresh");
    File.WriteAllBytes(Path.Combine(current, "profile.DAT"), [123]);
    string? second = CampaignCheckpointBackup.Archive(current);
    Check(second != backup && File.ReadAllBytes(second!).SequenceEqual(new byte[] { 123 }), "case insensitive unique backup");
    Check(File.ReadAllBytes(backup!).SequenceEqual(checkpoint), "earlier backup preserved across resets");
    Console.WriteLine("8 campaign checkpoint backup checks passed.");
}
finally { Directory.Delete(root, true); }
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
