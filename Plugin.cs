using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using Comfort.Common;
using EFT;
using EFT.Communications;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using SPTushonka.Reflection.Patching;
using SPTushonka.Reflection.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UnityEngine;



#pragma warning disable IDE0051 // Remove unused private members

namespace BossNotifier
{
    [BepInPlugin(PluginGuid, "BossNotifier", PluginVersion)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class BossNotifierPlugin : BasePlugin
    {
        public const string PluginGuid = "Mattexe.BossNotifier";
        public const string PluginVersion = "2.0.0";

        public static PropertyInfo FikaClientTypeProperty;

        // Configuration entries - General
        public static ConfigEntry<KeyboardShortcut> showBossesKeyCode;
        public static ConfigEntry<bool> showNotificationsOnRaidStart;

        // Configuration entries - Intel Center
        public static ConfigEntry<int> intelCenterUnlockLevel;
        public static ConfigEntry<int> intelCenterLocationUnlockLevel;
        public static ConfigEntry<int> intelCenterDetectedUnlockLevel;

        // Configuration entries - Markers
        public static ConfigEntry<bool> enableMarkers;
        public static ConfigEntry<KeyboardShortcut> toggleMarkersKey;
        public static ConfigEntry<bool> showThroughWalls;
        public static ConfigEntry<string> markerCharacter;
        public static ConfigEntry<bool> showBossName;
        public static ConfigEntry<bool> showDistance;
        public static ConfigEntry<int> fontSize;
        public static ConfigEntry<float> markerBaseScale;
        public static ConfigEntry<float> markerMaxScale;
        public static ConfigEntry<int> visibilityDistance;
        public static ConfigEntry<Color> markerColor;

        private static ManualLogSource logger;

        public static event Action OnPluginAwake;
        public static event Action OnRaidStarted;
        public static event Action<string> OnBossDied;
        public static event Action OnRaidEnded;

        // Methods to invoke events (called from BossNotifierMono)
        public static void InvokeOnRaidStarted() => OnRaidStarted?.Invoke();
        public static void InvokeOnBossDied(string bossName) => OnBossDied?.Invoke(bossName);
        public static void InvokeOnRaidEnded() => OnRaidEnded?.Invoke();

        // Logging methods. Named LogMessage because BasePlugin already exposes a Log property.
        public static void LogMessage(LogLevel level, string msg)
        {
            logger?.Log(level, msg);
        }

        // Dictionary mapping boss types to names
        public static readonly Dictionary<WildSpawnType, string> bossNames = new Dictionary<WildSpawnType, string>() {
            { WildSpawnType.bossBully, "Reshala" },
            { WildSpawnType.bossKnight, "Knight" },
            { WildSpawnType.followerBigPipe, "Big Pipe" },
            { WildSpawnType.followerBirdEye, "Birdeye" },
            { WildSpawnType.sectantPriest, "Cultists" },
            { WildSpawnType.bossTagilla, "Tagilla" },
            { WildSpawnType.bossKilla, "Killa" },
            { WildSpawnType.bossZryachiy, "Zryachiy" },
            { WildSpawnType.bossGluhar, "Glukhar" },
            { WildSpawnType.bossSanitar, "Sanitar" },
            { WildSpawnType.bossKojaniy, "Shturman" },
            { WildSpawnType.bossBoar, "Kaban" },
            { WildSpawnType.gifter, "Santa Claus" },
            { WildSpawnType.arenaFighterEvent, "Blood Hounds" },
            { WildSpawnType.crazyAssaultEvent, "Crazy Scavs" },
            { WildSpawnType.exUsec, "Rogues" },
            { WildSpawnType.bossKolontay, "Kollontay" },
            { WildSpawnType.bossPartisan, "Partisan" },

            // --- SPT 5.0 / EFT 1.0 additions ---
            // Cultist variants: same encounter as the priest, so they share the group name.
            { WildSpawnType.sectantPredvestnik, "Cultists" },
            { WildSpawnType.sectantPrizrak, "Cultists" },
            { WildSpawnType.sectantOni, "Cultists" },
            // Labyrinth. The in-game nicknames are literally "Shadow of Tagilla" and
            // "Vengeful Killa" (bot types bosstagillaagro / bosskillaagro).
            { WildSpawnType.bossTagillaAgro, "Shadow of Tagilla" },
            { WildSpawnType.bossKillaAgro, "Vengeful Killa" },
            // Icebreaker (bossWedge, 12 spawn points) and the dark Lab (bossWedgeLab).
            { WildSpawnType.bossWedge, "The Wedge" },
            { WildSpawnType.bossWedgeLab, "The Wedge" },
            // Black Division: the Icebreaker squad lead plus its operatives, which the Pier
            // (terminal) spawns as scripted ambush groups.
            { WildSpawnType.bossBullyBlackDiv, "Black Division" },
            { WildSpawnType.blackDivision, "Black Division" },
            { WildSpawnType.pmcBotBlackDiv, "Black Division" },
            // Russian military squads on the Pier, plus the Shoreline gate tower sentry.
            { WildSpawnType.vsRF, "Russian Military" },
            { WildSpawnType.vsRFSniper, "Russian Military" },
            { WildSpawnType.sentry, "Russian Military" },
            // Zryachiy's revenge event is the same boss showing up again.
            { WildSpawnType.ravangeZryachiyEvent, "Zryachiy" },

            { (WildSpawnType)4206927, "Punisher" },
            { (WildSpawnType)199, "Legion" },
        };

        // Custom boss configuration
        public static CustomBossConfig customBossConfig;
        public static Dictionary<int, string> customBossLookup = new Dictionary<int, string>(); // ID -> groupName (for notifications)
        public static Dictionary<int, string> customBossDisplayNames = new Dictionary<int, string>(); // ID -> displayName (for markers)
        public static HashSet<string> customBossGroups = new HashSet<string>(); // All custom group names

        // Custom group marker settings (created dynamically based on installed mods)
        public static Dictionary<string, ConfigEntry<string>> customGroupMarkerCharacters = new Dictionary<string, ConfigEntry<string>>();
        public static Dictionary<string, ConfigEntry<Color>> customGroupMarkerColors = new Dictionary<string, ConfigEntry<Color>>();
        public static HashSet<string> loadedCustomMods = new HashSet<string>(); // Track which mods are installed

        // Per-group counters for "all members eliminated" check on plural vanilla bosses (Cultists,
        // Blood Hounds, Crazy Scavs, Rogues) and custom groups. Reset between raids.
        public static Dictionary<string, int> groupSeenCount = new Dictionary<string, int>();
        public static Dictionary<string, int> groupDeadCount = new Dictionary<string, int>();

        // A plural/custom group is "eliminated" only when at least one member was seen and all seen members died.
        // Avoids false-positive "X eliminated" when no members spawned yet (group could spawn later).
        public static bool IsGroupFullyEliminated(string groupName)
        {
            int seen;
            if (!groupSeenCount.TryGetValue(groupName, out seen) || seen == 0) return false;
            int dead;
            groupDeadCount.TryGetValue(groupName, out dead);
            return dead >= seen;
        }

        // Set of plural boss names
        public static HashSet<string> pluralBosses = new HashSet<string>() {
            "Goons",
            "Cultists",
            "Blood Hounds",
            "Crazy Scavs",
            "Rogues",
            "Black Division",
            "Russian Military",
        };

        // Goons members (for grouping)
        public static readonly HashSet<string> goonMembers = new HashSet<string>() {
            "Knight",
            "Big Pipe",
            "Birdeye",
        };

        // Dictionary mapping zone IDs to names
        public static readonly Dictionary<string, string> zoneNames = new Dictionary<string, string>() {
            // === CUSTOMS ===
            {"ZoneBrige", "Bridge" },
            {"ZoneCrossRoad", "Crossroads" },
            {"ZoneDormitory", "Dormitory" },
            {"ZoneGasStation", "Gas Station" },
            {"ZoneFactoryCenter", "Factory Center" },
            {"ZoneFactorySide", "Factory Side" },
            {"ZoneOldAZS", "Old Gas Station" },
            {"ZoneBlockPost", "Checkpoint" },
            {"ZoneTankSquare", "Old Construction" },
            {"ZoneWade", "RUAF Roadblock" },
            {"ZoneCustoms", "Customs" },
            {"ZoneScavBase", "Scav Base" },

            // === FACTORY ===
            {"BotZone", "" },

            // === INTERCHANGE ===
            {"ZoneCenterBot", "Center Floor 2" },
            {"ZoneCenter", "Center Floor 1" },
            {"ZoneOLI", "OLI" },
            {"ZoneIDEA", "IDEA" },
            {"ZoneGoshan", "Goshan" },
            {"ZoneIDEAPark", "IDEA Parking" },
            {"ZoneOLIPark", "OLI Parking" },
            {"ZoneTrucks", "Trucks" },
            {"ZoneRoad", "Road" },
            {"ZonePowerStation", "Power Station" },

            // === LABS ===
            {"BotZoneFloor1", "Floor 1" },
            {"BotZoneFloor2", "Floor 2" },
            {"BotZoneBasement", "Basement" },
            {"BotZoneGate1", "Gate 1" },
            {"BotZoneGate2", "Gate 2" },

            // === LIGHTHOUSE ===
            {"Zone_Containers", "Containers" },
            {"Zone_Rocks", "Rocks" },
            {"Zone_Chalet", "Chalet" },
            {"Zone_Village", "Village" },
            {"Zone_Bridge", "Bridge" },
            {"Zone_OldHouse", "Old House" },
            {"Zone_LongRoad", "Long Road" },
            {"Zone_RoofBeach", "Roof Beach" },
            {"Zone_DestroyedHouse", "Destroyed House" },
            {"Zone_RoofContainers", "Roof Containers" },
            {"Zone_Blockpost", "Checkpoint" },
            {"Zone_RoofRocks", "Roof Rocks" },
            {"Zone_TreatmentRocks", "Treatment Rocks" },
            {"Zone_TreatmentContainers", "Treatment Containers" },
            {"Zone_TreatmentBeach", "Treatment Beach" },
            {"Zone_Hellicopter", "Helicopter" },
            {"Zone_SniperPeak", "Sniper Peak" },
            {"Zone_Island", "Island" },

            // === RESERVE ===
            {"ZoneRailStrorage", "Rail Storage" },
            {"ZonePTOR1", "Black Pawn" },
            {"ZonePTOR2", "White Knight" },
            {"ZoneBarrack", "Barracks" },
            {"ZoneBunkerStorage", "Bunker Storage" },
            {"ZoneSubStorage", "Sub Storage" },
            {"ZoneSubCommand", "Sub Command" },

            // === GROUND ZERO ===
            {"ZoneSandbox", "" },

            // === SHORELINE ===
            {"ZoneGreenHouses", "Green Houses" },
            {"ZoneIsland", "Island" },
            {"ZoneForestGasStation", "Forest Gas Station" },
            {"ZoneBunker", "Bunker" },
            {"ZoneBusStation", "Bus Station" },
            {"ZonePort", "Pier" },
            {"ZoneForestTruck", "Forest Truck" },
            {"ZoneForestSpawn", "Forest" },
            {"ZoneSanatorium1", "Sanatorium West" },
            {"ZoneSanatorium2", "Sanatorium East" },
            {"ZoneStartVillage", "Village" },
            {"ZoneMeteoStation", "Weather Station" },
            {"ZoneRailWays", "Railways" },
            {"ZoneSmuglers", "Smugglers" },
            {"ZonePassClose", "Pass" },
            {"ZoneTunnel", "Tunnel" },

            // === STREETS ===
            {"ZoneSW01", "SW01" },
            {"ZoneConstruction", "Construction" },
            {"ZoneCarShowroom", "Car Showroom" },
            {"ZoneCinema", "Cinema" },
            {"ZoneFactory", "Factory" },
            {"ZoneHotel_1", "Hotel 1" },
            {"ZoneHotel_2", "Hotel 2" },
            {"ZoneConcordia_1", "Concordia" },
            {"ZoneConcordiaParking", "Concordia Parking" },
            {"ZoneColumn", "Column" },
            {"ZoneSW00", "SW00" },
            {"ZoneStilo", "Stilo" },
            {"ZoneCard1", "Cardinal" },
            {"ZoneMvd", "MVD" },
            {"ZoneClimova", "Klimova" },

            // === WOODS ===
            {"ZoneWoodCutter", "Wood Cutter" },
            {"ZoneHouse", "House" },
            {"ZoneBigRocks", "Big Rocks" },
            {"ZoneHighRocks", "High Rocks" },
            {"ZoneMiniHouse", "Mini House" },
            {"ZoneRedHouse", "Red House" },
            {"ZoneScavBase2", "Scav Base" },
            {"ZoneClearVill", "Village" },
            {"ZoneBrokenVill", "Broken Village" },
            {"ZoneUsecBase", "USEC Base" },
            {"ZoneStoneBunker", "Stone Bunker" },
            {"ZoneDepo", "Depot" },

            // === ICEBREAKER ===
            {"BotZoneMash_t1", "Machine Room" },
            {"BotZoneFront", "Bow" },
            {"BotZoneFront2", "Bow" },
            {"BotZoneKitchen", "Kitchen" },
            {"BotZoneKorrDown1", "Lower Corridor" },
            {"BotZoneRoom_Eng", "Engine Room" },
            {"BotZoneRoom_Eng2", "Engine Room" },
            {"BotZoneEngineHide", "Engine Hideout" },
            {"BotZoneStern", "Stern" },
            {"BotZoneRoomsThird", "Third Deck Rooms" },
            {"BotZoneRoomsThirdKitchen", "Third Deck Kitchen" },
            {"BotZoneRoomsFour", "Fourth Deck Rooms" },
            {"BotZoneArmory", "Armory" },
            {"BotZoneGreenOffice", "Green Office" },

            // === EXTRA ZONES ON EXISTING MAPS ===
            {"ZoneTagilla", "Underground" },
            {"ZoneSnipeCarShowroom", "Car Showroom Rooftop" },
            {"ZoneGateTowerRight", "Gate Tower (Right)" },
            {"ZoneGateTowerLeft", "Gate Tower (Left)" },

            // === TERMINAL (PIER) ===
            {"Zone1BD1PortAmbush1", "Port Ambush" },
            {"Zone1BD1HangarAmbush11", "Hangar Ambush" },
            {"Zone1BD1HangarBD11", "Hangar" },
            {"Zone1BDGateAmbush13", "Gate Ambush" },
            {"Zone2BDWarehouse18", "Warehouse" },
            {"Zone2BDWarehouse19", "Warehouse" },
            {"Zone2BDColumns23", "Columns" },
            {"Zone2BDPort28", "Port" },
            {"Zone2BDPort30", "Port" },
            {"Zone2BDPort31", "Port" },
            {"Zone2BDPort32", "Port" },
            {"Zone2ScavPort29", "Scav Port" },
            {"Zone1VSRF1Spawn3", "VS RF Spawn" },
            {"Zone1VSRFSnipeRoofPort2", "Port Rooftop" },
            {"Zone1VSRFStoreAmbush10", "Store Ambush" },
            {"Zone2VSRFPassMiddle15", "Middle Pass" },
            {"Zone2VSRFColumns22", "Columns" },
            {"Zone2VSRFContainers24", "Containers" },
            {"Zone2VSRFExit33", "Exit" },
        };

        public override void Load()
        {
            logger = Log;

            // ConfigEntry<Color> only works once the TOML converter exists; BepInEx ships
            // converters for primitives and enums only, so register it before any Bind<Color>.
            RegisterColorConverter();

            // Namespace moved from Fika.Core.Coop.Utils -> Fika.Core.Main.Utils, and the host/client
            // indicator became the "ClientType" property (EClientType.Host == 2) instead of a
            // "MatchingType" field. Fika is optional, so everything here is null-tolerant.
            Type fikaUtilExternalType = Type.GetType("Fika.Core.Main.Utils.FikaBackendUtils, Fika.Core", false);
            if (fikaUtilExternalType != null)
            {
                FikaClientTypeProperty = AccessTools.Property(fikaUtilExternalType, "ClientType");
            }

            // Initialize configuration entries - General
            showBossesKeyCode = Config.Bind("1. 通用", "快捷键", new KeyboardShortcut(KeyCode.O), "按下后显示 Boss 通知。");
            showNotificationsOnRaidStart = Config.Bind("1. 通用", "开局显示 Boss", true, "进入战局时自动显示 Boss 通知。");

            // Initialize configuration entries - Intel Center
            intelCenterUnlockLevel = Config.Bind("2. 情报中心解锁（4 表示禁用）", "1. 情报中心等级要求", 0,
                new ConfigDescription("达到该情报中心等级后，解锁基础通知。",
                new AcceptableValueRange<int>(0, 4)));

            intelCenterLocationUnlockLevel = Config.Bind("2. 情报中心解锁（4 表示禁用）", "2. 情报中心位置等级要求", 0,
                new ConfigDescription("达到该等级后，通知中显示 Boss 所在区域。",
                new AcceptableValueRange<int>(0, 4)));

            intelCenterDetectedUnlockLevel = Config.Bind("2. 情报中心解锁（4 表示禁用）", "3. 情报中心侦测等级要求", 0,
                new ConfigDescription("达到该等级后，靠近 Boss 时显示侦测通知。",
                new AcceptableValueRange<int>(0, 4)));

            // Initialize configuration entries - Markers
            enableMarkers = Config.Bind("3. Boss 标记", "1. 启用标记", true, "在 Boss 头顶显示 3D 标记。");

            toggleMarkersKey = Config.Bind("3. Boss 标记", "2. 标记开关快捷键", new KeyboardShortcut(KeyCode.P), "按下后切换标记显示。");

            showThroughWalls = Config.Bind("3. Boss 标记", "3. 穿墙显示", true, "开启后标记可穿墙看到；关闭后只在视线内显示。");

            markerCharacter = Config.Bind("3. Boss 标记", "4. 标记符号", "▼",
                new ConfigDescription("标记使用的符号。",
                new AcceptableValueList<string>("▼", "▽", "↓", "●", "◆", "★", "☠", "◉", "▲")));

            showBossName = Config.Bind("3. Boss 标记", "5. 显示 Boss 名称", true, "在标记上显示 Boss 名称。");

            showDistance = Config.Bind("3. Boss 标记", "6. 显示距离", true, "在标记上显示与 Boss 的距离。");

            fontSize = Config.Bind("3. Boss 标记", "7. 字体大小", 64,
                new ConfigDescription("标记的字体大小。",
                new AcceptableValueRange<int>(32, 128)));

            markerBaseScale = Config.Bind("3. Boss 标记", "8. 基础缩放", 0.04f,
                new ConfigDescription("标记的基础缩放比例。",
                new AcceptableValueRange<float>(0.01f, 0.2f)));

            markerMaxScale = Config.Bind("3. Boss 标记", "9. 最大缩放", 0.3f,
                new ConfigDescription("远距离时的最大缩放比例。",
                new AcceptableValueRange<float>(0.1f, 1f)));

            visibilityDistance = Config.Bind("3. Boss 标记", "10. 可见距离", 200,
                new ConfigDescription("标记可见的最大距离（米）。",
                new AcceptableValueRange<int>(50, 500)));

            markerColor = Config.Bind("3. Boss 标记", "11. 标记颜色", new Color(1f, 0f, 0f, 1f), "Boss 标记的颜色（默认红色）。");


            LoadCustomBossConfig();

            // IL2CPP plugins cannot define MonoBehaviours out of the box: the game's il2cpp
            // runtime has to be told about them before AddComponent can use them.
            ClassInjector.RegisterTypeInIl2Cpp<BossNotifierMono>();

            // Enable patches
            new BossLocationSpawnPatch().Enable();
            new NewGamePatch().Enable();
            new BotBossPatch().Enable();

            // Subscribe to config changes
            Config.SettingChanged += Config_SettingChanged;

            logger.LogInfo($"Plugin BossNotifier v{PluginVersion} is loaded!");

            // Invoke event for addon to hook into
            OnPluginAwake?.Invoke();
        }

        // The default TOML converters cover primitives and enums only, so a Color entry would
        // throw while serialising its default value.
        private static void RegisterColorConverter()
        {
            // Another plugin (flir.enemymarkers) usually registers a Color converter first;
            // adding a second one only logs a warning, so reuse whatever is already there.
            if (TomlTypeConverter.CanConvert(typeof(Color))) return;

            TomlTypeConverter.AddConverter(typeof(Color), new BepInEx.Configuration.TypeConverter
            {
                ConvertToString = (obj, type) =>
                {
                    Color color = (Color)obj;
                    return string.Format(CultureInfo.InvariantCulture, "{0:R} {1:R} {2:R} {3:R}", color.r, color.g, color.b, color.a);
                },
                ConvertToObject = (str, type) =>
                {
                    string[] parts = str.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) return new Color(1f, 1f, 1f, 1f);

                    float channel(int index)
                    {
                        float value;
                        return float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
                    }

                    return new Color(channel(0), channel(1), channel(2), parts.Length > 3 ? channel(3) : 1f);
                }
            });
        }

        // Event handler for configuration changes
        private void Config_SettingChanged(object sender, SettingChangedEventArgs e)
        {
            // If player is in a raid, reset their notifications to reflect changes
            if (BossNotifierMono.Instance != null) BossNotifierMono.Instance.GenerateBossNotifications();
        }

        // Get boss name by type (for notifications - returns group name for custom bosses)
        public static string GetBossName(WildSpawnType type)
        {
            // Check vanilla bosses first
            if (bossNames.ContainsKey(type)) return bossNames[type];

            // Check custom bosses - return group name
            int typeId = (int)type;
            if (customBossLookup.ContainsKey(typeId)) return customBossLookup[typeId];

            return null;
        }

        // Get boss display name for markers (individual role name)
        public static string GetBossDisplayName(WildSpawnType type)
        {
            // Check vanilla bosses first - they use their normal name
            if (bossNames.ContainsKey(type)) return bossNames[type];

            // Check custom bosses - use individual display name
            int typeId = (int)type;
            if (customBossDisplayNames.ContainsKey(typeId)) return customBossDisplayNames[typeId];

            return null;
        }

        // Get marker character for a specific group (custom groups have their own settings)
        public static string GetMarkerCharacter(string groupName)
        {
            if (customGroupMarkerCharacters.ContainsKey(groupName))
                return customGroupMarkerCharacters[groupName].Value;

            return markerCharacter.Value; // Default to vanilla setting
        }

        // Get marker color for a specific group (custom groups have their own settings)
        public static Color GetMarkerColor(string groupName)
        {
            if (customGroupMarkerColors.ContainsKey(groupName))
                return customGroupMarkerColors[groupName].Value;

            return markerColor.Value; // Default to vanilla setting
        }

        // Check if a WildSpawnType is a boss
        public static bool IsBoss(WildSpawnType type)
        {
            // Check vanilla bosses
            if (bossNames.ContainsKey(type)) return true;

            // Check custom bosses
            int typeId = (int)type;
            return customBossLookup.ContainsKey(typeId);
        }

        // Look a zone up in the table without falling back to name derivation. Bot zones report
        // their own label, which may already be readable ("Scav Base") or a raw id
        // ("ZoneDormitory"); only the raw form needs translating, and running an already
        // readable label through GetZoneName would insert stray spaces.
        public static string GetMappedZoneName(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;

            string mapped;
            return zoneNames.TryGetValue(zoneId, out mapped) ? mapped : zoneId;
        }

        // Get zone name by ID. Returns an empty string when the zone carries no useful label
        // (Factory's plain "BotZone"), and a derived name for zones with no entry yet.
        public static string GetZoneName(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return "";

            if (zoneNames.ContainsKey(zoneId)) return zoneNames[zoneId];

            string location = zoneId.Replace("Bot", "").Replace("Zone", "");
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < location.Length; i++)
            {
                char c = location[i];
                if (char.IsUpper(c) && i != 0 && i < location.Length - 1 && !char.IsUpper(location[i + 1]) && !char.IsDigit(location[i + 1]))
                {
                    sb.Append(" ");
                }
                sb.Append(c);
            }
            return sb.ToString().Replace("_", " ").Trim();
        }

        // Check if a BepInEx plugin is loaded
        private bool IsPluginLoaded(string pluginGUID)
        {
            if (string.IsNullOrEmpty(pluginGUID)) return true; // No GUID = always load

            // BepInEx 6 has no BepInEx.Bootstrap.Chainloader type; the IL2CPP chainloader keeps
            // the same plugin registry.
            IL2CPPChainloader chainloader = IL2CPPChainloader.Instance;
            if (chainloader == null) return false;

            return chainloader.Plugins.ContainsKey(pluginGUID);
        }

        // Default colors for custom groups
        private static readonly Dictionary<string, Color> defaultGroupColors = new Dictionary<string, Color>()
        {
            { "RUAF", new Color(0.2f, 0.6f, 0.2f, 1f) },           // Green
            { "UNTAR", new Color(0.3f, 0.5f, 1f, 1f) },            // Blue
            { "Black Division", new Color(0.1f, 0.1f, 0.1f, 1f) }  // Dark gray/black
        };

        private void LoadCustomBossConfig()
        {
            string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string configPath = Path.Combine(pluginFolder, "custom_bosses.json");

            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);

                    // Newtonsoft.Json only ships as an IL2CPP proxy here, which cannot
                    // deserialise into a plain managed POCO, so use System.Text.Json.
                    customBossConfig = JsonSerializer.Deserialize<CustomBossConfig>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (customBossConfig?.customBossGroups != null)
                    {
                        int loadedGroups = 0;
                        int sectionIndex = 4; // Start at 4 (after "3. Boss Markers")

                        foreach (var group in customBossConfig.customBossGroups)
                        {
                            // Check if the mod is installed
                            if (!IsPluginLoaded(group.pluginGUID))
                            {
                                logger.LogInfo($"Skipping {group.groupName} - mod not installed (GUID: {group.pluginGUID})");
                                continue;
                            }

                            logger.LogInfo($"Mod detected: {group.groupName} (GUID: {group.pluginGUID})");
                            loadedCustomMods.Add(group.groupName);

                            // Add to lookup dictionaries
                            foreach (var member in group.members)
                            {
                                customBossLookup[member.id] = group.groupName;
                                customBossDisplayNames[member.id] = member.displayName;
                            }

                            // Track group names
                            customBossGroups.Add(group.groupName);

                            // Add to pluralBosses if needed
                            if (group.isPlural)
                            {
                                pluralBosses.Add(group.groupName);
                            }

                            // Create config entries for this group
                            string sectionName = $"{sectionIndex}. {group.groupName} 标记";

                            // Get default color for this group
                            Color defaultColor = defaultGroupColors.ContainsKey(group.groupName)
                                ? defaultGroupColors[group.groupName]
                                : new Color(1f, 0.5f, 0f, 1f); // Orange fallback

                            customGroupMarkerCharacters[group.groupName] = Config.Bind(
                                sectionName,
                                "标记符号",
                                "▼",
                                new ConfigDescription($"{group.groupName} 使用的标记符号。",
                                new AcceptableValueList<string>("▼", "▽", "↓", "●", "◆", "★", "☠", "◉", "▲")));

                            customGroupMarkerColors[group.groupName] = Config.Bind(
                                sectionName,
                                "标记颜色",
                                defaultColor,
                                $"{group.groupName} 标记的颜色。");

                            loadedGroups++;
                            sectionIndex++;
                        }

                        logger.LogInfo($"Loaded {loadedGroups} custom boss groups with {customBossLookup.Count} total IDs");
                    }
                }
                else
                {
                    logger.LogWarning($"Custom boss config not found at: {configPath}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to load custom boss config: {ex.Message}");
            }
        }
    }

    #region Patches
    // Patch for tracking boss location spawns
    public class BossLocationSpawnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BossLocationSpawn).GetMethod("Init");

        public static Dictionary<string, string> bossesInRaid = new Dictionary<string, string>();

        private static void TryAddBoss(string boss, string location)
        {
            if (location == null)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Error, "Tried to add boss with null location.");
                return;
            }
            if (bossesInRaid.ContainsKey(boss))
            {
                if (!bossesInRaid[boss].Contains(location) && !location.Equals(""))
                {
                    if (bossesInRaid[boss].Equals(""))
                    {
                        bossesInRaid[boss] = location;
                    }
                    else
                    {
                        bossesInRaid[boss] += ", " + location;
                    }
                }
            }
            else
            {
                bossesInRaid.Add(boss, location);
            }
        }

        [PatchPostfix]
        private static void PatchPostfix(BossLocationSpawn __instance)
        {
            if (!__instance.ShallSpawn) return;

            // Custom mods (RUAF / UNTAR / Black Division) inject BossLocationSpawn entries
            // with delays, random timing, BossChance < 100, or trigger conditions. ShallSpawn
            // is true at raid start but the bots may spawn minutes later, only on a trigger,
            // or never. Skip them here and let OnPersonAdd / BotBossPatch register them once
            // they actually spawn into the world.
            if (BossNotifierPlugin.customBossLookup.ContainsKey((int)__instance.BossType)) return;

            string name = BossNotifierPlugin.GetBossName(__instance.BossType);
            if (name == null) return;

            string location = GetSpawnLocations(__instance);
            BossNotifierPlugin.LogMessage(LogLevel.Info, $"Boss {name} @ zone {GetRawZone(__instance)} translated to {location}");

            TryAddBoss(name, location);
        }

        // SPT 5.0 moved this field on the wire: the spawn entries now ship "BossZone", and one
        // entry can list several comma separated zones where the boss may appear. Keep reading
        // "BornZone" as a fallback so pre-5.0 location data still works.
        private static string GetRawZone(BossLocationSpawn spawn)
        {
            string zone = spawn.BossZone;
            if (string.IsNullOrEmpty(zone)) zone = spawn.BornZone;
            return zone;
        }

        private static string GetSpawnLocations(BossLocationSpawn spawn)
        {
            string raw = GetRawZone(spawn);
            if (string.IsNullOrEmpty(raw)) return "";

            List<string> locations = new List<string>();
            foreach (string part in raw.Split(','))
            {
                string zone = part.Trim();
                if (zone.Length == 0) continue;

                string translated = BossNotifierPlugin.GetZoneName(zone);
                if (!locations.Contains(translated)) locations.Add(translated);
            }

            return string.Join(", ", locations);
        }
    }

    // Patch for tracking live boss spawns
    public class BotBossPatch : ModulePatch
    {
        // IL2CPP also emits a pointer constructor, so picking [0] is no longer deterministic.
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(BotBoss), new[] { typeof(BotOwner) });

        public static HashSet<string> spawnedBosses = new HashSet<string>();
        public static HashSet<string> deadBosses = new HashSet<string>();
        public static HashSet<string> vicinityNotificationsSent = new HashSet<string>(); // FIX: Track sent notifications
        public static Queue<string> vicinityNotifications = new Queue<string>();

        // Boss name -> the live bot, so the notification can report the zone the boss actually
        // spawned in. SpawnBotZone may not be assigned yet when the BotBoss constructor runs,
        // so the lookup is deferred until notifications are generated.
        public static Dictionary<string, BotOwner> bossOwners = new Dictionary<string, BotOwner>();

        // The zone a bot really spawned in, as opposed to every zone its BossLocationSpawn
        // entry lists as a candidate.
        public static string ResolveSpawnZone(BotOwner owner)
        {
            try
            {
                if (owner == null) return null;

                BotZone zone = owner.SpawnBotZone;
                if (zone == null) return null;

                string nameZone = zone.NameZone;
                string shortName = zone.ShortName;

                if (!string.IsNullOrEmpty(nameZone)) return BossNotifierPlugin.GetMappedZoneName(nameZone);
                if (!string.IsNullOrEmpty(shortName)) return BossNotifierPlugin.GetMappedZoneName(shortName);
            }
            catch (Exception ex)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Warning, $"Could not read the spawn zone: {ex.Message}");
            }

            return null;
        }

        [PatchPostfix]
        private static void PatchPostfix(BotBoss __instance)
        {
            WildSpawnType role = __instance.Owner.Profile.Info.Settings.Role;
            string name = BossNotifierPlugin.GetBossName(role);
            if (name == null) return;

            Vector3 positionVector = __instance.Player().Position;
            string position = $"{(int)positionVector.x}, {(int)positionVector.y}, {(int)positionVector.z}";
            BossNotifierPlugin.LogMessage(LogLevel.Info, $"{name} has spawned at {position} on {Singleton<GameWorld>.Instance.LocationId}");

            spawnedBosses.Add(name);
            bossOwners[name] = __instance.Owner;

            // Diagnostic: SpawnBotZone may still be unassigned this early, in which case the
            // notification resolves it later from the stored bot.
            string zoneAtSpawn = ResolveSpawnZone(__instance.Owner);
            BossNotifierPlugin.LogMessage(LogLevel.Info, $"{name} spawn zone at spawn time: {zoneAtSpawn ?? "(not assigned yet)"}");

            // Fallback: register the boss in bossesInRaid if BossLocationSpawn.Init didn't.
            // Hits two cases: custom mod bosses spawned dynamically (RUAF/UNTAR/Black Division),
            // and Fika clients that haven't received the sync packet yet.
            if (!BossLocationSpawnPatch.bossesInRaid.ContainsKey(name))
            {
                BossLocationSpawnPatch.bossesInRaid[name] = "";
                BossNotifierPlugin.LogMessage(LogLevel.Info, $"Registered {name} in bossesInRaid via spawn fallback");
                if (BossNotifierMono.Instance != null) BossNotifierMono.Instance.GenerateBossNotifications();
            }

            // Use "Goons" for vicinity notification if it's a goon member
            string notifName = BossNotifierPlugin.goonMembers.Contains(name) ? "Goons" : name;

            // FIX: Only send one vicinity notification per boss/group
            if (!vicinityNotificationsSent.Contains(notifName))
            {
                vicinityNotificationsSent.Add(notifName);
                bool isPlural = BossNotifierPlugin.pluralBosses.Contains(notifName);
                vicinityNotifications.Enqueue($"{notifName} {(isPlural ? "have" : "has")} been detected in your vicinity.");
            }
        }
    }

    // Patch for hooking when a raid is started
    internal class NewGamePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(GameWorld).GetMethod("OnGameStarted");

        [PatchPrefix]
        public static void PatchPrefix()
        {
            BossNotifierMono.Init();
        }
    }
    #endregion

    #region BossMarkerInfo
    // Class to hold marker information. The marker itself is drawn in OnGUI: legacy
    // UnityEngine.TextMesh has no registered IL2CPP class pointer in this build, so world
    // space text objects cannot be created at all.
    public class BossMarkerInfo
    {
        public Player Player { get; set; }
        public string BossName { get; set; }      // Display name (shown on marker)
        public string GroupName { get; set; }     // Group name (for death tracking/notifications)

        public BossMarkerInfo(Player player, string bossName, string groupName)
        {
            Player = player;
            BossName = bossName;
            GroupName = groupName;
        }
    }
    #endregion

    #region BossNotifierMono
    public class BossNotifierMono : MonoBehaviour
    {
        // Injected IL2CPP classes need this constructor; the game never calls it directly.
        public BossNotifierMono(IntPtr ptr) : base(ptr) { }

        public static BossNotifierMono Instance;
        private List<string> bossNotificationMessages;
        public int intelCenterLevel;

        // Marker system
        private GameWorld _gameWorld;
        private Dictionary<Player, BossMarkerInfo> _bossMarkers;
        private bool _markersVisible = true;
        private Camera _mainCamera;
        private Action<IPlayer> _onPersonAddHandler;
        private float _pendingNotificationDelay = -1f;
        private bool _showBossesKeyHeld;
        private bool _toggleMarkersKeyHeld;
        private static GUIStyle _markerStyle;

        #region Initialization
        public static void Init()
        {
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null) return;

            BossNotifierPlugin.LogMessage(LogLevel.Info, $"Game started on map {gameWorld.LocationId}");

            Instance = gameWorld.GetOrAddComponent<BossNotifierMono>();

            // Get Intel Center level
            try
            {
                var session = ClientAppUtils.GetMainApp().GetClientBackEndSession();
                var areas = session.Profile.Hideout.Areas;

                Instance.intelCenterLevel = 0;
                if (areas != null)
                {
                    for (int i = 0; i < areas.Length; i++)
                    {
                        var area = areas[i];
                        if (area != null && area.AreaType == EAreaType.IntelligenceCenter)
                        {
                            Instance.intelCenterLevel = area.Level;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Warning, $"Could not get Intel Center level: {ex.Message}");
                Instance.intelCenterLevel = 0;
            }

            BossNotifierPlugin.LogMessage(LogLevel.Info, $"Intel Center level: {Instance.intelCenterLevel}");
        }

        public void Start()
        {
            _gameWorld = Singleton<GameWorld>.Instance;
            _mainCamera = Camera.main;
            _markersVisible = BossNotifierPlugin.enableMarkers.Value;
            _bossMarkers = new Dictionary<Player, BossMarkerInfo>();
            bossNotificationMessages = new List<string>();

            // Register for player spawn events - the interop event takes a managed delegate,
            // which is kept around so OnDestroy can unsubscribe.
            if (_gameWorld != null)
            {
                _onPersonAddHandler = new Action<IPlayer>(OnPersonAdd);
                _gameWorld.OnPersonAdd += _onPersonAddHandler;
                BossNotifierPlugin.LogMessage(LogLevel.Info, "Registered OnPersonAdd event");
            }

            // Initialize markers for already spawned players (bosses)
            InitializeExistingBossMarkers();

            GenerateBossNotifications();

            // Notify addon that raid has started
            BossNotifierPlugin.InvokeOnRaidStarted();

            if (!BossNotifierPlugin.showNotificationsOnRaidStart.Value) return;

            // Delayed first notification: a timer in Update instead of MonoBehaviour.Invoke,
            // which would have to resolve a managed method by name on an injected class.
            _pendingNotificationDelay = 2f;
        }

        private void InitializeExistingBossMarkers()
        {
            if (_gameWorld == null || _gameWorld.AllAlivePlayersList == null) return;

            foreach (var player in _gameWorld.AllAlivePlayersList)
            {
                if (player == null || player.IsYourPlayer) continue;

                var settings = player.Profile?.Info?.Settings;
                if (settings == null) continue;

                WildSpawnType role = settings.Role;
                if (BossNotifierPlugin.IsBoss(role))
                {
                    string groupName = BossNotifierPlugin.GetBossName(role);          // Group name for notifications
                    string displayName = BossNotifierPlugin.GetBossDisplayName(role); // Display name for markers
                    if (groupName != null && !_bossMarkers.ContainsKey(player))
                    {
                        CreateBossMarker(player, displayName ?? groupName, groupName);

                        // FIX: Subscribe to death event for existing bosses
                        player.OnPlayerDeadOrUnspawn += OnBossDeadOrUnspawn;

                        RegisterCustomBossInRaid(role, groupName);
                    }
                }
            }
        }

        // Custom bosses are intentionally skipped by BossLocationSpawnPatch (delayed/triggered/random
        // spawns). Once the bot is actually in the world we add it to bossesInRaid so the player
        // sees the "located" notification only when the boss is genuinely present.
        private void RegisterCustomBossInRaid(WildSpawnType role, string groupName)
        {
            if (!BossNotifierPlugin.customBossLookup.ContainsKey((int)role)) return;
            if (BossLocationSpawnPatch.bossesInRaid.ContainsKey(groupName)) return;

            BossLocationSpawnPatch.bossesInRaid[groupName] = "";
            BossNotifierPlugin.LogMessage(LogLevel.Info, $"Registered custom boss {groupName} in bossesInRaid via OnPersonAdd");
            GenerateBossNotifications();
        }
        #endregion

        #region Marker Creation
        private void OnPersonAdd(IPlayer iPlayer)
        {
            // Interop hands out a distinct wrapper for the IPlayer view of the same native
            // object, so "as Player" would return null - TryCast unwraps the native pointer.
            Player player = iPlayer?.TryCast<Player>();
            if (player == null || player.IsYourPlayer) return;

            var settings = player.Profile?.Info?.Settings;
            if (settings == null) return;

            WildSpawnType role = settings.Role;

            // Check if this is a boss
            if (BossNotifierPlugin.IsBoss(role))
            {
                string groupName = BossNotifierPlugin.GetBossName(role);          // Group name for notifications
                string displayName = BossNotifierPlugin.GetBossDisplayName(role); // Display name for markers
                if (groupName != null)
                {
                    BossNotifierPlugin.LogMessage(LogLevel.Info, $"Boss detected via OnPersonAdd: {displayName ?? groupName} ({groupName})");
                    CreateBossMarker(player, displayName ?? groupName, groupName);

                    // Subscribe to death event
                    player.OnPlayerDeadOrUnspawn += OnBossDeadOrUnspawn;

                    RegisterCustomBossInRaid(role, groupName);
                }
            }
        }

        private void CreateBossMarker(Player player, string bossName, string groupName)
        {
            if (_bossMarkers.ContainsKey(player)) return;

            try
            {
                // Nothing is instantiated here any more: the marker is drawn from this entry
                // in OnGUI, which avoids the unavailable TextMesh type entirely.
                _bossMarkers[player] = new BossMarkerInfo(player, bossName, groupName);

                // Track seen count for plural / custom groups so "all eliminated" only fires once everyone is dead
                if (BossNotifierPlugin.pluralBosses.Contains(groupName) || BossNotifierPlugin.customBossGroups.Contains(groupName))
                {
                    int seen;
                    BossNotifierPlugin.groupSeenCount.TryGetValue(groupName, out seen);
                    BossNotifierPlugin.groupSeenCount[groupName] = seen + 1;
                }

                BossNotifierPlugin.LogMessage(LogLevel.Info, $"Created marker for boss: {bossName} (group: {groupName})");
            }
            catch (Exception ex)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Error, $"Failed to create marker for {bossName}: {ex.Message}");
            }
        }

        private void OnBossDeadOrUnspawn(Player player)
        {
            if (player == null) return;

            player.OnPlayerDeadOrUnspawn -= OnBossDeadOrUnspawn;

            if (_bossMarkers.TryGetValue(player, out var markerInfo))
            {
                BossNotifierPlugin.LogMessage(LogLevel.Info, $"Boss died/unspawned: {markerInfo.BossName} (group: {markerInfo.GroupName})");

                // For plural and custom groups (Cultists, Blood Hounds, RUAF, ...) we count individual deaths
                // and let IsGroupFullyEliminated decide. For single-instance bosses and Goons individuals
                // (Knight/Big Pipe/Birdeye - the goons block in GenerateBossNotifications checks each by name)
                // we mark the boss dead immediately.
                bool isPluralOrCustom = BossNotifierPlugin.pluralBosses.Contains(markerInfo.GroupName)
                                        || BossNotifierPlugin.customBossGroups.Contains(markerInfo.GroupName);
                if (isPluralOrCustom)
                {
                    int dead;
                    BossNotifierPlugin.groupDeadCount.TryGetValue(markerInfo.GroupName, out dead);
                    BossNotifierPlugin.groupDeadCount[markerInfo.GroupName] = dead + 1;
                }
                else
                {
                    BotBossPatch.deadBosses.Add(markerInfo.GroupName);
                }

                // Notify addon of boss death (use GroupName for consistency)
                BossNotifierPlugin.InvokeOnBossDied(markerInfo.GroupName);

                _bossMarkers.Remove(player);

                // FIX: Regenerate notifications after boss death
                GenerateBossNotifications();
            }
        }
        #endregion

        #region Update Loop
        public void Update()
        {
            // Handle vicinity notifications
            if (BotBossPatch.vicinityNotifications.Count > 0)
            {
                string notif = BotBossPatch.vicinityNotifications.Dequeue();
                if (Instance.intelCenterLevel >= BossNotifierPlugin.intelCenterDetectedUnlockLevel.Value)
                {
                    BossNotifierPlugin.LogMessage(LogLevel.Info, $"Vicinity notification: {notif}");
                    ShowNotification(notif, ENotificationDurationType.Long);
                    Instance.GenerateBossNotifications();
                }
            }

            // Handle the delayed raid-start notification
            if (_pendingNotificationDelay > 0f)
            {
                _pendingNotificationDelay -= Time.deltaTime;
                if (_pendingNotificationDelay <= 0f)
                {
                    _pendingNotificationDelay = -1f;
                    SendBossNotifications();
                }
            }

            // Handle keyboard shortcuts. IsPressed() polls the held state and the edge is
            // detected here, so a press is not lost if a frame is ever skipped - unlike
            // IsDown(), which has to observe the exact key-down frame.
            bool showBossesHeld = BossNotifierPlugin.showBossesKeyCode.Value.IsPressed();
            if (showBossesHeld && !_showBossesKeyHeld)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Info, "Show-bosses key pressed.");
                SendBossNotifications();
            }
            _showBossesKeyHeld = showBossesHeld;

            // Handle marker toggle key
            bool toggleMarkersHeld = BossNotifierPlugin.toggleMarkersKey.Value.IsPressed();
            if (toggleMarkersHeld && !_toggleMarkersKeyHeld)
            {
                _markersVisible = !_markersVisible;
                string status = _markersVisible ? "ON" : "OFF";
                ShowNotification($"Boss Markers: {status}", ENotificationDurationType.Default);
            }
            _toggleMarkersKeyHeld = toggleMarkersHeld;

            // Drop markers whose boss is gone; the drawing itself happens in OnGUI.
            if (_bossMarkers != null && _bossMarkers.Count > 0)
            {
                CleanupDeadMarkers();
            }
        }

        // 5.0 dropped the two argument overload, and the surviving one wants an icon type and
        // an explicit "no colour" nullable. rawText must stay true: with false the game runs
        // the string through its localisation/formatting step, which blanks out a mod's own text.
        private static void ShowNotification(string message, ENotificationDurationType duration)
        {
            NotificationManager.DisplayMessageNotification(
                message,
                duration,
                ENotificationIconType.Default,
                default(Il2CppSystem.Nullable<Color>),
                true);
        }

        private void CleanupDeadMarkers()
        {
            List<Player> toRemove = null;

            foreach (var kvp in _bossMarkers)
            {
                Player player = kvp.Key;
                if (player == null) continue;
                if (player.HealthController != null && !player.HealthController.IsAlive)
                {
                    if (toRemove == null) toRemove = new List<Player>();
                    toRemove.Add(player);
                }
            }

            if (toRemove == null) return;

            foreach (Player player in toRemove)
            {
                _bossMarkers.Remove(player);
            }
        }

        // Markers are drawn as IMGUI labels over the boss's world position. Legacy TextMesh
        // cannot be used here: it has no registered IL2CPP class pointer in this build. This
        // matches how the shipped flir.enemymarkers mod draws its labels.
        public void OnGUI()
        {
            if (!BossNotifierPlugin.enableMarkers.Value || !_markersVisible) return;
            if (_bossMarkers == null || _bossMarkers.Count == 0) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null) return;
            }

            GUISkin skin = GUI.skin;
            if (skin == null) return;

            if (_markerStyle == null)
            {
                _markerStyle = new GUIStyle(skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    richText = false
                };
            }

            // The font size setting is expressed on the old world-space scale (32-128); a
            // quarter of it reads well as screen pixels. The two scale settings keep
            // controlling how much the label grows towards the visibility limit.
            int baseFontPx = Mathf.Clamp(BossNotifierPlugin.fontSize.Value / 4, 8, 40);
            float growth = Mathf.Clamp(
                BossNotifierPlugin.markerMaxScale.Value / Mathf.Max(BossNotifierPlugin.markerBaseScale.Value, 0.0001f),
                1f, 2f);

            Vector3 cameraPos = _mainCamera.transform.position;
            int visibility = BossNotifierPlugin.visibilityDistance.Value;

            foreach (var kvp in _bossMarkers)
            {
                Player player = kvp.Key;
                BossMarkerInfo info = kvp.Value;
                if (player == null || info == null) continue;
                if (player.HealthController != null && !player.HealthController.IsAlive) continue;

                float distance = Vector3.Distance(cameraPos, player.Position);
                if (distance > visibility) continue;

                if (!BossNotifierPlugin.showThroughWalls.Value && !HasLineOfSight(cameraPos, player, distance)) continue;

                Vector3 screenPos = _mainCamera.WorldToScreenPoint(player.Position + Vector3.up * 2.2f);
                if (screenPos.z <= 0f) continue;

                float distanceFactor = Mathf.Clamp01(distance / visibility);
                _markerStyle.fontSize = Mathf.RoundToInt(baseFontPx * (1f + (growth - 1f) * distanceFactor));
                _markerStyle.normal.textColor = BossNotifierPlugin.GetMarkerColor(info.GroupName);

                string text = BossNotifierPlugin.GetMarkerCharacter(info.GroupName);
                if (BossNotifierPlugin.showBossName.Value) text += "\n" + info.BossName;
                if (BossNotifierPlugin.showDistance.Value) text += $"\n{distance:F0}m";

                // IMGUI's origin is the top left, screen coordinates start at the bottom.
                Rect rect = new Rect(screenPos.x - 150f, (Screen.height - screenPos.y) - 60f, 300f, 120f);
                GUI.Label(rect, text, _markerStyle);
            }
        }

        private static bool HasLineOfSight(Vector3 cameraPos, Player player, float distance)
        {
            Vector3 bossHeadPos = player.Position + Vector3.up * 1.5f;
            Vector3 direction = bossHeadPos - cameraPos;

            int layerMask = (1 << 12) | (1 << 11) | (1 << 16);

            RaycastHit hit;
            if (Physics.Raycast(cameraPos, direction.normalized, out hit, distance, layerMask))
            {
                return hit.distance >= distance - 1f;
            }

            return true;
        }
        #endregion

        #region Notifications
        private void SendBossNotifications()
        {
            if (!ShouldFunction())
            {
                BossNotifierPlugin.LogMessage(LogLevel.Info, "Boss notifications suppressed (Fika client, not host).");
                return;
            }
            if (intelCenterLevel < BossNotifierPlugin.intelCenterUnlockLevel.Value)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Info, $"Boss notifications suppressed: Intel Center level {intelCenterLevel} < required {BossNotifierPlugin.intelCenterUnlockLevel.Value}.");
                return;
            }

            if (bossNotificationMessages == null || bossNotificationMessages.Count == 0)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Info, "Boss notifications: no bosses located, showing placeholder.");
                ShowNotification("No Bosses Located", ENotificationDurationType.Long);
                return;
            }

            BossNotifierPlugin.LogMessage(LogLevel.Info, $"Boss notifications: displaying {bossNotificationMessages.Count} message(s).");
            foreach (var bossMessage in bossNotificationMessages)
            {
                BossNotifierPlugin.LogMessage(LogLevel.Info, $"  -> {bossMessage}");
                ShowNotification(bossMessage, ENotificationDurationType.Long);
            }
        }

        // Prefer the zone the boss really spawned in, and fall back to the candidate list from its
        // spawn entry. Called often, so the diagnostic is emitted once per boss.
        private static readonly HashSet<string> locationLogged = new HashSet<string>();

        private static string ResolveLocation(string bossName, string fallback)
        {
            BotOwner owner;
            if (BotBossPatch.bossOwners.TryGetValue(bossName, out owner))
            {
                string zone = BotBossPatch.ResolveSpawnZone(owner);
                if (!string.IsNullOrEmpty(zone))
                {
                    if (locationLogged.Add(bossName))
                    {
                        BossNotifierPlugin.LogMessage(LogLevel.Info, $"{bossName}: using actual spawn zone '{zone}' (candidates were '{fallback}')");
                    }
                    return zone;
                }
            }

            return fallback;
        }

        public void GenerateBossNotifications()
        {
            bossNotificationMessages = new List<string>();

            bool isDayTime = IsDay();
            bool isLocationUnlocked = intelCenterLevel >= BossNotifierPlugin.intelCenterLocationUnlockLevel.Value;
            bool isDetectionUnlocked = intelCenterLevel >= BossNotifierPlugin.intelCenterDetectedUnlockLevel.Value;

            // Track if we already added Goons notification
            bool goonsNotificationAdded = false;
            string goonsLocation = "";
            bool goonsDetected = false;

            foreach (var bossSpawn in BossLocationSpawnPatch.bossesInRaid)
            {
                if (isDayTime && bossSpawn.Key.Equals("Cultists")) continue;

                // Report the zone the boss actually spawned in. Its BossLocationSpawn entry only
                // lists every zone the boss may use, so without this the message stays vague
                // ("Dormitory, Gas Station, Scav Base") instead of naming the real place.
                string location = ResolveLocation(bossSpawn.Key, bossSpawn.Value);

                // Handle Goons as a group
                if (BossNotifierPlugin.goonMembers.Contains(bossSpawn.Key))
                {
                    if (!goonsNotificationAdded)
                    {
                        goonsLocation = location;
                        goonsDetected = BotBossPatch.spawnedBosses.Contains("Knight") ||
                                       BotBossPatch.spawnedBosses.Contains("Big Pipe") ||
                                       BotBossPatch.spawnedBosses.Contains("Birdeye");

                        // Check if ALL goons are dead
                        bool allGoonsDead = BotBossPatch.deadBosses.Contains("Knight") &&
                                           BotBossPatch.deadBosses.Contains("Big Pipe") &&
                                           BotBossPatch.deadBosses.Contains("Birdeye");

                        string goonsMessage;
                        if (allGoonsDead)
                        {
                            goonsMessage = "Goons have been eliminated. ☠";
                        }
                        else if (!isLocationUnlocked || goonsLocation == null || goonsLocation.Equals(""))
                        {
                            goonsMessage = $"Goons have been located.{(isDetectionUnlocked && goonsDetected ? " ✓" : "")}";
                        }
                        else
                        {
                            goonsMessage = $"Goons have been located near {goonsLocation}.{(isDetectionUnlocked && goonsDetected ? " ✓" : "")}";
                        }
                        bossNotificationMessages.Add(goonsMessage);
                        goonsNotificationAdded = true;
                    }
                    continue;
                }

                bool isDetected = BotBossPatch.spawnedBosses.Contains(bossSpawn.Key);

                // Plural and custom groups: "eliminated" only when every seen member is dead.
                // Single-instance bosses: deadBosses set is authoritative.
                bool isDead;
                if (BossNotifierPlugin.pluralBosses.Contains(bossSpawn.Key) || BossNotifierPlugin.customBossGroups.Contains(bossSpawn.Key))
                {
                    isDead = BossNotifierPlugin.IsGroupFullyEliminated(bossSpawn.Key);
                }
                else
                {
                    isDead = BotBossPatch.deadBosses.Contains(bossSpawn.Key);
                }

                string notificationMessage;

                if (isDead)
                {
                    notificationMessage = $"{bossSpawn.Key} {(BossNotifierPlugin.pluralBosses.Contains(bossSpawn.Key) ? "have" : "has")} been eliminated. ☠";
                }
                else if (!isLocationUnlocked || string.IsNullOrEmpty(location))
                {
                    notificationMessage = $"{bossSpawn.Key} {(BossNotifierPlugin.pluralBosses.Contains(bossSpawn.Key) ? "have" : "has")} been located.{(isDetectionUnlocked && isDetected ? " ✓" : "")}";
                }
                else
                {
                    notificationMessage = $"{bossSpawn.Key} {(BossNotifierPlugin.pluralBosses.Contains(bossSpawn.Key) ? "have" : "has")} been located near {location}.{(isDetectionUnlocked && isDetected ? " ✓" : "")}";
                }
                bossNotificationMessages.Add(notificationMessage);
            }
        }

        private bool IsDay()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld != null)
            {
                int hour = gameWorld.GameDateTime.Calculate().Hour;
                return hour >= 7 && hour < 22;
            }
            return false;
        }
        #endregion

        #region Cleanup
        public void OnDestroy()
        {
            if (_gameWorld != null)
            {
                if (_onPersonAddHandler != null) _gameWorld.OnPersonAdd -= _onPersonAddHandler;
                _onPersonAddHandler = null;
            }

            if (_bossMarkers != null)
            {
                foreach (var markerInfo in _bossMarkers.Values)
                {
                    if (markerInfo.Player != null)
                    {
                        markerInfo.Player.OnPlayerDeadOrUnspawn -= OnBossDeadOrUnspawn;
                    }
                }
                _bossMarkers.Clear();
            }

            // Interop can hand out a second wrapper for the same native object, so reference
            // equality would miss the match and leave a dead instance behind.
            if (Instance != null && Instance.Pointer == Pointer) Instance = null;

            BossLocationSpawnPatch.bossesInRaid.Clear();
            BotBossPatch.spawnedBosses.Clear();
            BotBossPatch.deadBosses.Clear();
            BotBossPatch.vicinityNotificationsSent.Clear(); // FIX: Clear this too
            BotBossPatch.bossOwners.Clear();
            locationLogged.Clear();
            BossNotifierPlugin.groupSeenCount.Clear();
            BossNotifierPlugin.groupDeadCount.Clear();

            // Notify addon that raid ended
            BossNotifierPlugin.InvokeOnRaidEnded();
        }
        #endregion

        #region Utility
        public bool ShouldFunction()
        {
            if (BossNotifierPlugin.FikaClientTypeProperty == null) return true;
            return (int)BossNotifierPlugin.FikaClientTypeProperty.GetValue(null) == 2;
        }
        #endregion
    }
    #endregion

    #region Component extensions
    internal static class ComponentExtensions
    {
        // BepInEx 6 ships no GetOrAddComponent helper, and IL2CPP has no
        // AddComponent(System.Type) overload, so roll our own.
        internal static T GetOrAddComponent<T>(this Component component) where T : Component
        {
            T existing = component.GetComponent<T>();
            if (existing != null) return existing;

            return component.gameObject.AddComponent<T>();
        }
    }
    #endregion
}