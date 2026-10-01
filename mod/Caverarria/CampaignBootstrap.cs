using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Enums;
using Terraria.ID;
using Terraria.IO;
using Terraria.Map;
using Terraria.ModLoader;

namespace Caverarria;

internal static class CampaignBootstrap
{
    public const string WorldName = "Caverarria — The Island";
    public static bool Started;
    public static bool IsCampaignWorld { get; private set; }
    public static string CampaignId { get; private set; } = "";
    public static string AssetsPath => Path.Combine(Main.SavePath, "Caverarria");
    public static string SavePath => Environment.GetEnvironmentVariable("CAVERARRIA_SAVE")
        ?? Path.Combine(AssetsPath, "Campaigns", CampaignId);
    public static string HealthKey => Environment.GetEnvironmentVariable("CAVERARRIA_SAVE") ?? CampaignId;
    public static bool WantsLoad => Environment.GetEnvironmentVariable("CAVERARRIA_LOAD") is string requested
        ? requested == "1" : HasProfile(SavePath);
    internal static bool HasProfile(string path) => Directory.Exists(path) && Directory.EnumerateFiles(path)
        .Any(file => string.Equals(Path.GetFileName(file), "Profile.dat", StringComparison.OrdinalIgnoreCase));

    public static void MarkCampaignWorld(string? id = null)
    {
        IsCampaignWorld = true;
        CampaignId = string.IsNullOrWhiteSpace(id) ? Main.ActiveWorldFileData.UniqueId.ToString("N") : id;
        if (CampaignId == Guid.Empty.ToString("N")) CampaignId = Guid.NewGuid().ToString("N");
    }
    public static void ClearWorldMarker() { IsCampaignWorld = false; CampaignId = ""; }

    public static void PrepareWorld()
    {
        MarkCampaignWorld();
        Main.worldName = Main.ActiveWorldFileData.Name;
        Main.worldID = Main.ActiveWorldFileData.UniqueId.GetHashCode() & int.MaxValue;
        Main.worldSurface = Math.Min(Main.maxTilesY - 200, 900);
        Main.rockLayer = Math.Min(Main.maxTilesY - 100, 1000);
        Main.dayTime = true; Main.time = 27000;
        Main.spawnTileX = CampaignRuntime.OriginTileX + 30;
        Main.spawnTileY = CampaignRuntime.OriginTileY + 20;
        for (int x = Main.spawnTileX - 12; x <= Main.spawnTileX + 12; x++)
        {
            var tile = Main.tile[x, Main.spawnTileY + 2];
            tile.HasTile = true;
            tile.TileType = (ushort)ModContent.TileType<CampaignSolid>();
        }
    }

    public static void EnterWithCurrentPlayer()
    {
        if (Main.netMode != NetmodeID.SinglePlayer) { Main.NewText("Caverarria currently supports single player."); return; }
        if (IsCampaignWorld) return;
        if (!Main.gameMenu)
        {
            WorldFile.SaveWorld();
            Player.SavePlayer(Main.ActivePlayerFileData);
        }
        Player player = Main.ActivePlayerFileData.Player.SerializedClone();
        player.name += " — Cave Story";
        Start(player);
    }

    public static void AutoStart()
    {
        if (Started || Environment.GetEnvironmentVariable("CAVERARRIA_AUTOSTART") != "1") return;
        Started = true;
        string playerPath = Path.Combine(Main.PlayerPath, "CaverarriaTraveler.plr");
        if (Environment.GetEnvironmentVariable("CAVERARRIA_LOAD") == "1" && File.Exists(playerPath))
        {
            var saved = Player.LoadPlayer(playerPath, false);
            Start(saved.Player, saved);
            return;
        }
        Player player = new() { name = "Island Traveler", statLife = 100, statLifeMax = 100, statMana = 20, statManaMax = 20, hair = 14, hairColor = new Color(52, 85, 122), shirtColor = new Color(232, 235, 235), underShirtColor = new Color(67, 101, 123), pantsColor = new Color(62, 71, 98), shoeColor = new Color(168, 42, 45), skinColor = new Color(241, 202, 170) };
        player.inventory[0].SetDefaults(ItemID.CopperShortsword);
        player.inventory[1].SetDefaults(ItemID.CopperPickaxe);
        player.inventory[2].SetDefaults(ItemID.CopperAxe);
        Start(player);
    }

    private static void Start(Player player, PlayerFileData? saved = null)
    {
        Main.myPlayer = 0;
        Main.mapEnabled = false;
        Directory.CreateDirectory(Main.PlayerPath);
        var data = saved ?? new PlayerFileData(Path.Combine(Main.PlayerPath, "CaverarriaTraveler.plr"), false)
        {
            Player = player,
            Name = player.name,
            Metadata = FileMetadata.FromCurrentSettings(FileType.Player)
        };
        if (saved == null)
        {
            data.SetAsActive();
            Player.SavePlayer(data, true);
            data = Player.LoadPlayer(data.Path, false);
        }
        data.SetAsActive();
        data.StartPlayTimer();

        Main.maxTilesX = 2200;
        Main.maxTilesY = 1200;
        Main.Map = new WorldMap(Main.maxTilesX, Main.maxTilesY);
        WorldGen.clearWorld();
        WorldGen.setWorldSize();
        Main.worldName = WorldName;
        Main.worldID = 10012026;
        Main.worldSurface = 900;
        Main.rockLayer = 1000;
        Main.GameMode = 0;
        Main.dayTime = true;
        Main.time = 27000;
        Main.spawnTileX = CampaignRuntime.OriginTileX + 30;
        Main.spawnTileY = CampaignRuntime.OriginTileY + 20;
        Main.mapEnabled = false;
        Main.ActiveWorldFileData = WorldFile.CreateMetadata(WorldName, false, 0);
        Main.WorldFileMetadata = Main.ActiveWorldFileData.Metadata;
        Main.ActiveWorldFileData.SetWorldSize(Main.maxTilesX, Main.maxTilesY);
        PrepareWorld();
        WorldFile.SaveWorld();
        Main.menuMode = 10;
        WorldGen.playWorld();
    }
}
