using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; 
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618

// ==========================================
// 【更新】全數值抽離至設定檔 (Config)
// ==========================================
public class PanoramaVoteConfig : BasePluginConfig
{
    [JsonPropertyName("VoteDurationSeconds")]
    public float VoteDurationSeconds { get; set; } = 60.0f; // 投票持續時間 (預設 60 秒)

    [JsonPropertyName("VoteCooldownSeconds")]
    public float VoteCooldownSeconds { get; set; } = 90.0f; // 【新增】兩次投票之間的冷卻時間 (預設 90 秒)

    [JsonPropertyName("MinPlayersRequired")]
    public int MinPlayersRequired { get; set; } = 6; // 最少需要多少真人玩家才能發起投票 (預設 6 人)

    [JsonPropertyName("RequiredVotePercentage")]
    public float RequiredVotePercentage { get; set; } = 0.8f; // 【新增】洗牌/取消洗牌所需的同意比例 (預設 0.8 即 80%)

    [JsonPropertyName("JoinMessageDelaySeconds")]
    public float JoinMessageDelaySeconds { get; set; } = 4.0f; // 玩家進服文字延遲顯示 (預設 4 秒)

    [JsonPropertyName("JoinMessages")]
    public List<string> JoinMessages { get; set; } = new List<string>
    {
        "{Prefix} {White}DEMO下載以及相關指令請至網站 {Lime}c2t.clouds.tw",
        "{Prefix} {Orange}隨 機 隊 伍 {White}以及 {Orange}投 票 換 圖 {White}請參考網站相關指令",
        "{Prefix} {Lime}建 議 反 饋 {White}與 {Lime}異 常 問 題 {White}請至網站填寫問題回報"
    };
}

public partial class SLAYER_PanoramaVote : BasePlugin, IPluginConfig<PanoramaVoteConfig>
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "2.4_UltimateVote_Config_Pro"; 
    public override string ModuleAuthor => "SLAYER / Optimized / UltimateVote";
    public override string ModuleDescription => "Panorama RTV, Shuffle & Unshuffle Vote with Config";
    
    public PanoramaVoteConfig Config { get; set; }
    public CPanoramaVote voteHandler; 
    
    private readonly string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private enum VoteType { None, MapChange, Shuffle, Unshuffle }
    private VoteType _currentVoteType = VoteType.None;
    private string _targetMap = string.Empty;
    
    private double _lastMapVoteTime = -9999.0;      
    private double _lastShuffleVoteTime = -9999.0;  
    
    private bool _isMapChanging = false;

    private readonly string[] _allowedMaps = { 
        "de_mirage", "de_inferno", "de_dust2", "de_nuke", "de_anubis", "de_ancient", "de_cache" 
    };

    public void OnConfigParsed(PanoramaVoteConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this); 
        _isMapChanging = false;
        _currentVoteType = VoteType.None;
        
        AddCommandListener("say", OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);

        RegisterListener<Listeners.OnClientPutInServer>(playerSlot =>
        {
            var initialPlayer = Utilities.GetPlayerFromSlot(playerSlot);
            if (initialPlayer == null || !initialPlayer.IsValid || initialPlayer.IsBot || initialPlayer.IsHLTV) return;

            AddTimer(Config.JoinMessageDelaySeconds, () => 
            {
                var p = Utilities.GetPlayerFromSlot(playerSlot);
                if (p != null && p.IsValid)
                {
                    foreach (var msg in Config.JoinMessages)
                    {
                        p.PrintToChat(ReplaceColorTags(msg));
                    }
                }
            });
        });

        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            
            if (gameRules != null && gameRules.GameRules != null && !gameRules.GameRules.WarmupPeriod)
            {
                if (_currentVoteType != VoteType.None)
                {
                    voteHandler.CancelVote();
                    Server.PrintToChatAll($" {Prefix} 比賽已正式開始，尚未完成的投票已被系統強制取消");
                    _currentVoteType = VoteType.None;
                }
            }
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnMapStart>(mapName => 
        {
            _isMapChanging = false;
            _currentVoteType = VoteType.None;
            _lastMapVoteTime = -9999.0;      
            _lastShuffleVoteTime = -9999.0;  
        });

        RegisterEventHandler<EventVoteCast>((@event, info) =>
        {
            voteHandler.VoteCast(@event); 
            return HookResult.Continue;
        });
    }

    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        
        string text = info.GetArg(1).Trim('"').Trim().ToLower();

        if (text == ".r" || text == ".ready" || text == "!r" || text == "!ready" || text == ".unready" || text == "!unready")
        {
            if (_currentVoteType != VoteType.None)
            {
                player.PrintToChat($" {Prefix} 投票進行中 {ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ] {ChatColors.White}投票結束再準備");
                return HookResult.Stop; 
            }
        }
        
        if (text.StartsWith(".rtv"))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ExecuteRtvLogic(player, parts);
            return HookResult.Handled; 
        }
        
        if (text.StartsWith(".vote"))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length == 1)
            {
                player.PrintToChat($" {Prefix} 投 票 系 統 說 明 {ChatColors.Silver}[ {ChatColors.Green}限 熱 身 階 段 使 用 {ChatColors.Silver}]{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 投 票 換 圖：請 輸 入 {ChatColors.Yellow}.rtv 地圖名稱{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 隨 機 分 隊：請 輸 入 {ChatColors.Yellow}.vote shuffle{ChatColors.White}");
                player.PrintToChat($" {Prefix} 取 消 隨 機 分 隊：請 輸 入 {ChatColors.Yellow}.vote unshuffle{ChatColors.White}");
                return HookResult.Handled;
            }

            if (parts.Length >= 2 && parts[1] == "shuffle")
            {
                ExecuteShuffleVoteLogic(player);
                return HookResult.Handled;
            }

            if (parts.Length >= 2 && parts[1] == "unshuffle")
            {
                ExecuteUnshuffleVoteLogic(player);
                return HookResult.Handled;
            }
            
            player.PrintToChat($" {Prefix} 無 效 指 令！請 輸 入 {ChatColors.Yellow}.vote{ChatColors.White} 或 {ChatColors.Yellow}.rtv{ChatColors.White} 查 看 說 明");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("css_rtv", "發起 RTV 投票更換地圖")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid) return;
        string[] args = new string[info.ArgCount];
        for (int i = 0; i < info.ArgCount; i++) args[i] = info.GetArg(i);
        ExecuteRtvLogic(player, args);
    }

    [ConsoleCommand("css_vshuffle", "發起隨機分隊投票")]
    public void OnCommandShuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid) return;
        ExecuteShuffleVoteLogic(player);
    }

    [ConsoleCommand("css_vunshuffle", "發起取消隨機分隊投票")]
    public void OnCommandUnshuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid) return;
        ExecuteUnshuffleVoteLogic(player);
    }

    private void ExecuteRtvLogic(CCSPlayerController player, string[] args)
    {
        if (args.Length < 2)
        {
            player.PrintToChat($" {Prefix} 投 票 說 明 {ChatColors.Silver}[ {ChatColors.Green}限 熱 身 階 段 使 用 {ChatColors.Silver}]{ChatColors.White}");
            player.PrintToChat($" {Prefix} 使 用 方 法： {ChatColors.Yellow}.rtv de_mirage{ChatColors.White}");
            player.PrintToChat($" {Prefix} 可 用 地 圖： {ChatColors.Yellow}{string.Join(", ", _allowedMaps)}{ChatColors.White}");
            return;
        }

        if (!PassCommonVoteChecks(player, VoteType.MapChange)) return;

        string inputMap = args[1].Trim().ToLower();
        string? matchedMap = _allowedMaps.FirstOrDefault(m => m.ToLower() == inputMap);

        if (matchedMap == null)
        {
            player.PrintToChat($" {Prefix} 伺 服 器 不 支 援 地 圖 [{args[1]}] ！");
            player.PrintToChat($" {Prefix} 可 用 地 圖： {ChatColors.Yellow}{string.Join(", ", _allowedMaps)}");
            return;
        }

        _targetMap = matchedMap;
        _currentVoteType = VoteType.MapChange; 
        voteHandler.Init(); 

        // 使用 Config.VoteDurationSeconds 代替 60.0f
        voteHandler.SendYesNoVoteToAll(Config.VoteDurationSeconds, player.Slot, "#SFUI_vote_changelevel", _targetMap, VoteResultCallback, VoteHandlerCallback);

        _lastMapVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White}");
    }

    private void ExecuteShuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player, VoteType.Shuffle)) return;

        _currentVoteType = VoteType.Shuffle; 
        voteHandler.Init(); 
        
        // 使用 Config.VoteDurationSeconds 代替 60.0f
        voteHandler.SendYesNoVoteToAll(Config.VoteDurationSeconds, player.Slot, "#SFUI_vote_scramble_teams", "", VoteResultCallback, VoteHandlerCallback);

        _lastShuffleVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.Lime}隨 機 分 隊{ChatColors.White} 投 票");
    }

    private void ExecuteUnshuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player, VoteType.Unshuffle)) return;

        _currentVoteType = VoteType.Unshuffle; 
        voteHandler.Init(); 
        
        // 使用 Config.VoteDurationSeconds 代替 60.0f
        voteHandler.SendYesNoVoteToAll(Config.VoteDurationSeconds, player.Slot, "#SFUI_Scoreboard_Undo", "", VoteResultCallback, VoteHandlerCallback);

        _lastShuffleVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.LightRed}取 消 隨 機 分 隊{ChatColors.White} 投 票");
    }

    private bool PassCommonVoteChecks(CCSPlayerController player, VoteType requestedType)
    {
        if (player.TeamNum != (byte)CsTeam.Terrorist && player.TeamNum != (byte)CsTeam.CounterTerrorist)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}只 有 在 CT 或 T 的 玩 家 才 能 發 起 投 票");
            return false;
        }

        if (_isMapChanging)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，請 稍 候");
            return false;
        }

        if (_currentVoteType != VoteType.None)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試");
            return false;
        }

        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (gameRules == null || gameRules.GameRules == null || !gameRules.GameRules.WarmupPeriod)
        {
            player.PrintToChat($" {Prefix} 投 票 系 統 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用");
            return false;
        }

        int activePlayerCount = Utilities.GetPlayers().Count(p => p != null && p.IsValid && !p.IsBot && (p.TeamNum == 2 || p.TeamNum == 3));

        if (activePlayerCount < Config.MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Green}{Config.MinPlayersRequired} 人 {ChatColors.White}以 上 才 能 發 起 投 票");
            return false;
        }

        double currentTime = Server.CurrentTime;
        double targetLastTime = (requestedType == VoteType.MapChange) ? _lastMapVoteTime : _lastShuffleVoteTime;
        string voteTypeName = (requestedType == VoteType.MapChange) ? "換 圖" : "隊 伍 洗 牌";

        // 【更新】使用 Config.VoteCooldownSeconds
        if (currentTime - targetLastTime < Config.VoteCooldownSeconds)
        {
            int timeLeft = (int)Math.Ceiling(Config.VoteCooldownSeconds - (currentTime - targetLastTime));
            player.PrintToChat($" {Prefix} {voteTypeName}投 票 冷 卻 中，請 等 待 {ChatColors.Green}{timeLeft} {ChatColors.White}秒 後 再 試");
            return false;
        }

        return true;
    }

    // ==========================================
    // 投票結果處理中樞 (更新版：修正換圖失敗的語病)
    // ==========================================
    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        int activePlayerCount = Utilities.GetPlayers().Count(p => p != null && p.IsValid && !p.IsBot && (p.TeamNum == 2 || p.TeamNum == 3));
        bool isVotePassed = false;

        if (_currentVoteType == VoteType.MapChange)
        {
            isVotePassed = info.yes_votes > info.no_votes; // 換圖：多數決
        }
        else if (_currentVoteType == VoteType.Shuffle || _currentVoteType == VoteType.Unshuffle)
        {
            int requiredVotes = (int)Math.Ceiling(activePlayerCount * Config.RequiredVotePercentage);
            isVotePassed = info.yes_votes >= requiredVotes; // 洗牌：8成門檻
        }

        if (isVotePassed) 
        {
            if (_currentVoteType == VoteType.MapChange)
            {
                _isMapChanging = true; 
                Server.PrintToChatAll($" {Prefix} 投 票 通 過！ {ChatColors.Green}5 秒 {ChatColors.White}後 更 換 地 圖 至 {ChatColors.Green}{_targetMap}");
                
                foreach (var p in Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsBot))
                {
                    p.PrintToCenter($"投 票 通 過：5 秒 後 更 換 地 圖 {_targetMap}");
                }
                
                string mapCmd = _targetMap;
                AddTimer(7.0f, () => { Server.ExecuteCommand($"changelevel {mapCmd}"); });
            }
            else if (_currentVoteType == VoteType.Shuffle)
            {
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.Lime}已 開 啟 隨 機 隊 伍 分 配 {ChatColors.Default}」 將 自 動 洗 牌");
                
               foreach (var p in Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsBot && (p.TeamNum == 2 || p.TeamNum == 3)))
                {
                    p.PrintToCenter("投 票 通 過：已 開 啟 隨 機 隊 伍 分 配");
                }
                
                Server.ExecuteCommand("css_shuffle");
            }
            else if (_currentVoteType == VoteType.Unshuffle)
            {
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.LightRed}已 取 消 隨 機 隊 伍 分 配 {ChatColors.Default}」 維 持 隊 伍 不 變");
                
                foreach (var p in Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsBot && (p.TeamNum == 2 || p.TeamNum == 3)))
                {
                    p.PrintToCenter("投 票 通 過：已 取 消 隨 機 隊 伍 分 配");
                }
                
                Server.ExecuteCommand("css_unshuffle");
            }

            _currentVoteType = VoteType.None; 
            return true;
        }
        else
        {
            // === 修正失敗時的提示文字邏輯 ===
            if (_currentVoteType == VoteType.Shuffle || _currentVoteType == VoteType.Unshuffle)
            {
                string voteName = _currentVoteType == VoteType.Shuffle ? "洗 牌" : "取 消 洗 牌";
                string requiredPercentText = $"{Math.Round(Config.RequiredVotePercentage * 100)}%";
                Server.PrintToChatAll($" {Prefix} {voteName} 投 票 失 敗！需 達 {ChatColors.Green}{requiredPercentText}{ChatColors.Default} 玩 家 同 意");
            }
            else if (_currentVoteType == VoteType.MapChange)
            {
                // 換圖失敗專用提示
                Server.PrintToChatAll($" {Prefix} 換 圖 投 票 失 敗，將 維 持 當 前 地 圖");
            }
            
            _currentVoteType = VoteType.None; 
            return false;
        }
    }

    private void VoteHandlerCallback(YesNoVoteAction action, int param1, int param2)
    {
        switch (action)
        {
            case YesNoVoteAction.VoteAction_Start:
                Server.PrintToChatAll($" {Prefix} 投 票 開 始！請 在 左 上 角 選 擇{ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ]");
                break;
            case YesNoVoteAction.VoteAction_Vote:
                break;
            case YesNoVoteAction.VoteAction_End:
                if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_Cancelled)
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 已 被 系 統 取 消。");
                    _currentVoteType = VoteType.None; 
                }
                break;
        } 
    } 
    
    private string ReplaceColorTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        return input
            .Replace("{Prefix}", Prefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{White}", ChatColors.White.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Green}", ChatColors.Green.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Lime}", ChatColors.Lime.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Red}", ChatColors.Red.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LightRed}", ChatColors.LightRed.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{DarkRed}", ChatColors.DarkRed.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Orange}", ChatColors.Orange.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Yellow}", ChatColors.Yellow.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Blue}", ChatColors.Blue.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Silver}", ChatColors.Silver.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
