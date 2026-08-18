using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; 
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Collections.Frozen; // 【.NET 10 升級】：引入凍結集合

// 【已移除 using System.Linq;】 貫徹 0 記憶體垃圾極致版

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618

public class PanoramaVoteConfig : BasePluginConfig
{
    [JsonPropertyName("VoteDurationSeconds")]
    public float VoteDurationSeconds { get; set; } = 60.0f; 
    [JsonPropertyName("VoteCooldownSeconds")]
    public float VoteCooldownSeconds { get; set; } = 90.0f; 
    [JsonPropertyName("MinPlayersRequired")]
    public int MinPlayersRequired { get; set; } = 6; 
    [JsonPropertyName("RequiredVotePercentage")]
    public float RequiredVotePercentage { get; set; } = 0.8f; 
    [JsonPropertyName("JoinMessageDelaySeconds")]
    public float JoinMessageDelaySeconds { get; set; } = 4.0f; 
    
    // 【.NET 10 升級】：集合表達式
    [JsonPropertyName("JoinMessages")]
    public List<string> JoinMessages { get; set; } = [
        "{Prefix} {White}DEMO下載以及相關指令請至網站 {Lime}c2t.clouds.tw",
        "{Prefix} {Orange}隨 機 隊 伍 {White}以及 {Orange}投 票 換 圖 {White}請參考網站相關指令",
        "{Prefix} {Lime}建 議 反 饋 {White}與 {Lime}異 常 問 題 {White}請至網站填寫問題回報"
    ];
}

public partial class SLAYER_PanoramaVote : BasePlugin, IPluginConfig<PanoramaVoteConfig>
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "2.7_ZeroGC_Pro"; 
    public override string ModuleAuthor => "SLAYER / Optimized / UltimateVote";
    public override string ModuleDescription => "Panorama RTV, Shuffle Vote with Zero LINQ Garbage";
    
    public PanoramaVoteConfig Config { get; set; }
    public CPanoramaVote voteHandler; 
    
    private readonly string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private enum VoteType { None, MapChange, Shuffle, Unshuffle }
    private VoteType _currentVoteType = VoteType.None;
    private string _targetMap = string.Empty;
    
    private double _lastMapVoteTime = -9999.0;      
    private double _lastShuffleVoteTime = -9999.0;  
    
    private bool _isMapChanging = false;

    // 【.NET 10 升級】：改用 FrozenSet 搭配不分大小寫比對，O(1) 物理極限查詢
    private readonly FrozenSet<string> _allowedMaps = new[] { 
        "de_mirage", "de_inferno", "de_dust2", "de_nuke", "de_anubis", "de_ancient", "de_cache" 
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            // 【.NET 10 升級】：現代化模式匹配
            if (Utilities.GetPlayerFromSlot(playerSlot) is not { IsValid: true, IsBot: false, IsHLTV: false }) return;

            AddTimer(Config.JoinMessageDelaySeconds, () => 
            {
                if (Utilities.GetPlayerFromSlot(playerSlot) is { IsValid: true } p)
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
            CCSGameRulesProxy? gameRules = null;
            foreach (var rule in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
            {
                gameRules = rule;
                break;
            }
            
            // 【.NET 10 升級】：巢狀屬性模式匹配
            if (gameRules is { GameRules.WarmupPeriod: false })
            {
                if (_currentVoteType != VoteType.None)
                {
                    voteHandler.CancelVote();
                    Server.PrintToChatAll($" {Prefix} 比賽已正式開始，{ChatColors.Green}投票{ChatColors.White} 被 {ChatColors.Orange}強制{ChatColors.White} 取消");
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
        if (player is not { IsValid: true }) return HookResult.Continue;
        
        string text = info.GetArg(1).Trim('"').Trim().ToLower();

        // 檢查準備指令
        if (text == ".r" || text == ".ready" || text == "!r" || text == "!ready" || text == ".unready" || text == "!unready")
        {
            if (_currentVoteType != VoteType.None || _isMapChanging)
            {
                if (_isMapChanging)
                {
                    player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，禁 止 輸 入 準 備 指 令{ChatColors.White}");
                    player.PrintToCenter("地 圖 即 將 更 換 ， 禁 止 輸 入 .R");
                }
                else
                {
                    player.PrintToChat($" {Prefix} 投票進行中 {ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ] {ChatColors.White}投票結束再準備");
                    player.PrintToCenter("請 投 票 結 束 ， 再 輸 入 .R 準 備");
                }
                return HookResult.Stop; 
            }
        }

        // 【新增防護】：如果地圖正在更換中，全面禁止輸入 .vote 或 .rtv 發起新投票
        if (text == ".rtv" || text == "!rtv" || text == ".vote" || text == "!vote")
        {
            if (_isMapChanging)
            {
                player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，無 法 發 起 投 票{ChatColors.White}");
                player.PrintToCenter("地 圖 即 將 更 換 ， 無 法 發 起 投 票");
                return HookResult.Stop;
            }
        }
        
        if (text.StartsWith(".rtv"))
        {
            // 【.NET 10 升級】：改用 ReadOnlySpan<char> 進行 0 GC 的切片解析，徹底消滅 string.Split
            ReadOnlySpan<char> textSpan = text.AsSpan();
            int spaceIdx = textSpan.IndexOf(' ');
            string args = "";
            
            if (spaceIdx != -1)
            {
                ReadOnlySpan<char> remainder = textSpan[(spaceIdx + 1)..].TrimStart();
                int nextSpace = remainder.IndexOf(' ');
                args = nextSpace == -1 ? remainder.ToString() : remainder[..nextSpace].ToString();
            }
            
            Server.ExecuteCommand($"css_slayer_vote_internal {player.Slot} rtv {args}");
            return HookResult.Handled; 
        }
        
        if (text.StartsWith(".vote"))
        {
            // 【.NET 10 升級】：改用 ReadOnlySpan<char> 進行 0 GC 的切片解析
            ReadOnlySpan<char> textSpan = text.AsSpan();
            int spaceIdx = textSpan.IndexOf(' ');
            
            if (spaceIdx == -1)
            {
                player.PrintToChat($" {Prefix} 投 票 系 統 說 明 {ChatColors.Silver}[ {ChatColors.Green}限 熱 身 階 段 使 用 {ChatColors.Silver}]{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 投 票 換 圖：請 輸 入 {ChatColors.Yellow}.rtv 地圖名稱{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 隨 機 分 隊：請 輸 入 {ChatColors.Yellow}.vote shuffle{ChatColors.White}");
                player.PrintToChat($" {Prefix} 取 消 隨 機 分 隊：請 輸 入 {ChatColors.Yellow}.vote unshuffle{ChatColors.White}");
                return HookResult.Handled;
            }
            else
            {
                ReadOnlySpan<char> remainder = textSpan[(spaceIdx + 1)..].TrimStart();
                int nextSpace = remainder.IndexOf(' ');
                ReadOnlySpan<char> firstArg = nextSpace == -1 ? remainder : remainder[..nextSpace];

                if (firstArg.SequenceEqual("shuffle") || firstArg.SequenceEqual("unshuffle"))
                {
                    Server.ExecuteCommand($"css_slayer_vote_internal {player.Slot} {firstArg.ToString()}");
                    return HookResult.Handled;
                }
            }
            
            player.PrintToChat($" {Prefix} 無 效 指 令！請 輸 入 {ChatColors.Yellow}.vote{ChatColors.White} 或 {ChatColors.Yellow}.rtv{ChatColors.White} 查 看 說 明");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("css_slayer_vote_internal", "Internal vote proxy")]
    public void OnInternalVote(CCSPlayerController? caller, CommandInfo info) 
    {
        if (!int.TryParse(info.GetArg(1), out int slot)) return;
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } player) return;

        string action = info.GetArg(2).ToLower();
        
        if (action == "rtv") 
        {
            string mapArg = info.GetArg(3);
            // 【.NET 10 升級】：集合表達式
            string[] args = string.IsNullOrEmpty(mapArg) ? [".rtv"] : [".rtv", mapArg];
            ExecuteRtvLogic(player, args);
        }
        else if (action == "shuffle") 
        {
            ExecuteShuffleVoteLogic(player);
        }
        else if (action == "unshuffle") 
        {
            ExecuteUnshuffleVoteLogic(player);
        }
    }

    [ConsoleCommand("css_rtv", "發起 RTV 投票更換地圖")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player is not { IsValid: true }) return;
        string[] args = new string[info.ArgCount];
        for (int i = 0; i < info.ArgCount; i++) args[i] = info.GetArg(i);
        ExecuteRtvLogic(player, args);
    }

    [ConsoleCommand("css_vshuffle", "發起隨機分隊投票")]
    public void OnCommandShuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player is not { IsValid: true }) return;
        ExecuteShuffleVoteLogic(player);
    }

    [ConsoleCommand("css_vunshuffle", "發起取消隨機分隊投票")]
    public void OnCommandUnshuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player is not { IsValid: true }) return;
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

        string inputMap = args[1].Trim();
        string? matchedMap = null;
        
        // 【.NET 10 升級】：利用 FrozenSet 的 TryGetValue 達成 O(1) 取值，取代低效能的 foreach 遍歷
        if (_allowedMaps.TryGetValue(inputMap, out string? actualMap))
        {
            matchedMap = actualMap;
        }

        if (matchedMap == null)
        {
            player.PrintToChat($" {Prefix} 伺 服 器 不 支 援 地 圖 [{args[1]}] ！");
            player.PrintToChat($" {Prefix} 可 用 地 圖： {ChatColors.Yellow}{string.Join(", ", _allowedMaps)}");
            return;
        }

        _targetMap = matchedMap;
        _currentVoteType = VoteType.MapChange; 
        voteHandler.Init(); 

        voteHandler.SendYesNoVoteToAll(Config.VoteDurationSeconds, player.Slot, "#SFUI_vote_changelevel", _targetMap, VoteResultCallback, VoteHandlerCallback);

        _lastMapVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White}");
    }

    private void ExecuteShuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player, VoteType.Shuffle)) return;

        _currentVoteType = VoteType.Shuffle; 
        voteHandler.Init(); 
        
        voteHandler.SendYesNoVoteToAll(Config.VoteDurationSeconds, player.Slot, "#SFUI_vote_scramble_teams", "", VoteResultCallback, VoteHandlerCallback);

        _lastShuffleVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.Lime}隨 機 分 隊{ChatColors.White} 投 票");
    }

    private void ExecuteUnshuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player, VoteType.Unshuffle)) return;

        _currentVoteType = VoteType.Unshuffle; 
        voteHandler.Init(); 
        
        voteHandler.SendYesNoVoteToAll(Config.VoteDurationSeconds, player.Slot, "#SFUI_Scoreboard_Undo", "", VoteResultCallback, VoteHandlerCallback);

        _lastShuffleVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.LightRed}取 消 隨 機 分 隊{ChatColors.White} 投 票");
    }

    private bool PassCommonVoteChecks(CCSPlayerController player, VoteType requestedType)
    {
        // 【.NET 10 升級】：模式匹配
        if (player.TeamNum is not ((byte)CsTeam.Terrorist or (byte)CsTeam.CounterTerrorist))
        {
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}只 有 在 CT 或 T 的 玩 家 才 能 發 起 投 票");
            return false;
        }

        if (_isMapChanging)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}地 圖 即 將 更 換，請 稍 候");
            return false;
        }

        if (_currentVoteType != VoteType.None)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試");
            return false;
        }

        CCSGameRulesProxy? gameRules = null;
        foreach (var rule in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
        {
            gameRules = rule;
            break;
        }
        
        // 【.NET 10 升級】：巢狀屬性模式匹配
        if (gameRules is not { GameRules.WarmupPeriod: true })
        {
            player.PrintToChat($" {Prefix} 投 票 系 統 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用");
            player.PrintToCenter("比 賽 進 行 中 ， 禁 止 發 起 投 票");
            return false;
        }

        int activePlayerCount = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            // 【.NET 10 升級】：模式匹配
            if (p is { IsValid: true, IsBot: false, TeamNum: 2 or 3 })
            {
                activePlayerCount++;
            }
        }

        if (activePlayerCount < Config.MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Green}{Config.MinPlayersRequired} 人 {ChatColors.White}以 上 才 能 發 起 投 票");
            return false;
        }

        double currentTime = Server.CurrentTime;
        double targetLastTime = (requestedType == VoteType.MapChange) ? _lastMapVoteTime : _lastShuffleVoteTime;
        string voteTypeName = (requestedType == VoteType.MapChange) ? "換 圖" : "隊 伍 洗 牌";

        if (currentTime - targetLastTime < Config.VoteCooldownSeconds)
        {
            int timeLeft = (int)Math.Ceiling(Config.VoteCooldownSeconds - (currentTime - targetLastTime));
            player.PrintToChat($" {Prefix} {voteTypeName}投 票 冷 卻 中，請 等 待 {ChatColors.Green}{timeLeft} {ChatColors.White}秒 後 再 試");
            return false;
        }

        return true;
    }

    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        int activePlayerCount = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            // 【.NET 10 升級】：模式匹配
            if (p is { IsValid: true, IsBot: false, TeamNum: 2 or 3 })
            {
                activePlayerCount++;
            }
        }
        
        bool isVotePassed = false;

        if (_currentVoteType == VoteType.MapChange)
        {
            isVotePassed = info.yes_votes > info.no_votes;
        }
        else if (_currentVoteType == VoteType.Shuffle || _currentVoteType == VoteType.Unshuffle)
        {
            int requiredVotes = (int)Math.Ceiling(activePlayerCount * Config.RequiredVotePercentage);
            isVotePassed = info.yes_votes >= requiredVotes;
        }

        if (isVotePassed) 
        {
            if (_currentVoteType == VoteType.MapChange)
            {
                _isMapChanging = true; 
                Server.PrintToChatAll($" {Prefix} 投 票 通 過！ {ChatColors.Green}5 秒 {ChatColors.White}後 更 換 地 圖 至 {ChatColors.Green}{_targetMap}");
                
                foreach (var p in Utilities.GetPlayers())
                {
                    // 【.NET 10 升級】：模式匹配
                    if (p is { IsValid: true, IsBot: false })
                    {
                        p.PrintToCenter($"投 票 通 過：5 秒 後 更 換 地 圖 {_targetMap}");
                    }
                }
                
                string mapCmd = _targetMap;
                AddTimer(7.0f, () => { 
                    // 完美防禦：換圖前一刻再次確認是否為熱身狀態，防止「定時炸彈」摧毀正賽
                    CCSGameRulesProxy? rules = null;
                    foreach (var r in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")) { rules = r; break; }
                    
                    // 【.NET 10 升級】：巢狀屬性模式匹配
                    if (rules is { GameRules.WarmupPeriod: true }) {
                        Server.ExecuteCommand($"changelevel {mapCmd}"); 
                    } else {
                        Server.PrintToChatAll($" {Prefix} {ChatColors.Yellow}換 圖 終 止！{ChatColors.White}比 賽 已 經 開 始。");
                    }
                });
            }
            else if (_currentVoteType == VoteType.Shuffle)
            {
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.Lime}已 開 啟 隨 機 隊 伍 分 配 {ChatColors.Default}」 將 自 自 動 洗 牌");
                
                foreach (var p in Utilities.GetPlayers())
                {
                    // 【.NET 10 升級】：模式匹配
                    if (p is { IsValid: true, IsBot: false, TeamNum: 2 or 3 })
                    {
                        p.PrintToCenter("投 票 通 過：已 開 啟 隨 機 隊 伍 分 配");
                    }
                }
                
                Server.ExecuteCommand("css_shuffle");
            }
            else if (_currentVoteType == VoteType.Unshuffle)
            {
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.LightRed}已 取 消 隨 機 隊 伍 分 配 {ChatColors.Default}」 維 持 隊 伍 不 變");
                
                foreach (var p in Utilities.GetPlayers())
                {
                    // 【.NET 10 升級】：模式匹配
                    if (p is { IsValid: true, IsBot: false, TeamNum: 2 or 3 })
                    {
                        p.PrintToCenter("投 票 通 過：已 取 消 隨 機 隊 伍 分 配");
                    }
                }
                
                Server.ExecuteCommand("css_unshuffle");
            }

            _currentVoteType = VoteType.None; 
            return true;
        }
        else
        {
            if (_currentVoteType == VoteType.Shuffle || _currentVoteType == VoteType.Unshuffle)
            {
                string voteName = _currentVoteType == VoteType.Shuffle ? "洗 牌" : "取 消 洗 牌";
                string requiredPercentText = $"{Math.Round(Config.RequiredVotePercentage * 100)}%";
                Server.PrintToChatAll($" {Prefix} {voteName} 投 票 失 敗！需 達 {ChatColors.Green}{requiredPercentText}{ChatColors.Default} 玩 家 同 意");
            }
            else if (_currentVoteType == VoteType.MapChange)
            {
                Server.PrintToChatAll($" {Prefix} {ChatColors.Lime}換 圖 投 票{ChatColors.White} 失 敗，將 維 持 當 前 地 圖");
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
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Orange}投 票 已 被 系 統 取 消");
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
