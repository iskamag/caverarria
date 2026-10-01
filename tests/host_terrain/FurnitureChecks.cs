using System.Collections;
using System.Reflection;
using System.Text.Json;
using Caverarria;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

internal static class FurnitureChecks
{
    private static bool dataLayout(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("Version").GetInt32() == 2
            && json.RootElement.GetProperty("Subdivisions").GetInt32() == 2;
    }
    internal static void Run(string directory, Action<bool, string> check)
    {
        Terraria.Program.SavePath = directory;
        TileObjectData.Initialize();
        bool oldFrame = Main.tileFrameImportant[TileID.SliceOfCake];
        Main.tileFrameImportant[TileID.SliceOfCake] = true;
        try
        {
            check(CampaignTilePixels.LogicalSize(new Rectangle(18, 36, 16, 16)) == new Point(8, 8),
                "Terraria tile texture texels were not normalized to the native material grid");
            check(CampaignTilePixels.LogicalSize(new Rectangle(0, 0, 20, 18)) == new Point(10, 9),
                "overhanging furniture frame dimensions were forced to one host tile");
            Vector2 material = CampaignRuntime.Origin + new Vector2(24, 32) * CampaignRuntime.Scale;
            check(CampaignTilePixels.Position(material, new Vector2(10.1f, 6.1f)) == new Vector2(14, 26)
                && CampaignTilePixels.Position(material, new Vector2(10.9f, 6.9f)) == new Vector2(14, 26),
                "fractional camera motion resampled or shifted static furniture within a logical pixel");
            check(CampaignTilePixels.CameraOrigin(new Vector2(-.2f, -.8f)) == new Vector2(-1, -1),
                "guest negative camera margins were truncated toward zero");
            check(CampaignTilePixels.Position(material, new Vector2(11, 7)) == new Vector2(13, 25),
                "furniture did not advance exactly one logical pixel with the guest camera");
            check(20f * new CampaignViewConfig().PlayerBodyScale / CampaignRuntime.Scale == 10f
                && 42f * new CampaignViewConfig().PlayerBodyScale / CampaignRuntime.Scale == 21f,
                "new default geometry changed native avatar dimensions");
            TileObjectData cake = TileObjectData.GetTileData(TileID.SliceOfCake, 0);
            check(cake.Width == 2 && cake.Height == 2 && cake.Origin.X == 0 && cake.Origin.Y == 1,
                "actual Slice of Cake placement footprint differs from vanilla");
            check(CampaignFurniture.Supports(TileID.SliceOfCake), "ordinary cake was rejected as campaign decoration");
            Rectangle bounds = CampaignFurniture.Footprint(cake, 203, 206);
            check(bounds == new Rectangle(203, 205, 2, 2), "furniture placement did not apply vanilla placement origin");
            check(CampaignFurniture.Fits(bounds, (x, y) => x >= 203 && x <= 204 && y >= 205 && y <= 206),
                "complete furniture footprint rejected");
            check(!CampaignFurniture.Fits(bounds, (x, y) => x != 204 || y != 205),
                "occupied furniture corner outside clicked tile was ignored");
            check(!CampaignFurniture.Fits(bounds, (x, y) => x < 204), "room boundary failed to protect complete furniture footprint");
            string path = Path.Combine(directory, CampaignFurniture.FileName);
            var pieces = Enumerable.Range(0, 4).Select(n => new { X = n % 2, Y = n / 2, FrameX = n % 2 * 18, FrameY = n / 2 * 18 }).ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 2, Subdivisions = 2, Objects = new[] {
                new { Stage = 3, X = 2, Y = 4, Tile = (int)TileID.SliceOfCake, TileName = (string?)null, Style = 0, Alternate = 0, Pieces = pieces },
                new { Stage = 4, X = 2, Y = 4, Tile = (int)TileID.SliceOfCake, TileName = (string?)null, Style = 0, Alternate = 0, Pieces = pieces }
            } }));
            CampaignFurniture.ClearSession();
            typeof(CampaignFurniture).GetMethod("EnsureLoaded", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            var metadata = (IDictionary)typeof(CampaignFurniture).GetField("furniture", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            check(metadata.Count == 2, "furniture in independent rooms collided in metadata identity");
            typeof(CampaignFurniture).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { false });
            using (var data = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var saved = data.RootElement.GetProperty("Objects")[0].GetProperty("Pieces");
                check(saved.GetArrayLength() == 4 && saved[3].GetProperty("FrameX").GetInt32() == 18
                    && saved[3].GetProperty("FrameY").GetInt32() == 18,
                    "multi-tile furniture frame/style geometry was flattened during round trip");
            }
            check(dataLayout(path), "furniture save did not identify its host/native geometry version");
            check(!File.Exists(path + ".tmp"), "furniture atomic write left its temporary file");
            CampaignFurniture.SyncPersistence(false);
            metadata.Clear();
            typeof(CampaignFurniture).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { false });
            check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Objects").GetArrayLength() == 2,
                "volatile furniture changes replaced saved baseline");
            CampaignFurniture.ReloadSaved();
            check(metadata.Count == 2, "retry failed to restore saved furniture metadata");
            bool fail = false, noItem = false, effectOnly = false;
            new CampaignTerrainProtection().KillTile(202, 204, TileID.SliceOfCake, ref fail, ref effectOnly, ref noItem);
            check(!fail && !noItem && metadata.Count == 1,
                "ordinary furniture mining modified guest terrain or suppressed vanilla item drop");
            // The fixture engine throws on Send: reaching here also proves cake
            // mining never issued a native terrain collision mutation.
            metadata.Clear(); CampaignFurniture.SyncPersistence(true);
            check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Objects").GetArrayLength() == 0, "enabling persistence failed to save current furniture state");
            CampaignFurniture.ReloadSaved(); CampaignFurniture.SyncPersistence(false); CampaignFurniture.ClearNewGame();
            check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Objects").GetArrayLength() == 0, "fresh campaign retained furniture from an old run");
            CampaignFurniture.ClearSession();
            File.WriteAllText(path, "[]");
            bool rejected = false;
            try { typeof(CampaignFurniture).GetMethod("EnsureLoaded", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == "[]", "unsupported furniture must fail without deleting its file");
        }
        finally { Main.tileFrameImportant[TileID.SliceOfCake] = oldFrame; CampaignFurniture.ClearSession(); }
    }
}
