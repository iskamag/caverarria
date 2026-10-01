using System.Reflection;
using System.Text.Json;
using Caverarria;

internal static class SnapshotMapChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        PropertyInfo snapshot = typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Snapshot))!;
        FieldInfo pending = typeof(CampaignRuntime).GetField("pendingMap", flags)!;
        JsonElement previousSnapshot = CampaignRuntime.Snapshot;
        JsonElement previousPending = (JsonElement)pending.GetValue(null)!;
        JsonElement previousMap = CampaignRuntime.CurrentMap;
        try
        {
            pending.SetValue(null, default(JsonElement));
            // A fresh/load result emits its map once. The immediately following
            // silent-audio command emits only state, before map reconciliation.
            using (JsonDocument initial = JsonDocument.Parse("{\"map\":{\"name\":\"Start Point\",\"width\":1,\"height\":1,\"tiles\":[1]},\"scene\":\"game\"}"))
                snapshot.SetValue(null, initial.RootElement);
            snapshot.SetValue(null, JsonSerializer.SerializeToElement(new { scene = "game", audio = new { enabled = false } }));
            JsonElement retained = (JsonElement)pending.GetValue(null)!;
            check(retained.ValueKind == JsonValueKind.Object && retained.GetProperty("name").GetString() == "Start Point",
                "silent startup audio response discarded the unreconciled room map");
            check(retained.GetProperty("tiles")[0].GetInt32() == 1,
                "pending map depended on a disposed source snapshot document");
            snapshot.SetValue(null, JsonSerializer.SerializeToElement(new { scene = "game", hit = new { accepted = true }, map = (object?)null }));
            check(((JsonElement)pending.GetValue(null)!).GetRawText() == retained.GetRawText(),
                "hit response with no room delta discarded the pending map");
            snapshot.SetValue(null, JsonSerializer.SerializeToElement(new { scene = "game", map = new { name = "First Cave", width = 2, height = 1, tiles = new[] { 2, 3 } } }));
            snapshot.SetValue(null, JsonSerializer.SerializeToElement(new { scene = "game", audio = new { enabled = true } }));
            JsonElement newer = (JsonElement)pending.GetValue(null)!;
            check(newer.GetProperty("name").GetString() == "First Cave" && newer.GetProperty("width").GetInt32() == 2 && newer.GetProperty("tiles")[1].GetInt32() == 3,
                "newer room delta did not replace the pending earlier room before reconciliation");
            check(CampaignRuntime.CurrentMap.Equals(previousMap),
                "snapshot assignment published a room map before its collision projection");
        }
        finally
        {
            snapshot.SetValue(null, previousSnapshot);
            pending.SetValue(null, previousPending);
        }
    }
}
