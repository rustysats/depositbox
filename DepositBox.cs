using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine; 

namespace Oxide.Plugins
{
    [Info("DepositBox", "rustysats", "0.3.0")]
    [Description("Drop box that registers deposits for admin while removing items from the game. Now with Discord, claim functionality, and optional ServerInfo leaderboard updates (supports Oxide and Carbon).")]
    internal class DepositBox : RustPlugin
    {
        private static DepositBox instance;

        private int DepositItemID;
        private ulong DepositBoxSkinID;
        private string DiscordWebhookUrl;
        private int ClaimRewardBudget;
        private bool UseServerInfo;
        private int ServerInfoUpdateFreq;

        private const string permPlace = "depositbox.place";
        private const string permCreateClaim = "depositbox.createclaim";

        private Dictionary<string, int> depositLog = new Dictionary<string, int>();

        private readonly Dictionary<Item, BasePlayer> depositTrack = new Dictionary<Item, BasePlayer>();

        private Timer serverInfoTimer;

        #region Oxide Hooks

        private void Init()
        {
            instance = this;
            LoadConfiguration();
            LoadDepositLog();
            permission.RegisterPermission(permPlace, this);
            permission.RegisterPermission(permCreateClaim, this);
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file.");
            Config["ClaimRewardBudget"] = "100000";
            Config["DepositBoxSkinID"] = 3406403145;
            Config["DepositItemID"] = -1779183908;
            Config["DiscordWebhookUrl"] = "";
            Config["UseServerInfo"] = false;
            Config["ServerInfoUpdateFreq"] = 30;
            SaveConfig();
        }

        private void OnServerInitialized(bool initial)
        {
            ReinitializeDepositBoxes();

            if (UseServerInfo)
            {
                serverInfoTimer = timer.Every(ServerInfoUpdateFreq * 60, UpdateServerInfoLeaderboard);
            }
        }

        private void Unload()
        {
            if (serverInfoTimer != null)
            {
                serverInfoTimer.Destroy();
                serverInfoTimer = null;
            }

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity is StorageContainer container)
                {
                    var restrictions = container.GetComponents<DepositBoxRestriction>();
                    foreach (var r in restrictions)
                    {
                        UnityEngine.Object.Destroy(r);
                    }
                }
            }
            depositTrack.Clear();

            instance = null;
        }

        private void ReinitializeDepositBoxes()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity is StorageContainer container && container.skinID == DepositBoxSkinID)
                {
                    var restrictions = container.GetComponents<DepositBoxRestriction>();
                    foreach (var restriction in restrictions)
                    {
                        UnityEngine.Object.Destroy(restriction);
                    }
                    var newRestriction = container.gameObject.AddComponent<DepositBoxRestriction>();
                    newRestriction.container = container.inventory;
                    newRestriction.InitDepositBox();
                }
            }
        }

        private void OnEntitySpawned(StorageContainer container)
        {
            if (container == null || container.skinID != DepositBoxSkinID) return;

            if (!container.TryGetComponent(out DepositBoxRestriction mono))
            {
                mono = container.gameObject.AddComponent<DepositBoxRestriction>();
                mono.container = container.inventory;
                mono.InitDepositBox();
            }
        }

        #endregion

        #region Commands

        [ChatCommand("depositbox")]
        private void GiveDepositBox(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permPlace))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }
            player.inventory.containerMain.GiveItem(ItemManager.CreateByItemID(833533164, 1, DepositBoxSkinID));
            player.ChatMessage(lang.GetMessage("BoxGiven", this, player.UserIDString));
        }

        [ChatCommand("createclaim")]
        private void CreateClaimCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permCreateClaim))
            {
                player.ChatMessage(lang.GetMessage("NoCreateClaimPermission", this, player.UserIDString));
                return;
            }
            if (depositLog == null || depositLog.Count == 0)
            {
                player.ChatMessage(lang.GetMessage("NoDepositData", this, player.UserIDString));
                return;
            }
            int totalDeposits = depositLog.Values.Sum();
            if (totalDeposits == 0)
            {
                player.ChatMessage(lang.GetMessage("ZeroDeposits", this, player.UserIDString));
                return;
            }
            var claimData = new Dictionary<string, int>();
            foreach (var entry in depositLog)
            {
                double percentage = (double)entry.Value / totalDeposits;
                int reward = (int)Math.Floor(percentage * ClaimRewardBudget);
                claimData[entry.Key] = reward;
            }
            Interface.Oxide.DataFileSystem.WriteObject("DepositBox_Claim", claimData);
            player.ChatMessage(lang.GetMessage("ClaimFileUpdated", this, player.UserIDString));
        }

        #endregion

        #region DepositBoxRestriction Class

        public class DepositBoxRestriction : FacepunchBehaviour
        {
            public ItemContainer container;

            public void InitDepositBox()
            {
                container.canAcceptItem += CanAcceptItem;
                container.onItemAddedRemoved += OnItemAddedRemoved;
            }

            private bool CanAcceptItem(Item item, int targetPos)
            {
                if (item == null || item.info == null || item.info.itemid != DepositBox.instance.DepositItemID)
                {
                    return false;
                }
                if (item.GetOwnerPlayer() is BasePlayer player)
                {
                    DepositBox.instance.TrackDeposit(item, player);
                }
                return true;
            }

            private void OnItemAddedRemoved(Item item, bool added)
            {
                if (!added || item.info.itemid != DepositBox.instance.DepositItemID) return;
                if (DepositBox.instance.depositTrack.TryGetValue(item, out BasePlayer player))
                {
                    DepositBox.instance.LogDeposit(player, item.amount);
                    DepositBox.instance.depositTrack.Remove(item);
                    item.Remove();
                }
            }

            public void Destroy()
            {
                container.canAcceptItem -= CanAcceptItem;
                container.onItemAddedRemoved -= OnItemAddedRemoved;
                Destroy(this);
            }
        }

        #endregion

        #region Logging and Discord Integration

        private void LogDeposit(BasePlayer player, int depositAmount)
        {
            string steamId = player.UserIDString;
            if (!depositLog.ContainsKey(steamId))
            {
                depositLog[steamId] = 0;
            }
            depositLog[steamId] += depositAmount;
            SaveDepositLog();
            int playerTotalDeposits = depositLog[steamId];
            int totalDepositedByAllPlayers = depositLog.Values.Sum();
            double playerPercentageOfTotal = totalDepositedByAllPlayers > 0 ? (double)playerTotalDeposits / totalDepositedByAllPlayers * 100.0 : 0.0;
            player.ChatMessage(lang.GetMessage("DepositRecorded", this, steamId)
                .Replace("{amount}", depositAmount.ToString(CultureInfo.InvariantCulture))
                .Replace("{total_amount}", playerTotalDeposits.ToString(CultureInfo.InvariantCulture))
                .Replace("{percentage}", playerPercentageOfTotal.ToString("F2", CultureInfo.InvariantCulture)));
            SendDiscordNotification(player, depositAmount, playerTotalDeposits);
        }

        public void TrackDeposit(Item item, BasePlayer player)
        {
            if (item != null && player != null)
            {
                depositTrack[item] = player;
            }
        }

        private void LoadDepositLog()
        {
            depositLog = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, int>>("DepositBoxLog")
                ?? new Dictionary<string, int>();
        }

        private void SaveDepositLog()
        {
            Interface.Oxide.DataFileSystem.WriteObject("DepositBoxLog", depositLog);
        }

        private void SendDiscordNotification(BasePlayer player, int depositAmount, int newTotal)
        {
            if (string.IsNullOrEmpty(DiscordWebhookUrl))
            {
                return;
            }
            string playerName = !string.IsNullOrEmpty(player.displayName) ? player.displayName : player.UserIDString;
            string message = $"{playerName} ({player.UserIDString}) deposited {depositAmount} items. Their total is now {newTotal}.";
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }
            var payload = new Dictionary<string, string> { ["content"] = message };
            string postData = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            webrequest.EnqueuePost(DiscordWebhookUrl, postData, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    PrintWarning($"Discord notification failed: {code} {response}");
                }
            }, this, headers);
        }

        #endregion

        #region ServerInfo Leaderboard Update

        private void UpdateServerInfoLeaderboard()
        {
            string serverInfoPath = GetServerInfoPath();
            if (string.IsNullOrEmpty(serverInfoPath))
            {
                Puts("ServerInfo.json not found in oxide/config or carbon/configs, skipping update.");
                return;
            }
            try
            {
                string fileContent = File.ReadAllText(serverInfoPath);
                var serverInfo = JsonConvert.DeserializeObject<Dictionary<string, object>>(fileContent);
                if (serverInfo == null || !serverInfo.ContainsKey("settings"))
                {
                    Puts("ServerInfo.json format unexpected, skipping update.");
                    return;
                }
                var settings = serverInfo["settings"] as Newtonsoft.Json.Linq.JObject;
                if (settings == null) return;
                var tabs = settings["Tabs"] as Newtonsoft.Json.Linq.JArray;
                if (tabs == null)
                {
                    tabs = new Newtonsoft.Json.Linq.JArray();
                    settings["Tabs"] = tabs;
                }
                var leaderboardLines = new List<string> { $"Leaderboard is updated every {ServerInfoUpdateFreq} minutes.", "" };
                var sorted = depositLog.OrderByDescending(x => x.Value).Take(100).ToList();
                int rank = 1;
                foreach (var kv in sorted)
                {
                    string steamId = kv.Key;
                    int totalDeposited = kv.Value;
                    string name = covalence.Players.FindPlayerById(steamId)?.Name ?? steamId;
                    leaderboardLines.Add($"{rank}. {name} - {totalDeposited} items");
                    rank++;
                }
                int linesPerPage = 20;
                var pages = new List<object>();
                for (int i = 0; i < leaderboardLines.Count; i += linesPerPage)
                {
                    var chunk = leaderboardLines.Skip(i).Take(linesPerPage).ToList();
                    pages.Add(new { TextLines = chunk, ImageSettings = new object[] { } });
                }
                var leaderboardTab = new
                {
                    ButtonText = "Leaderboard",
                    HeaderText = "Top Depositors",
                    Pages = pages.ToArray(),
                    TabButtonAnchor = 4,
                    TabButtonFontSize = 16,
                    HeaderAnchor = 0,
                    HeaderFontSize = 32,
                    TextFontSize = 16,
                    TextAnchor = 3,
                    OxideGroup = ""
                };
                for (int i = tabs.Count - 1; i >= 0; i--)
                {
                    if (tabs[i]["ButtonText"]?.ToString() == "Leaderboard")
                    {
                        tabs.RemoveAt(i);
                    }
                }
                tabs.Add(Newtonsoft.Json.Linq.JObject.FromObject(leaderboardTab));
                File.WriteAllText(serverInfoPath, JsonConvert.SerializeObject(serverInfo, Formatting.Indented));
                Puts("ServerInfo.json updated with leaderboard data.");
                string reloadCmd = GetServerInfoReloadCommand(serverInfoPath);
                if (!string.IsNullOrEmpty(reloadCmd))
                {
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, reloadCmd);
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"Error updating ServerInfo: {ex.Message}");
            }
        }

        private string GetServerInfoPath()
        {
            if (File.Exists("oxide/config/ServerInfo.json"))
                return "oxide/config/ServerInfo.json";
            if (File.Exists("carbon/configs/ServerInfo.json"))
                return "carbon/configs/ServerInfo.json";
            return null;
        }

        private string GetServerInfoReloadCommand(string serverInfoPath)
        {
            if (serverInfoPath.Contains("oxide"))
                return "oxide.reload ServerInfo";
            if (serverInfoPath.Contains("carbon"))
                return "carbon.reload ServerInfo";
            return "";
        }

        #endregion

        #region Configuration

        private void LoadConfiguration()
        {
            DepositItemID = Convert.ToInt32(Config["DepositItemID"], CultureInfo.InvariantCulture);
            DepositBoxSkinID = Convert.ToUInt64(Config["DepositBoxSkinID"], CultureInfo.InvariantCulture);
            DiscordWebhookUrl = Convert.ToString(Config["DiscordWebhookUrl"]);
            ClaimRewardBudget = Convert.ToInt32(Config["ClaimRewardBudget"], CultureInfo.InvariantCulture);
            UseServerInfo = Convert.ToBoolean(Config["UseServerInfo"]);
            ServerInfoUpdateFreq = Convert.ToInt32(Config["ServerInfoUpdateFreq"], CultureInfo.InvariantCulture);
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to place this box.",
                ["BoxGiven"] = "You have received a Deposit Box.",
                ["DepositRecorded"] = "Your deposit of {amount} has been recorded. You have deposited a total of {total_amount} items, which is {percentage}% of all deposits.",
                ["NoCreateClaimPermission"] = "You do not have permission to create a claim file.",
                ["NoDepositData"] = "No deposit data found to create a claim.",
                ["ZeroDeposits"] = "Total deposits are zero, cannot create claim.",
                ["ClaimFileUpdated"] = "Claim file has been created/updated."
            }, this);
        }

        #endregion
    }
}
