using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace Caverarria;

/// <summary>Ordinary non-solid Terraria furniture, scoped to the current guest room.</summary>
internal static class CampaignFurniture
{
    internal const string FileName = "TerrainFurniture.json";
    private sealed record Piece(int X, int Y, short FrameX, short FrameY);
    private sealed record Furniture(int Stage, int X, int Y, int Tile, string? TileName, int Style, int Alternate, Piece[] Pieces);
    private sealed record FurnitureFile(int Version, int Subdivisions, Furniture[] Objects);
    private static readonly Dictionary<(int stage, int x, int y), Furniture> furniture = new();
    private static string? loadedPath;
    private static TerrainPersistence persistence = TerrainPersistence.Session;
    private static int Stage => CampaignRuntime.Snapshot.Field("stage").Integer("id");
    private static string MetadataPath => Path.Combine(CampaignBootstrap.SavePath, FileName);
    private static void EnsureLoaded()
    {
        string path = MetadataPath;
        if (loadedPath == path) return;
        furniture.Clear();
        if (File.Exists(path))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.Integer("Version") != 2
                || json.RootElement.Integer("Subdivisions") != 2)
                throw new InvalidDataException("Unsupported furniture layout.");
            foreach (var item in JsonSerializer.Deserialize<FurnitureFile>(json.RootElement.GetRawText())?.Objects ?? [])
                furniture[(item.Stage, item.X, item.Y)] = item;
        }
        loadedPath = path;
    }
    private static void Save(bool force = false)
    {
        if (!force && persistence == TerrainPersistence.Off) return;
        Directory.CreateDirectory(CampaignBootstrap.SavePath);
        string temporary = MetadataPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new FurnitureFile(2, 2, furniture.Values.ToArray())));
        File.Move(temporary, MetadataPath, true);
    }
    public static void ClearSession() { furniture.Clear(); drawPieces.Clear(); loadedPath = null; persistence = TerrainPersistence.Session; CampaignTilePixels.DisposeFrames(); }
    public static void ClearNewGame() { EnsureLoaded(); furniture.Clear(); Save(force: true); }
    public static void ReloadSaved() { furniture.Clear(); loadedPath = null; EnsureLoaded(); }
    public static void SyncPersistence(TerrainPersistence mode)
    {
        if (persistence == mode) return;
        // Off blocks are never authoritative, so returning from Off reloads the file.
        bool wasOff = persistence == TerrainPersistence.Off;
        bool loaded = loadedPath != null;
        persistence = mode;
        if (mode == TerrainPersistence.Off) { loadedPath = null; furniture.Clear(); drawPieces.Clear(); }
        else if (loaded && !wasOff) { Refresh(); Save(); }
        else { loadedPath = null; furniture.Clear(); drawPieces.Clear(); EnsureLoaded(); Refresh(); }
    }
    /// <summary>Commits current furniture as the checkpoint baseline for Session mode.</summary>
    public static void MarkSaved() { if (persistence == TerrainPersistence.Session) Save(force: true); }
    /// <summary>Drops a stage's furniture after the engine restores its authored room.</summary>
    public static void ForgetStage(int stage)
    {
        EnsureLoaded();
        foreach (var key in furniture.Keys.Where(key => key.stage == stage).ToArray()) furniture.Remove(key);
        Refresh(); Save(force: true);
    }
    private static int TileType(Furniture item) => item.TileName == null ? item.Tile
        : ModContent.TryFind<ModTile>(item.TileName, out var tile) ? tile.Type : -1;

    internal static bool Supports(int type, int style = 0)
    {
        if (type < 0 || type >= Main.tileSolid.Length || Main.tileSolid[type] || Main.tileSolidTop[type]
            || !Main.tileFrameImportant[type] || Main.tileContainer[type] || TileID.Sets.IsAContainer[type]) return false;
        TileObjectData? data = TileObjectData.GetTileData(type, style);
        // Tile entities/chests need their own state protocol. Ordinary decorative
        // objects keep all their state in their actual multi-tile frames.
        return data != null && data.Width > 0 && data.Height > 0 && data.Width <= 8 && data.Height <= 8
            && data.HookPostPlaceMyPlayer.hook == null;
    }
    internal static Rectangle Footprint(TileObjectData data, int i, int j)
        => new(i - data.Origin.X, j - data.Origin.Y, data.Width, data.Height);
    internal static bool Fits(Rectangle footprint, Func<int, int, bool> available)
    {
        for (int y = footprint.Top; y < footprint.Bottom; y++)
            for (int x = footprint.Left; x < footprint.Right; x++) if (!available(x, y)) return false;
        return footprint.Width > 0 && footprint.Height > 0;
    }
    internal static bool CanPlace(int i, int j, int type)
    {
        int style = Main.LocalPlayer.HeldItem.createTile == type ? Main.LocalPlayer.HeldItem.placeStyle : 0;
        if (!CampaignRuntime.ControlsEnabled || !Supports(type, style)) return false;
        TileObjectData data = TileObjectData.GetTileData(type, style)!;
        // Vanilla still owns anchor/liquid/reach checks, placement and item use.
        return Fits(Footprint(data, i, j), (x, y) => CampaignTerrainEdits.Cell(x, y, out _, out _)
            && !Main.tile[x, y].HasTile);
    }
    internal static bool CanMine(int i, int j, int type) => CampaignRuntime.ControlsEnabled && Supports(type)
        && CampaignTerrainEdits.Cell(i, j, out _, out _);
    internal static void Place(int i, int j, int type)
    {
        if (!CampaignRuntime.Active || !Supports(type) || !CampaignTerrainEdits.Cell(i, j, out _, out _)) return;
        EnsureLoaded();
        Tile tile = Main.tile[i, j];
        TileObjectData? data = TileObjectData.GetTileData(tile);
        if (data == null) return;
        var top = TileObjectData.TopLeft(i, j);
        int style = 0, alternate = 0;
        TileObjectData.GetTileInfo(tile, ref style, ref alternate);
        var pieces = new List<Piece>();
        for (int y = 0; y < data.Height; y++) for (int x = 0; x < data.Width; x++)
        {
            Tile piece = Main.tile[top.X + x, top.Y + y];
            if (!piece.HasTile || piece.TileType != type) return;
            pieces.Add(new Piece(x, y, piece.TileFrameX, piece.TileFrameY));
        }
        int left = top.X - CampaignRuntime.OriginTileX, upper = top.Y - CampaignRuntime.OriginTileY;
        var placed = new Furniture(Stage, left, upper, type, TileLoader.GetTile(type)?.FullName, style, alternate, pieces.ToArray());
        furniture[(Stage, left, upper)] = placed;
        Save();
    }
    internal static void Mine(int i, int j, bool fail, bool effectOnly)
    {
        if (fail || effectOnly) return;
        EnsureLoaded();
        int x = i - CampaignRuntime.OriginTileX, y = j - CampaignRuntime.OriginTileY;
        foreach (var pair in furniture.Where(pair => pair.Key.stage == Stage
            && pair.Value.Pieces.Any(piece => pair.Value.X + piece.X == x && pair.Value.Y + piece.Y == y)).ToArray())
            furniture.Remove(pair.Key);
        Save();
        // Do not edit the native collision cell or replace vanilla's item drop.
    }
    private static void Refresh()
    {
        bool changed = false;
        foreach (var pair in furniture.Where(pair => pair.Key.stage == Stage).ToArray())
        {
            Furniture item = pair.Value; int type = TileType(item);
            var pieces = item.Pieces.ToArray();
            for (int n = 0; n < pieces.Length; n++)
            {
                Piece piece = pieces[n];
                int i = CampaignRuntime.OriginTileX + item.X + piece.X, j = CampaignRuntime.OriginTileY + item.Y + piece.Y;
                if (i < 0 || j < 0 || i >= Main.maxTilesX || j >= Main.maxTilesY) continue;
                Tile tile = Main.tile[i, j];
                if (!tile.HasTile || tile.TileType != type) continue;
                if (tile.TileFrameX == piece.FrameX && tile.TileFrameY == piece.FrameY) continue;
                pieces[n] = piece with { FrameX = tile.TileFrameX, FrameY = tile.TileFrameY }; changed = true;
            }
            furniture[pair.Key] = item with { Pieces = pieces };
        }
        if (changed) Save();
    }
    public static void Project(int stage)
    {
        EnsureLoaded();
        foreach (Furniture item in furniture.Values.Where(item => item.Stage == stage))
        {
            int type = TileType(item);
            if (type < 0 || !Supports(type, item.Style)) continue;
            if (!item.Pieces.All(piece => {
                int i = CampaignRuntime.OriginTileX + item.X + piece.X, j = CampaignRuntime.OriginTileY + item.Y + piece.Y;
                return CampaignTerrainEdits.Cell(i, j, out _, out _) && !Main.tile[i, j].HasTile;
            })) continue;
            foreach (Piece piece in item.Pieces)
            {
                Tile tile = Main.tile[CampaignRuntime.OriginTileX + item.X + piece.X, CampaignRuntime.OriginTileY + item.Y + piece.Y];
                tile.HasTile = true; tile.TileType = (ushort)type;
                tile.TileFrameX = piece.FrameX; tile.TileFrameY = piece.FrameY;
            }
        }
    }
    private sealed record DrawPiece(Texture2D Texture, Vector2 Position, Color Color, SpriteEffects Effects);
    private static readonly List<DrawPiece> drawPieces = new();
    public static void Prepare(Vector2 nativeCamera)
    {
        drawPieces.Clear();
        if (!CampaignRuntime.Active) return;
        EnsureLoaded(); Refresh();
        foreach (Furniture item in furniture.Values.Where(item => item.Stage == Stage))
        {
            int type = TileType(item);
            if (type < 0) continue;
            Main.instance.LoadTiles(type);
            Texture2D texture = TextureAssets.Tile[type].Value;
            foreach (Piece piece in item.Pieces)
            {
                int i = CampaignRuntime.OriginTileX + item.X + piece.X, j = CampaignRuntime.OriginTileY + item.Y + piece.Y;
                if (i < 0 || j < 0 || i >= Main.maxTilesX || j >= Main.maxTilesY) continue;
                Tile tile = Main.tile[i, j];
                if (!tile.HasTile || tile.TileType != type) continue;
                short fx = tile.TileFrameX, fy = tile.TileFrameY;
                // Use vanilla's actual frame offsets, source dimensions and
                // animation, plus tML tile drawing adjustments.
                Main.instance.TilesRenderer.GetTileDrawData(i, j, tile, (ushort)type, ref fx, ref fy,
                    out int width, out int height, out int top, out int half, out int addX, out int addY,
                    out SpriteEffects effects, out Texture2D glow, out Rectangle glowSource, out Color glowColor);
                Rectangle source = new(fx + addX, fy + addY, width, height - half);
                if (source.Left < 0 || source.Top < 0 || source.Right > texture.Width || source.Bottom > texture.Height) continue;
                Vector2 position = CampaignTilePixels.Position(
                    new Vector2(i * 16 - (width - 16) / 2f, j * 16 + top + half), nativeCamera);
                drawPieces.Add(new DrawPiece(CampaignTilePixels.Prepare(texture, source), position, Color.White, effects));
                if (glow != null && glowSource.Width > 0 && glowSource.Height > 0)
                    drawPieces.Add(new DrawPiece(CampaignTilePixels.Prepare(glow, glowSource), position, glowColor, effects));
            }
        }
    }
    // Caller supplies the guest-grid transform, shared with projected terrain.
    public static void Draw(SpriteBatch batch)
    {
        foreach (var piece in drawPieces)
            batch.Draw(piece.Texture, piece.Position, null, piece.Color, 0, Vector2.Zero, 1, piece.Effects, 0);
    }
}
