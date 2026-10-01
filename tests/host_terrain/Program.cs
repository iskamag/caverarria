using System.Reflection;
using System.Text.Json;
using Caverarria;
using Terraria;
using Terraria.ID;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
var engineProperty = typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Engine))!;
var snapshotProperty = typeof(CampaignRuntime).GetProperty(nameof(CampaignRuntime.Snapshot))!;
string temporary = Path.Combine(Path.GetTempPath(), "caverarria-terrain-probe-" + Guid.NewGuid().ToString("N"));
string? oldOverride = Environment.GetEnvironmentVariable("CAVERARRIA_SAVE");
var oldEngine = CampaignRuntime.Engine;
var oldSnapshot = CampaignRuntime.Snapshot;
Directory.CreateDirectory(temporary);
try
{
    Environment.SetEnvironmentVariable("CAVERARRIA_SAVE", temporary);
    CampaignBootstrap.MarkCampaignWorld("terrain-probe");
    engineProperty.SetValue(null, new InertEngine());
    snapshotProperty.SetValue(null, JsonSerializer.SerializeToElement(new { scene = "game", control_enabled = true, script_mode = "Map", stage = new { id = 3, width = 20, height = 10 } }));
    int ox = CampaignRuntime.OriginTileX, oy = CampaignRuntime.OriginTileY;
    Check(CampaignTerrainEdits.Cell(ox, oy, out int x, out int y) && x == 0 && y == 0, "first native cell origin");
    Check(CampaignTerrainEdits.Cell(ox + 2, oy + 2, out x, out y) && x == 0 && y == 0, "three host tiles share one native cell");
    Check(CampaignTerrainEdits.Cell(ox + 3, oy + 3, out x, out y) && x == 1 && y == 1, "next cell boundary");
    Check(!CampaignTerrainEdits.Cell(ox - 1, oy, out _, out _), "left boundary protected");
    Check(!CampaignTerrainEdits.Cell(ox, oy - 1, out _, out _), "top boundary protected");
    Check(!CampaignTerrainEdits.Cell(ox + 60, oy, out _, out _), "right boundary protected");
    Check(!CampaignTerrainEdits.Cell(ox, oy + 30, out _, out _), "bottom boundary protected");
    Check(CampaignTerrainEdits.CanMine(ox, oy), "ordinary campaign mining permitted");
    var protection = new CampaignTerrainProtection();
    Check(!protection.CanExplode(ox, oy, TileID.Stone), "explosives cannot desynchronize guest collision");
    Check(!protection.CanReplace(ox, oy, TileID.Stone, TileID.Dirt), "block swap cannot bypass whole-cell transactions");
    Check(!protection.Slope(ox, oy, TileID.Stone), "hammer cannot desynchronize guest collision");
    snapshotProperty.SetValue(null, JsonSerializer.SerializeToElement(new { control_enabled = false, stage = new { width = 20, height = 10 } }));
    Check(!CampaignTerrainEdits.CanMine(ox, oy), "script control lock blocks mining");
    Check(!CampaignTerrainEdits.CanPlace(ox, oy, TileID.Stone), "script control lock blocks placement");
    bool AnyReach(int i, int j) => true;
    bool LeftWall(int i, int j) => i == 9 && j >= 20 && j <= 22;
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, LeftWall, AnyReach, out int snappedX, out int snappedY)
        && snappedX == 10 && snappedY == 21, "center cursor attaches at nearest cell edge against left native wall");
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, (i, j) => j == 23, AnyReach, out snappedX, out snappedY)
        && snappedX == 11 && snappedY == 22, "center cursor attaches against native floor");
    Check(!CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, (i, j) => false, AnyReach, out snappedX, out snappedY)
        && snappedX == 11 && snappedY == 21, "floating cell without native neighbors cannot attach");
    Check(!CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, (i, j) => i == 9 && j == 19, AnyReach, out _, out _),
        "diagonal native neighbor cannot attach");
    Check(!CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, LeftWall, (i, j) => i >= 11, out _, out _),
        "snapping does not extend ordinary placement reach");
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, LeftWall, (i, j) => j == 22, out snappedX, out snappedY)
        && snappedX == 10 && snappedY == 22, "nearest eligible edge selected when preferred tile is out of reach");
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 10, 20, LeftWall, AnyReach, out snappedX, out snappedY)
        && snappedX == 10 && snappedY == 20, "already valid edge target remains under mouse");
    var reachPosition = new Microsoft.Xna.Framework.Vector2(160.5f, 320.5f);
    Check(!CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 4, 20), "fractional left range bound matches vanilla");
    Check(CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 5, 20), "nearest left in-range tile retained");
    Check(CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 16, 20), "rightmost reachable tile matches vanilla minus-one adjustment");
    Check(!CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 17, 20), "right range cannot overshoot vanilla");
    Check(CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 10, 26), "bottommost reachable tile matches vanilla minus-two adjustment");
    Check(!CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 10, 27), "bottom range cannot overshoot vanilla");
    Check(!CampaignTerrainEdits.PlacementInReach(reachPosition, 20, 42, 6, 6, 10, 14), "fractional top range bound matches vanilla");
    // Load/save actual production metadata with two stages, then verify new-game cleanup.
    string metadata = Path.Combine(temporary, CampaignTerrainEdits.FileName);
    File.WriteAllText(metadata, "[{\"Stage\":3,\"X\":2,\"Y\":4,\"Tile\":1,\"Item\":1,\"Style\":0,\"FrameX\":18,\"FrameY\":0},{\"Stage\":4,\"X\":2,\"Y\":4,\"Tile\":1,\"Item\":1,\"Style\":0,\"FrameX\":18,\"FrameY\":0}]");
    CampaignTerrainEdits.ClearSession();
    typeof(CampaignTerrainEdits).GetMethod("EnsureLoaded", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
    typeof(CampaignTerrainEdits).GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
    using (var data = JsonDocument.Parse(File.ReadAllText(metadata)))
    {
        Check(data.RootElement.GetArrayLength() == 2, "same cell in different stages persisted independently");
        Check(data.RootElement[0].GetProperty("FrameX").GetInt32() == 18, "placed block source art retained");
    }
    Check(!File.Exists(metadata + ".tmp"), "atomic metadata write leaves no temporary file");
    CampaignTerrainEdits.ClearNewGame();
    Check(File.ReadAllText(metadata) == "[]", "fresh campaign removes obsolete placed item metadata");
    CampaignBootstrap.ClearWorldMarker();
    Check(protection.CanPlace(ox, oy, TileID.Stone), "ordinary world placement preserved");
    bool damaged = false;
    Check(protection.CanKillTile(ox, oy, TileID.Stone, ref damaged), "ordinary world mining preserved");
    Check(protection.Slope(ox, oy, TileID.Stone), "ordinary world hammer preserved");
    Check(protection.CanExplode(ox, oy, TileID.Stone), "ordinary world explosives preserved");
}
finally
{
    CampaignTerrainEdits.ClearSession(); CampaignBootstrap.ClearWorldMarker();
    engineProperty.SetValue(null, oldEngine); snapshotProperty.SetValue(null, oldSnapshot);
    Environment.SetEnvironmentVariable("CAVERARRIA_SAVE", oldOverride);
    Directory.Delete(temporary, true);
}
Console.WriteLine($"{checks} production terrain host contract checks passed (synthetic, no hosted mining/placement).");
sealed class InertEngine : ICampaignEngine
{
    public int Width => 320; public int Height => 240;
    public bool HasPcmAudio => false; public int AudioSampleRate => 48000;
    public JsonElement Send(object request) => throw new Exception("unexpected guest mutation");
    public JsonElement Resize(int width, int height) => default;
    public void CopyPixels(int layer, byte[] destination) { }
    public void ReadAudio(byte[] destination) { }
    public void Dispose() { }
}
