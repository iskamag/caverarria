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
    FurnitureChecks.Run(temporary, Check);
    Check(CampaignTerrainEdits.Cell(ox, oy, out int x, out int y) && x == 0 && y == 0, "first native cell origin");
    Check(CampaignTerrainEdits.Cell(ox + 1, oy + 1, out x, out y) && x == 0 && y == 0, "two host tiles share one native cell");
    Check(CampaignTerrainEdits.Cell(ox + 2, oy + 2, out x, out y) && x == 1 && y == 1, "next cell boundary");
    Check(!CampaignTerrainEdits.Cell(ox - 1, oy, out _, out _), "left boundary protected");
    Check(!CampaignTerrainEdits.Cell(ox, oy - 1, out _, out _), "top boundary protected");
    Check(!CampaignTerrainEdits.Cell(ox + 40, oy, out _, out _), "right boundary protected");
    Check(!CampaignTerrainEdits.Cell(ox, oy + 20, out _, out _), "bottom boundary protected");
    Check(CampaignTerrainEdits.CanMine(ox, oy), "ordinary campaign mining permitted");
    var protection = new CampaignTerrainProtection();
    Check(!protection.CanExplode(ox, oy, TileID.Stone), "explosives cannot desynchronize guest collision");
    Check(!protection.CanReplace(ox, oy, TileID.Stone, TileID.Dirt), "block swap cannot bypass whole-cell transactions");
    Check(!protection.Slope(ox, oy, TileID.Stone), "hammer cannot desynchronize guest collision");
    snapshotProperty.SetValue(null, JsonSerializer.SerializeToElement(new { control_enabled = false, stage = new { width = 20, height = 10 } }));
    Check(!CampaignTerrainEdits.CanMine(ox, oy), "script control lock blocks mining");
    Check(!CampaignTerrainEdits.CanPlace(ox, oy, TileID.Stone), "script control lock blocks placement");
    bool AnyReach(int i, int j) => true;
    bool LeftWall(int i, int j) => i == 9 && j >= 20 && j <= 21;
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, LeftWall, AnyReach, out int snappedX, out int snappedY)
        && snappedX == 10 && snappedY == 21, "native-cell cursor attaches at nearest cell edge against left native wall");
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, (i, j) => j == 22, AnyReach, out snappedX, out snappedY)
        && snappedX == 11 && snappedY == 21, "native-cell cursor attaches against native floor");
    Check(!CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, (i, j) => false, AnyReach, out snappedX, out snappedY)
        && snappedX == 11 && snappedY == 21, "floating cell without native neighbors cannot attach");
    Check(!CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, (i, j) => i == 9 && j == 19, AnyReach, out _, out _),
        "diagonal native neighbor cannot attach");
    Check(!CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, LeftWall, (i, j) => i >= 11, out _, out _),
        "snapping does not extend ordinary placement reach");
    Check(CampaignTerrainEdits.TryFindPlacementTarget(10, 20, 11, 21, LeftWall, (i, j) => j == 20, out snappedX, out snappedY)
        && snappedX == 10 && snappedY == 20, "nearest eligible edge selected when preferred tile is out of reach");
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
    Check(!new CampaignViewConfig().TerrariaSizedBlocks, "whole campaign cell placement remains the default");
    var wholeBounds = CampaignTerrainEdits.PlacementBounds(ox + 4, oy + 5, false);
    var smallBounds = CampaignTerrainEdits.PlacementBounds(ox + 4, oy + 5, true);
    Check(wholeBounds.Width == 32 && wholeBounds.Height == 32 && wholeBounds.X == (ox + 4) * 16 && wholeBounds.Y == (oy + 4) * 16,
        "whole placement collision covers selected native cell");
    Check(smallBounds.Width == 16 && smallBounds.Height == 16 && smallBounds.X == (ox + 4) * 16 && smallBounds.Y == (oy + 5) * 16,
        "small placement collision matches one ordinary host tile");
    for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++)
    {
        int bit = 1 << (sy * 2 + sx);
        Check(CampaignTerrainEdits.MaskHasTile(bit, sx, sy) && !CampaignTerrainEdits.MaskHasTile(15 ^ bit, sx, sy),
            "partial guest mask selects exact host tile");
        var visual = CampaignTerrainEdits.BlockDrawBounds(7, 9, sx, sy);
        Check(visual.Width == 8 && visual.Height == 8,
            "small block is rasterized on the existing native pixel grid");
        if (sx < 1) Check(visual.Right == CampaignTerrainEdits.BlockDrawBounds(7, 9, sx + 1, sy).Left,
            "adjacent small block visuals have no crack or overlap");
    }
    var wholeVisual = CampaignTerrainEdits.BlockDrawBounds(7, 9, -1, -1);
    Check(wholeVisual.Width == 16 && CampaignTerrainEdits.BlockDrawBounds(7, 9, 0, 0).Left == wholeVisual.Left
        && CampaignTerrainEdits.BlockDrawBounds(7, 9, 1, 1).Right == wholeVisual.Right,
        "two ordinary blocks occupy the same native visual width as one campaign block");
    Check(CampaignTerrainEdits.CellAllowsSmallBlock(0, false), "small blocks allowed in original empty cells");
    Check(!CampaignTerrainEdits.CellAllowsSmallBlock(0x41, false), "original solid cell must be mined before small placement");
    Check(CampaignTerrainEdits.CellAllowsSmallBlock(0x50, true), "mined slope cell can accept small blocks");
    var wholePieces = CampaignTerrainEdits.BlockDrawPieces(7, 9, -1, -1).ToArray();
    var smallPieces = CampaignTerrainEdits.BlockDrawPieces(7, 9, 1, 0).ToArray();
    Check(wholePieces.Length == 4 && wholePieces.All(piece => piece.Width == 8 && piece.Height == 8),
        "whole block repeats four normalized tile samples without stretching logical pixels");
    Check(wholePieces.Sum(piece => piece.Width * piece.Height) == 256, "normalized samples cover full native cell area");
    Check(smallPieces.Length == 1 && smallPieces[0] == CampaignTerrainEdits.BlockDrawBounds(7, 9, 1, 0),
        "small block uses the same normalized eight-pixel texture scale");
    string legacy = Path.Combine(temporary, "legacy");
    Directory.CreateDirectory(legacy);
    File.WriteAllText(Path.Combine(legacy, "Profile.dat"), "checkpoint-preserved");
    File.WriteAllText(Path.Combine(legacy, "terrain.json"), "{\"13\":{\"11:9\":511}}");
    File.WriteAllText(Path.Combine(legacy, "TerrainBlocks.json"), "[{\"Stage\":13,\"X\":11,\"Y\":9}]");
    File.WriteAllText(Path.Combine(legacy, "TerrainFurniture.json"), "[{\"Stage\":13,\"X\":11,\"Y\":9}]");
    string otherProfile = Path.Combine(temporary, "other-campaign", "Profile.dat");
    Directory.CreateDirectory(Path.GetDirectoryName(otherProfile)!); File.WriteAllText(otherProfile, "other campaign");
    CampaignTerrainEdits.PrepareSaveLayout(legacy);
    Check(File.ReadAllText(otherProfile) == "other campaign", "layout reset affects only selected campaign directory");
    Check(!File.Exists(Path.Combine(legacy, "terrain.json")) && !File.Exists(Path.Combine(legacy, "TerrainBlocks.json")) && !File.Exists(Path.Combine(legacy, "TerrainFurniture.json")),
        "legacy 3x3 terrain pair is removed from active version 2 save paths");
    Check(Directory.GetFiles(legacy, "*.bak").Length == 0, "incompatible terrain is discarded without archival by user request");
    Check(!File.Exists(Path.Combine(legacy, "Profile.dat")), "layout upgrade resets incompatible campaign checkpoint");
    CampaignTerrainEdits.PrepareSaveLayout(legacy);
    Check(Directory.GetFiles(legacy, "*.bak").Length == 0, "legacy layout reset is idempotent");
    File.WriteAllText(Path.Combine(legacy, "Terrain.json"), "{\"version\":2,\"subdivisions\":2,\"stages\":{}}");
    File.WriteAllText(Path.Combine(legacy, "TerrainBlocks.json"), Metadata("[]"));
    CampaignTerrainEdits.PrepareSaveLayout(legacy);
    Check(File.Exists(Path.Combine(legacy, "Terrain.json")) && File.Exists(Path.Combine(legacy, "TerrainBlocks.json")),
        "version 2 terrain files are retained unchanged");
    // Load/save actual production metadata with two stages, then verify new-game cleanup.
    string metadata = Path.Combine(temporary, CampaignTerrainEdits.FileName);
    File.WriteAllText(metadata, Metadata("[{\"Stage\":3,\"X\":2,\"Y\":4,\"Tile\":1,\"Item\":1,\"Style\":0,\"FrameX\":18,\"FrameY\":0},{\"Stage\":4,\"X\":2,\"Y\":4,\"Tile\":1,\"Item\":1,\"Style\":0,\"FrameX\":18,\"FrameY\":0}]"));
    CampaignTerrainEdits.ClearSession();
    typeof(CampaignTerrainEdits).GetMethod("EnsureLoaded", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
    typeof(CampaignTerrainEdits).GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { false });
    using (var data = JsonDocument.Parse(File.ReadAllText(metadata)))
    {
        Check(data.RootElement.GetProperty("Blocks").GetArrayLength() == 2, "same cell in different stages persisted independently");
        Check(data.RootElement.GetProperty("Blocks")[0].GetProperty("FrameX").GetInt32() == 18, "placed block source art retained");
    }
    Check(!File.Exists(metadata + ".tmp"), "atomic metadata write leaves no temporary file");
    var dictionary = (System.Collections.IDictionary)typeof(CampaignTerrainEdits).GetField("blocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    Check(dictionary.Values.Cast<object>().All(b => (int)b.GetType().GetProperty("SubX")!.GetValue(b)! == -1
        && (int)b.GetType().GetProperty("SubY")!.GetValue(b)! == -1), "version 2 whole-cell metadata has explicit whole-cell identity");
    CampaignTerrainEdits.SyncPersistence(false);
    dictionary.Clear(); // Represents mining saved placed blocks during a volatile session.
    typeof(CampaignTerrainEdits).GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { false });
    using (var data = JsonDocument.Parse(File.ReadAllText(metadata)))
        Check(data.RootElement.GetProperty("Blocks").GetArrayLength() == 2, "disabled persistence preserves saved metadata baseline");
    CampaignTerrainEdits.ReloadSaved();
    Check(dictionary.Count == 2, "retry restores saved metadata and discards volatile changes");
    dictionary.Clear();
    CampaignTerrainEdits.SyncPersistence(true);
    Check(File.ReadAllText(metadata) == Metadata("[]"), "enabling persistence saves current session changes");
    File.WriteAllText(metadata, Metadata("[{\"Stage\":3,\"X\":2,\"Y\":4,\"Tile\":1,\"Item\":1,\"Style\":0,\"FrameX\":18,\"FrameY\":0}]"));
    CampaignTerrainEdits.ReloadSaved();
    CampaignTerrainEdits.SyncPersistence(false);
    Check(new CampaignViewConfig().PersistentTerrainEdits, "terrain persistence defaults enabled");
    CampaignTerrainEdits.ClearNewGame();
    Check(File.ReadAllText(metadata) == Metadata("[]"), "fresh campaign removes obsolete placed item metadata");
    File.WriteAllText(metadata, Metadata("[{\"Stage\":3,\"X\":2,\"Y\":4,\"Tile\":1,\"Item\":1,\"Style\":0,\"FrameX\":18,\"FrameY\":0,\"SubX\":0,\"SubY\":1},{\"Stage\":3,\"X\":2,\"Y\":4,\"Tile\":0,\"Item\":2,\"Style\":0,\"FrameX\":0,\"FrameY\":0,\"SubX\":1,\"SubY\":1}]"));
    CampaignTerrainEdits.ReloadSaved();
    Check(dictionary.Count == 2, "different small block item identities coexist in one native cell");
    CampaignTerrainEdits.SyncPersistence(true);
    using (var data = JsonDocument.Parse(File.ReadAllText(metadata)))
        Check(data.RootElement.GetProperty("Blocks")[0].GetProperty("SubX").GetInt32() != data.RootElement.GetProperty("Blocks")[1].GetProperty("SubX").GetInt32(),
            "small block subcell identities survive metadata round trip");
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
static string Metadata(string blocks) => "{\"Version\":2,\"Subdivisions\":2,\"Blocks\":" + blocks + "}";
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
