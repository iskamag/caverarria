using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace Caverarria;

/// <summary>Terraria item/art metadata; the guest owns persisted terrain collision.</summary>
internal static class CampaignTerrainEdits
{
    internal const string FileName = "TerrainBlocks.json";
    internal const int LayoutVersion = 2, Subdivisions = 2, FullMask = 15;
    private sealed record TerrainFile(int Version, int Subdivisions, Block[] Blocks);
    private sealed record Block(int Stage, int X, int Y, int Tile, string? TileName, int Item, string? ItemName, int Style, int FrameX, int FrameY, int SubX = -1, int SubY = -1);
    private static readonly Dictionary<(int stage, int x, int y, int subX, int subY), Block> blocks = new();
    private static readonly Dictionary<(int x, int y), int> activeSolid = new();
    private static int[] currentCellAttributes = [];
    private static string? loadedPath;
    private static bool editing;
    private static bool persistenceEnabled = true;
    private static string MetadataPath => Path.Combine(CampaignBootstrap.SavePath, FileName);
    private static bool SmallBlocks => ModContent.GetInstance<CampaignViewConfig>().TerrariaSizedBlocks;
    private static int Stage => CampaignRuntime.Snapshot.Field("stage").Integer("id");

    public static void ClearSession() { loadedPath = null; blocks.Clear(); activeSolid.Clear(); currentCellAttributes = []; editing = false; persistenceEnabled = true; }
    public static void ClearNewGame()
    {
        EnsureLoaded(); blocks.Clear(); activeSolid.Clear(); Save(force: true);
    }
    public static void SyncPersistence(bool enabled)
    {
        if (persistenceEnabled == enabled) return;
        persistenceEnabled = enabled;
        if (enabled) { EnsureLoaded(); Save(); }
    }
    public static void ReloadSaved()
    {
        loadedPath = null; blocks.Clear(); activeSolid.Clear(); EnsureLoaded();
    }
    /// <summary>Discard this campaign's incompatible 0.1 checkpoint and terrain, as requested for 0.2.</summary>
    public static void PrepareSaveLayout(string? directory = null)
    {
        directory ??= CampaignBootstrap.SavePath;
        if (!Directory.Exists(directory)) return;
        string[] files = Directory.EnumerateFiles(directory).Where(path =>
            Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("Terrain.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals(CampaignFurniture.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool incompatible = false;
        foreach (string path in files)
        {
            bool guest = Path.GetFileName(path).Equals("Terrain.json", StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            incompatible |= json.RootElement.ValueKind != JsonValueKind.Object
                || json.RootElement.Integer(guest ? "version" : "Version") != LayoutVersion
                || json.RootElement.Integer(guest ? "subdivisions" : "Subdivisions") != Subdivisions;
        }
        if (!incompatible) return;
        foreach (string path in files) File.Delete(path);
        foreach (string path in Directory.EnumerateFiles(directory).Where(path =>
            Path.GetFileName(path).Equals("Profile.dat", StringComparison.OrdinalIgnoreCase))) File.Delete(path);
    }
    private static void EnsureLoaded()
    {
        string path = MetadataPath;
        if (loadedPath == path) return;
        blocks.Clear();
        PrepareSaveLayout();
        if (File.Exists(path))
            foreach (var b in JsonSerializer.Deserialize<TerrainFile>(File.ReadAllText(path))?.Blocks ?? [])
            {
                if (b.SubX >= Subdivisions || b.SubY >= Subdivisions || b.SubX < -1 || b.SubY < -1 || (b.SubX < 0) != (b.SubY < 0))
                    throw new InvalidDataException("Invalid version 2 terrain block coordinates.");
                blocks[(b.Stage, b.X, b.Y, b.SubX, b.SubY)] = b;
            }
        loadedPath = path;
    }
    private static void Save(bool force = false)
    {
        if (!force && !persistenceEnabled) return;
        Directory.CreateDirectory(CampaignBootstrap.SavePath);
        string temporary = MetadataPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new TerrainFile(LayoutVersion, Subdivisions, blocks.Values.ToArray())));
        File.Move(temporary, MetadataPath, true);
    }
    private static int TileType(Block b) => b.TileName == null ? b.Tile
        : ModContent.TryFind<ModTile>(b.TileName, out var tile) ? tile.Type : TileID.Stone;
    private static int ItemType(Block b) => b.ItemName == null ? b.Item
        : ModContent.TryFind<ModItem>(b.ItemName, out var item) ? item.Type : ItemID.StoneBlock;

    internal static bool Cell(int i, int j, out int x, out int y)
    {
        x = (i - CampaignRuntime.OriginTileX) / 2;
        y = (j - CampaignRuntime.OriginTileY) / 2;
        var stage = CampaignRuntime.Snapshot.Field("stage");
        return CampaignRuntime.Active && i >= CampaignRuntime.OriginTileX && j >= CampaignRuntime.OriginTileY
            && x < stage.Integer("width") && y < stage.Integer("height");
    }
    internal static bool CanMine(int i, int j) => !editing && CampaignRuntime.ControlsEnabled && Cell(i, j, out _, out _);
    internal static bool CanPlace(int i, int j, int type)
    {
        if (editing || !CampaignRuntime.ControlsEnabled || !Cell(i, j, out int x, out int y)
            || !Main.tileSolid[type] || Main.tileSolidTop[type] || Main.tileFrameImportant[type] || TileID.Sets.Falling[type]) return false;
        bool small = SmallBlocks;
        var bounds = PlacementBounds(i, j, small);
        foreach (var player in Main.player)
            if (player.active && !player.dead && player.Hitbox.Intersects(bounds)) return false;
        if (small)
        {
            int index = y * CampaignRuntime.Snapshot.Field("stage").Integer("width") + x;
            bool allowed = index >= 0 && index < currentCellAttributes.Length
                && CellAllowsSmallBlock(currentCellAttributes[index], activeSolid.ContainsKey((x, y)));
            return allowed && !Main.tile[i, j].HasTile;
        }
        for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++)
            if (Main.tile[CampaignRuntime.OriginTileX + x * 2 + sx, CampaignRuntime.OriginTileY + y * 2 + sy].HasTile) return false;
        return true;
    }
    // Preserve authored solid/slope art and physics until it is mined as a whole
    // cell. Empty cells and existing edit masks can accept individual host tiles.
    internal static bool CellAllowsSmallBlock(int attribute, bool edited) => edited || attribute is 0 or 0x40;
    internal static Rectangle PlacementBounds(int i, int j, bool small)
    {
        if (small) return new Rectangle(i * 16, j * 16, 16, 16);
        int x = (i - CampaignRuntime.OriginTileX) / 2, y = (j - CampaignRuntime.OriginTileY) / 2;
        return new Rectangle((CampaignRuntime.OriginTileX + x * 2) * 16, (CampaignRuntime.OriginTileY + y * 2) * 16, 32, 32);
    }
    /// <summary>Keep the mouse's native cell, but let vanilla attach its first host tile at that cell's edge.</summary>
    public static void TrySnapPlacementTarget(Player player)
    {
        if (SmallBlocks) return; // Vanilla already targets and attaches individual host tiles.
        int type = player.HeldItem.createTile;
        int desiredX = Player.tileTargetX, desiredY = Player.tileTargetY;
        if (type < 0 || !CanPlace(desiredX, desiredY, type)
            || !Cell(desiredX, desiredY, out int x, out int y)) return;
        int left = CampaignRuntime.OriginTileX + x * 2, top = CampaignRuntime.OriginTileY + y * 2;
        int reachX = Player.tileRangeX + player.blockRange + player.HeldItem.tileBoost;
        int reachY = Player.tileRangeY + player.blockRange + player.HeldItem.tileBoost;
        bool InReach(int i, int j) => PlacementInReach(player.position, player.width, player.height, reachX, reachY, i, j);
        bool Solid(int i, int j) => Cell(i, j, out _, out _) && Main.tile[i, j].HasTile
            && !Main.tile[i, j].IsActuated && Main.tileSolid[Main.tile[i, j].TileType];
        if (!TryFindPlacementTarget(left, top, desiredX, desiredY, Solid, InReach, out int targetX, out int targetY)) return;
        Player.tileTargetX = targetX;
        Player.tileTargetY = targetY;
    }

    internal static bool PlacementInReach(Vector2 position, int width, int height, int reachX, int reachY, int x, int y)
        => position.X / 16f - reachX <= x && (position.X + width) / 16f + reachX - 1f >= x
            && position.Y / 16f - reachY <= y && (position.Y + height) / 16f + reachY - 2f >= y;

    internal static bool TryFindPlacementTarget(int left, int top, int desiredX, int desiredY,
        Func<int, int, bool> solid, Func<int, int, bool> inReach, out int targetX, out int targetY)
    {
        targetX = desiredX; targetY = desiredY;
        int closest = int.MaxValue;
        for (int y = top; y < top + 2; y++) for (int x = left; x < left + 2; x++)
        {
            if (!inReach(x, y) || !(solid(x - 1, y) || solid(x + 1, y) || solid(x, y - 1) || solid(x, y + 1))) continue;
            int distance = (x - desiredX) * (x - desiredX) + (y - desiredY) * (y - desiredY);
            if (distance >= closest) continue;
            closest = distance; targetX = x; targetY = y;
        }
        return closest != int.MaxValue;
    }

    internal static void Mine(int i, int j, ref bool fail, bool effectOnly, ref bool noItem)
    {
        if (fail || effectOnly || !CanMine(i, j) || !Cell(i, j, out int x, out int y)) return;
        EnsureLoaded();
        int stage = Stage;
        int subX = (i - CampaignRuntime.OriginTileX) % 2, subY = (j - CampaignRuntime.OriginTileY) % 2;
        blocks.TryGetValue((stage, x, y, subX, subY), out var placed);
        bool small = placed != null;
        if (!small) blocks.TryGetValue((stage, x, y, -1, -1), out placed);
        // A cell containing small blocks must not lose neighbors when its remaining
        // native terrain is mined. Existing whole-cell placements always remain whole.
        if (placed == null && blocks.Values.Any(b => b.Stage == stage && b.X == x && b.Y == y && b.SubX >= 0)) small = true;
        editing = true;
        try
        {
            if (!CampaignRuntime.EditTerrain(x, y, false, small ? subX : -1, small ? subY : -1)) { fail = true; return; }
            bool drop = !noItem;
            noItem = true;
            blocks.Remove((stage, x, y, small ? subX : -1, small ? subY : -1)); Save();
            if (small) Main.tile[i, j].ClearTile(); else ClearCell(x, y);
            if (drop) Item.NewItem(new EntitySource_TileBreak(i, j),
                PlacementBounds(i, j, small),
                placed == null ? ItemID.StoneBlock : ItemType(placed));
        }
        finally { editing = false; }
    }
    internal static void Place(int i, int j, int type, Item item)
    {
        if (editing || !CampaignRuntime.Active || !Cell(i, j, out int x, out int y)) return;
        EnsureLoaded();
        int stage = Stage;
        var tile = Main.tile[i, j];
        bool small = SmallBlocks;
        int subX = small ? (i - CampaignRuntime.OriginTileX) % 2 : -1, subY = small ? (j - CampaignRuntime.OriginTileY) % 2 : -1;
        var block = new Block(stage, x, y, type, TileLoader.GetTile(type)?.FullName,
            item.type, item.ModItem?.FullName, item.placeStyle, tile.TileFrameX, tile.TileFrameY, subX, subY);
        editing = true;
        try
        {
            // PlaceInWorld follows Terraria's ordinary placement/item consumption.
            if (!CampaignRuntime.EditTerrain(x, y, true, subX, subY))
            {
                tile.ClearTile();
                Item.NewItem(new EntitySource_TileBreak(i, j), i * 16, j * 16, 16, 16, item.type);
                return;
            }
            blocks[(stage, x, y, subX, subY)] = block; Save();
            ProjectPlacedTiles(stage);
        }
        finally { editing = false; }
    }
    private static void ClearCell(int x, int y)
    {
        for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++)
            Main.tile[CampaignRuntime.OriginTileX + x * 2 + sx, CampaignRuntime.OriginTileY + y * 2 + sy].ClearTile();
    }
    internal static bool MaskHasTile(int mask, int subX, int subY) => (mask & (1 << (subY * 2 + subX))) != 0;
    private static bool GuestHasBlock(Block block) => activeSolid.TryGetValue((block.X, block.Y), out int mask)
        && (block.SubX < 0 ? mask == FullMask : MaskHasTile(mask, block.SubX, block.SubY));

    internal static Rectangle BlockDrawBounds(int cellX, int cellY, int subX, int subY)
    {
        if (subX < 0) return new Rectangle(cellX * 16 - 8, cellY * 16 - 8, 16, 16);
        return new Rectangle(cellX * 16 - 8 + subX * 8, cellY * 16 - 8 + subY * 8, 8, 8);
    }

    internal static IEnumerable<Rectangle> BlockDrawPieces(int cellX, int cellY, int subX, int subY)
    {
        if (subX >= 0) { yield return BlockDrawBounds(cellX, cellY, subX, subY); yield break; }
        // A full Cave Story cell contains four ordinary Terraria tiles. Each tile
        // uses the same fixed two texture pixels per native pixel sampling rate.
        for (int y = 0; y < Subdivisions; y++) for (int x = 0; x < Subdivisions; x++)
            yield return BlockDrawBounds(cellX, cellY, x, y);
    }

    public static void ProjectPlacedTiles(int stage)
    {
        EnsureLoaded();
        activeSolid.Clear();
        currentCellAttributes = CampaignRuntime.CurrentMap.Field("cell_attributes").Elements().Select(value => value.GetInt32()).ToArray();
        foreach (var edit in CampaignRuntime.CurrentMap.Field("terrain_edits").Elements())
            activeSolid[(edit.Integer("x"), edit.Integer("y"))] = edit.Integer("mask", edit.Boolean("solid") ? FullMask : 0);
        foreach (var b in blocks.Values.Where(b => b.Stage == stage && GuestHasBlock(b)))
        {
            // Guest script mutations can remove a placed cell; never resurrect it.
            int i = CampaignRuntime.OriginTileX + b.X * 2, j = CampaignRuntime.OriginTileY + b.Y * 2;
            if (i < 0 || j < 0 || i + 1 >= Main.maxTilesX || j + 1 >= Main.maxTilesY) continue;
            int firstX = b.SubX < 0 ? 0 : b.SubX, firstY = b.SubY < 0 ? 0 : b.SubY;
            int count = b.SubX < 0 ? 2 : 1;
            for (int sy = firstY; sy < firstY + count; sy++) for (int sx = firstX; sx < firstX + count; sx++)
            {
                var tile = Main.tile[i + sx, j + sy];
                if (!tile.HasTile) continue;
                tile.TileType = (ushort)TileType(b);
                tile.TileFrameX = (short)b.FrameX; tile.TileFrameY = (short)b.FrameY;
            }
        }
    }
    public static void Prepare()
    {
        if (!CampaignRuntime.Active) return;
        EnsureLoaded();
        foreach (var b in blocks.Values.Where(b => b.Stage == Stage && GuestHasBlock(b)))
        {
            int type = TileType(b); Main.instance.LoadTiles(type);
            var texture = TextureAssets.Tile[type].Value;
            var source = TileSource(texture, b);
            CampaignTilePixels.Prepare(texture, source);
        }
    }
    private static Rectangle TileSource(Texture2D texture, Block block)
        => new(Math.Clamp(block.FrameX, 0, Math.Max(0, texture.Width - 16)),
            Math.Clamp(block.FrameY, 0, Math.Max(0, texture.Height - 16)), 16, 16);
    public static void Draw(SpriteBatch batch, Vector2 nativeCamera)
    {
        if (!CampaignRuntime.Active) return;
        EnsureLoaded();
        foreach (var b in blocks.Values.Where(b => b.Stage == Stage && GuestHasBlock(b)))
        {
            int i = CampaignRuntime.OriginTileX + b.X * 2, j = CampaignRuntime.OriginTileY + b.Y * 2;
            int testI = i + Math.Max(0, b.SubX), testJ = j + Math.Max(0, b.SubY);
            if (testI < 0 || testJ < 0 || testI >= Main.maxTilesX || testJ >= Main.maxTilesY || !Main.tile[testI, testJ].HasTile) continue;
            int type = TileType(b); Main.instance.LoadTiles(type);
            var texture = TextureAssets.Tile[type].Value;
            var normalized = CampaignTilePixels.Get(texture, TileSource(texture, b));
            foreach (var bounds in BlockDrawPieces(b.X, b.Y, b.SubX, b.SubY))
                batch.Draw(normalized, new Vector2(bounds.X - MathF.Floor(nativeCamera.X), bounds.Y - MathF.Floor(nativeCamera.Y)), Color.White);
        }
    }
}
