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
    private sealed record Block(int Stage, int X, int Y, int Tile, string? TileName, int Item, string? ItemName, int Style, int FrameX, int FrameY);
    private static readonly Dictionary<(int stage, int x, int y), Block> blocks = new();
    private static readonly HashSet<(int x, int y)> activeSolid = new();
    private static string? loadedPath;
    private static bool editing;
    private static string MetadataPath => Path.Combine(CampaignBootstrap.SavePath, FileName);
    private static int Stage => CampaignRuntime.Snapshot.Field("stage").Integer("id");

    public static void ClearSession() { loadedPath = null; blocks.Clear(); activeSolid.Clear(); editing = false; }
    public static void ClearNewGame()
    {
        EnsureLoaded(); blocks.Clear(); activeSolid.Clear(); Save();
    }
    private static void EnsureLoaded()
    {
        string path = MetadataPath;
        if (loadedPath == path) return;
        blocks.Clear();
        if (File.Exists(path))
            foreach (var b in JsonSerializer.Deserialize<Block[]>(File.ReadAllText(path)) ?? [])
                blocks[(b.Stage, b.X, b.Y)] = b;
        loadedPath = path;
    }
    private static void Save()
    {
        Directory.CreateDirectory(CampaignBootstrap.SavePath);
        string temporary = MetadataPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(blocks.Values.ToArray()));
        File.Move(temporary, MetadataPath, true);
    }
    private static int TileType(Block b) => b.TileName == null ? b.Tile
        : ModContent.TryFind<ModTile>(b.TileName, out var tile) ? tile.Type : TileID.Stone;
    private static int ItemType(Block b) => b.ItemName == null ? b.Item
        : ModContent.TryFind<ModItem>(b.ItemName, out var item) ? item.Type : ItemID.StoneBlock;

    internal static bool Cell(int i, int j, out int x, out int y)
    {
        x = (i - CampaignRuntime.OriginTileX) / 3;
        y = (j - CampaignRuntime.OriginTileY) / 3;
        var stage = CampaignRuntime.Snapshot.Field("stage");
        return CampaignRuntime.Active && i >= CampaignRuntime.OriginTileX && j >= CampaignRuntime.OriginTileY
            && x < stage.Integer("width") && y < stage.Integer("height");
    }
    internal static bool CanMine(int i, int j) => !editing && CampaignRuntime.ControlsEnabled && Cell(i, j, out _, out _);
    internal static bool CanPlace(int i, int j, int type)
    {
        if (editing || !CampaignRuntime.ControlsEnabled || !Cell(i, j, out int x, out int y)
            || !Main.tileSolid[type] || Main.tileSolidTop[type] || Main.tileFrameImportant[type] || TileID.Sets.Falling[type]) return false;
        var bounds = new Rectangle((CampaignRuntime.OriginTileX + x * 3) * 16, (CampaignRuntime.OriginTileY + y * 3) * 16, 48, 48);
        foreach (var player in Main.player)
            if (player.active && !player.dead && player.Hitbox.Intersects(bounds)) return false;
        for (int sy = 0; sy < 3; sy++) for (int sx = 0; sx < 3; sx++)
            if (Main.tile[CampaignRuntime.OriginTileX + x * 3 + sx, CampaignRuntime.OriginTileY + y * 3 + sy].HasTile) return false;
        return true;
    }
    /// <summary>Keep the mouse's native cell, but let vanilla attach its first host tile at that cell's edge.</summary>
    public static void TrySnapPlacementTarget(Player player)
    {
        int type = player.HeldItem.createTile;
        int desiredX = Player.tileTargetX, desiredY = Player.tileTargetY;
        if (type < 0 || !CanPlace(desiredX, desiredY, type)
            || !Cell(desiredX, desiredY, out int x, out int y)) return;
        int left = CampaignRuntime.OriginTileX + x * 3, top = CampaignRuntime.OriginTileY + y * 3;
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
        for (int y = top; y < top + 3; y++) for (int x = left; x < left + 3; x++)
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
        blocks.TryGetValue((stage, x, y), out var placed);
        editing = true;
        try
        {
            if (!CampaignRuntime.EditTerrain(x, y, false)) { fail = true; return; }
            bool drop = !noItem;
            noItem = true;
            blocks.Remove((stage, x, y)); Save();
            ClearCell(x, y);
            if (drop) Item.NewItem(new EntitySource_TileBreak(i, j),
                new Rectangle((CampaignRuntime.OriginTileX + x * 3) * 16, (CampaignRuntime.OriginTileY + y * 3) * 16, 48, 48),
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
        var block = new Block(stage, x, y, type, TileLoader.GetTile(type)?.FullName,
            item.type, item.ModItem?.FullName, item.placeStyle, tile.TileFrameX, tile.TileFrameY);
        editing = true;
        try
        {
            // PlaceInWorld follows Terraria's ordinary placement/item consumption.
            if (!CampaignRuntime.EditTerrain(x, y, true))
            {
                tile.ClearTile();
                Item.NewItem(new EntitySource_TileBreak(i, j), i * 16, j * 16, 16, 16, item.type);
                return;
            }
            blocks[(stage, x, y)] = block; Save();
            ProjectPlacedTiles(stage);
        }
        finally { editing = false; }
    }
    private static void ClearCell(int x, int y)
    {
        for (int sy = 0; sy < 3; sy++) for (int sx = 0; sx < 3; sx++)
            Main.tile[CampaignRuntime.OriginTileX + x * 3 + sx, CampaignRuntime.OriginTileY + y * 3 + sy].ClearTile();
    }
    private static bool GuestHasBlock(Block block) => activeSolid.Contains((block.X, block.Y));

    public static void ProjectPlacedTiles(int stage)
    {
        EnsureLoaded();
        activeSolid.Clear();
        foreach (var edit in CampaignRuntime.CurrentMap.Field("terrain_edits").Elements())
            if (edit.Boolean("solid")) activeSolid.Add((edit.Integer("x"), edit.Integer("y")));
        foreach (var b in blocks.Values.Where(b => b.Stage == stage && GuestHasBlock(b)))
        {
            // Guest script mutations can remove a placed cell; never resurrect it.
            int i = CampaignRuntime.OriginTileX + b.X * 3, j = CampaignRuntime.OriginTileY + b.Y * 3;
            if (i < 0 || j < 0 || i + 2 >= Main.maxTilesX || j + 2 >= Main.maxTilesY || !Main.tile[i, j].HasTile) continue;
            for (int sy = 0; sy < 3; sy++) for (int sx = 0; sx < 3; sx++)
            {
                var tile = Main.tile[i + sx, j + sy];
                tile.HasTile = true; tile.TileType = (ushort)TileType(b);
                tile.TileFrameX = (short)b.FrameX; tile.TileFrameY = (short)b.FrameY;
            }
        }
    }
    public static void Draw(SpriteBatch batch, Vector2 nativeCamera)
    {
        if (!CampaignRuntime.Active) return;
        EnsureLoaded();
        foreach (var b in blocks.Values.Where(b => b.Stage == Stage && GuestHasBlock(b)))
        {
            int i = CampaignRuntime.OriginTileX + b.X * 3, j = CampaignRuntime.OriginTileY + b.Y * 3;
            if (i < 0 || j < 0 || i >= Main.maxTilesX || j >= Main.maxTilesY || !Main.tile[i, j].HasTile) continue;
            int type = TileType(b); Main.instance.LoadTiles(type);
            var texture = TextureAssets.Tile[type].Value;
            int fx = Math.Clamp(b.FrameX, 0, Math.Max(0, texture.Width - 16)), fy = Math.Clamp(b.FrameY, 0, Math.Max(0, texture.Height - 16));
            batch.Draw(texture, new Vector2(b.X * 16 - 8, b.Y * 16 - 8) - nativeCamera,
                new Rectangle(fx, fy, 16, 16), Color.White);
        }
    }
}
