using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; 
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Collections.Frozen; 

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
    public override string ModuleVersion => "3.0_StrictDot_ZeroGC"; 
    public override string ModuleAuthor => "SLAYER / Optimized / UltimateVote";
    public override string ModuleDescription => "Panorama RTV, Shuffle Vote with True Zero Allocation (Strict Dot Only)";
    
    public PanoramaVoteConfig Config { get; set; }
    public CPanoramaVote voteHandler; 
    
    private readonly string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private enum VoteType { None, MapChange, Shuffle, Unshuffle }
    private VoteType _currentVoteType = VoteType.None;
    private string _targetMap = string.Empty;
    
    private double _lastMapVoteTime = -9999.0;      
    private double _lastShuffleVoteTime = -9999.0;  
    
    private bool _isMapChanging = false;

    private static readonly FrozenSet<string> _allowedMaps = new[] { 
        "de_mirage", "de_inferno", "de_dust2", "de_nuke", "de_anubis", "de_ancient", "de_cache" 
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // ▼▼▼ 【0-GC 預先快取區】：消除執行期重複字串拼接、陣列與委派配置 ▼▼▼
    private static readonly string _allowedMapsText = string.Join(", ", _allowedMaps);
    private static readonly string[] _emptyRtvArgs = [".rtv"];

    private static readonly string _colorWhite = ChatColors.White.ToString();
    private static readonly string _colorGreen = ChatColors.Green.ToString();
    private static readonly string _colorLime = ChatColors.Lime.ToString();
    private static readonly string _colorRed = ChatColors.Red.ToString();
    private static readonly string _colorLightRed = ChatColors.LightRed.ToString();
    private static readonly string _colorDarkRed = ChatColors.DarkRed.ToString();
    private static readonly string _colorOrange = ChatColors.Orange.ToString();
    private static readonly string _colorYellow = ChatColors.Yellow.ToString();
    private static readonly string _colorBlue = ChatColors.Blue.ToString();
    private static readonly string _colorSilver = ChatColors.Silver.ToString();

    private string[] _formattedJoinMessages = [];
    private string _cachedRequiredPercentText = "80%";

    private CCSGameRulesProxy? _cachedGameRulesProxy = null;
    private YesNoVoteResult? _cachedVoteResultCallback;
    private YesNoVoteHandler? _cachedVoteHandlerCallback;
    // ▲▲▲ ▲▲▲ ▲▲▲

    public void OnConfigParsed(PanoramaVoteConfig config)
    {
        Config = config;
        _cachedRequiredPercentText = $"{Math.Round(Config.RequiredVotePercentage * 100)}%";

        // 【0-GC 優化】：在載入設定檔時一次性完成顏色標籤替換，玩家進服時 0 GC 直接印出
        if (Config.JoinMessages is { Count: > 0 })
        {
            _formattedJoinMessages = new string[Config.JoinMessages.Count];
            for (int i = 0; i < Config.JoinMessages.Count; i++)
            {
                _formattedJoinMessages[i] = ReplaceColorTags(Config.JoinMessages[i]);
            }
        }
        else
        {
            _formattedJoinMessages = [];
        }
    }

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this); 
        _isMapChanging = false;
        _currentVoteType = VoteType.None;

        _cachedVoteResultCallback = VoteResultCallback;
        _cachedVoteHandlerCallback = VoteHandlerCallback;
        
        AddCommandListener("say", OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);

        RegisterListener<Listeners.OnClientPutInServer>(playerSlot =>
        {
            var initialPlayer = Utilities.GetPlayerFromSlot(playerSlot);
            if (initialPlayer is not { IsValid: true, IsBot: false, IsHLTV: false }) return;

            AddTimer(Config.JoinMessageDelaySeconds, () => 
            {
                var p = Utilities.GetPlayerFromSlot(playerSlot);
                if (p is { IsValid: true })
                {
                    // 【0-GC 優化】：直接讀取預先格式化好的快取陣列，免除每次進服跑 33 次 Replace
                    foreach (var msg in _formattedJoinMessages)
                    {
                        p.PrintToChat(msg);
                    }
                }
            });
        });

        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            if (GetGameRules() is { WarmupPeriod: false })
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
            _cachedGameRulesProxy = null; 
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
        
        ReadOnlySpan<char> textSpan = info.GetArg(1).AsSpan().Trim('"').Trim();
        if (textSpan.IsEmpty) return HookResult.Continue;

        // 【0-GC 極速短路】：本插件為 Strict Dot Only，若首字元不是 '.'，代表是一般聊天，1 個 CPU 指令瞬間放行！
        if (textSpan[0] != '.') return HookResult.Continue;

        if (IsReadyCommand(textSpan))
        {
            if (_currentVoteType != VoteType.None || _isMapChanging)
            {
                if (_isMapChanging)
                {
                    player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，禁 止 輸 入 準 備 指令{ChatColors.White}");
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

        if (IsVoteStartCommand(textSpan))
        {
            if (_isMapChanging)
            {
                player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，無 法 發 起 投 票{ChatColors.White}");
                player.PrintToCenter("地 圖 即 將 更 換 ， 無 法 發 起 投 票");
                return HookResult.Stop;
            }
        }

        if (textSpan.StartsWith(".rtv", StringComparison.OrdinalIgnoreCase))
        {
            int spaceIndex = textSpan.IndexOf(' ');
            string args = spaceIndex == -1 ? "" : textSpan[(spaceIndex + 1)..].ToString();
            
            Server.ExecuteCommand($"css_slayer_vote_internal {player.Slot} rtv {args}");
            return HookResult.Handled; 
        }
        
        if (textSpan.StartsWith(".vote", StringComparison.OrdinalIgnoreCase))
        {
            int spaceIndex = textSpan.IndexOf(' ');
            
            if (spaceIndex == -1) 
            {
                player.PrintToChat($" {Prefix} 投 票 系 統 說 明 {ChatColors.Silver}[ {ChatColors.Green}限 熱 身 階 段 使 用 {ChatColors.Silver}]{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 投 票 換 圖：請 輸 入 {ChatColors.Yellow}.rtv 地圖名稱{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 隨 機 分 隊：請 輸 入 {ChatColors.Yellow}.vote shuffle{ChatColors.White}");
                player.PrintToChat($" {Prefix} 取 消 隨 機 分 隊：請 輸 入 {ChatColors.Yellow}.vote unshuffle{ChatColors.White}");
                return HookResult.Handled;
            }

            ReadOnlySpan<char> argSpan = textSpan[(spaceIndex + 1)..].Trim();
            if (argSpan.Equals("shuffle", StringComparison.OrdinalIgnoreCase))
            {
                Server.ExecuteCommand($"css_slayer_vote_internal {player.Slot} shuffle");
                return HookResult.Handled;
            }
            if (argSpan.Equals("unshuffle", StringComparison.OrdinalIgnoreCase))
            {
                Server.ExecuteCommand($"css_slayer_vote_internal {player.Slot} unshuffle");
                return HookResult.Handled;
            }
            
            player.PrintToChat($" {Prefix} 無 效 指 令！請 輸 入 {ChatColors.Yellow}.vote{ChatColors.White} 或 {ChatColors.Yellow}.rtv{ChatColors.White} 查 看 說 明");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    private static bool IsReadyCommand(ReadOnlySpan<char> text)
    {
        return text.Equals(".r", StringComparison.OrdinalIgnoreCase) || 
               text.Equals(".ready", StringComparison.OrdinalIgnoreCase) || 
               text.Equals(".unready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVoteStartCommand(ReadOnlySpan<char> text)
    {
        return text.Equals(".rtv", StringComparison.OrdinalIgnoreCase) || 
               text.Equals(".vote", StringComparison.OrdinalIgnoreCase);
    }

    [ConsoleCommand("css_slayer_vote_internal", "Internal vote proxy")]
    public void OnInternalVote(CCSPlayerController? caller, CommandInfo info) 
    {
        if (!int.TryParse(info.GetArg(1), out int slot)) return;
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true }) return;

        // 【0-GC 優化】：改用 ReadOnlySpan 比對，消除 .ToLower() 暫存字串配置
        ReadOnlySpan<char> actionSpan = info.GetArg(2).AsSpan().Trim();
        
        if (actionSpan.Equals("rtv", StringComparison.OrdinalIgnoreCase)) 
        {
            string mapArg = info.GetArg(3);
            string[] args = string.IsNullOrEmpty(mapArg) ? _emptyRtvArgs : [".rtv", mapArg];
            ExecuteRtvLogic(player, args);
        }
        else if (actionSpan.Equals("shuffle", StringComparison.OrdinalIgnoreCase)) 
        {
            ExecuteShuffleVoteLogic(player);
        }
        else if (actionSpan.Equals("unshuffle", StringComparison.OrdinalIgnoreCase)) 
        {
            ExecuteUnshuffleVoteLogic(player);
        }
    }

    // 【核心修改】：從根源上摧毀 css_rtv 生成的 !rtv 捷徑
    [ConsoleCommand("css_slayer_rtv", "發起 RTV 投票更換地圖 (內部指令)")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player is not { IsValid: true }) return;
        string[] args = new string[info.ArgCount];
        for (int i = 0; i < info.ArgCount; i++) args[i] = info.GetArg(i);
        ExecuteRtvLogic(player, args);
    }

    // 【核心修改】：從根源上摧毀 css_vshuffle 生成的 !vshuffle 捷徑
    [ConsoleCommand("css_slayer_vshuffle", "發起隨機分隊投票 (內部指令)")]
    public void OnCommandShuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player is not { IsValid: true }) return;
        ExecuteShuffleVoteLogic(player);
    }

    // 【核心修改】：從根源上摧毀 css_vunshuffle 生成的 !vunshuffle 捷徑
    [ConsoleCommand("css_slayer_vunshuffle", "發起取消隨機分隊投票 (內部指令)")]
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
            player.PrintToChat($" {Prefix} 可 用 地 圖： {ChatColors.Yellow}{_allowedMapsText}{ChatColors.White}");
            return;
        }

        if (!PassCommonVoteChecks(player, VoteType.MapChange)) return;

        string inputMap = args[1].Trim();
        
        if (!_allowedMaps.TryGetValue(inputMap, out string? matchedMap))
        {
            player.PrintToChat($" {Prefix} 伺 服 器 不 支 援 地 圖 [{args[1]}] ！");
            player.PrintToChat($" {Prefix} 可 用 地 圖： {ChatColors.Yellow}{_allowedMapsText}");
            return;
        }

        _targetMap = matchedMap;
        _currentVoteType = VoteType.MapChange; 
        voteHandler.Init(); 

        voteHandler.SendYesNoVoteToAll(
            Config.VoteDurationSeconds,
            player.Slot,
            "#SFUI_vote_changelevel",
            _targetMap,
            _cachedVoteResultCallback ??= VoteResultCallback,
            _cachedVoteHandlerCallback ??= VoteHandlerCallback
        );

        _lastMapVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White}");
    }

    private void ExecuteShuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player, VoteType.Shuffle)) return;

        _currentVoteType = VoteType.Shuffle; 
        voteHandler.Init(); 
        
        voteHandler.SendYesNoVoteToAll(
            Config.VoteDurationSeconds,
            player.Slot,
            "#SFUI_vote_scramble_teams",
            "",
            _cachedVoteResultCallback ??= VoteResultCallback,
            _cachedVoteHandlerCallback ??= VoteHandlerCallback
        );

        _lastShuffleVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.Lime}隨 機 分 隊{ChatColors.White} 投 票");
    }

    private void ExecuteUnshuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player, VoteType.Unshuffle)) return;

        _currentVoteType = VoteType.Unshuffle; 
        voteHandler.Init(); 
        
        voteHandler.SendYesNoVoteToAll(
            Config.VoteDurationSeconds,
            player.Slot,
            "#SFUI_Scoreboard_Undo",
            "",
            _cachedVoteResultCallback ??= VoteResultCallback,
            _cachedVoteHandlerCallback ??= VoteHandlerCallback
        );

        _lastShuffleVoteTime = Server.CurrentTime; 
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.LightRed}取 消 隨 機 分 隊{ChatColors.White} 投 票");
    }

    private bool PassCommonVoteChecks(CCSPlayerController player, VoteType requestedType)
    {
        if (player.TeamNum != (byte)CsTeam.Terrorist && player.TeamNum != (byte)CsTeam.CounterTerrorist)
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

        if (GetGameRules() is not { WarmupPeriod: true })
        {
            player.PrintToChat($" {Prefix} 投 票 系 統 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用");
            player.PrintToCenter("比 賽 進 行 中 ， 禁 止 發 起 投 票");
            return false;
        }

        int activePlayerCount = GetActivePlayerCount();
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
        int activePlayerCount = GetActivePlayerCount();
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
                
                string centerMsg = $"投 票 通 過：5 秒 後 更 換 地 圖 {_targetMap}";
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p is { IsValid: true, IsBot: false })
                    {
                        p.PrintToCenter(centerMsg);
                    }
                }
                
                string mapCmd = _targetMap;
                AddTimer(7.0f, () => { 
                    if (GetGameRules() is { WarmupPeriod: true }) {
                        Server.ExecuteCommand($"changelevel {mapCmd}"); 
                    } else {
                        Server.PrintToChatAll($" {Prefix} {ChatColors.Yellow}換 圖 終 止！{ChatColors.White}比 賽 已 經 開 始。");
                    }
                });
            }
            else if (_currentVoteType == VoteType.Shuffle)
            {
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.Lime}已 開 啟 隨 機 隊 伍 分 配 {ChatColors.Default}」 將 自 動 洗 牌");
                
                foreach (var p in Utilities.GetPlayers())
                {
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
                Server.PrintToChatAll($" {Prefix} {voteName} 投 票 失 敗！需 達 {ChatColors.Green}{_cachedRequiredPercentText}{ChatColors.Default} 玩 家 同 意");
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

    #region Helpers 
    private CCSGameRules? GetGameRules()
    {
        // 【0-GC 安全快取】：透過 CCSGameRulesProxy.IsValid 確保底層指標絕對安全有效
        if (_cachedGameRulesProxy is { IsValid: true, GameRules: not null })
        {
            return _cachedGameRulesProxy.GameRules;
        }

        foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
        {
            if (entity is { IsValid: true, GameRules: not null } proxy)
            {
                _cachedGameRulesProxy = proxy;
                return proxy.GameRules;
            }
        }
        return null;
    }

    private static int GetActivePlayerCount()
    {
        int count = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is { IsValid: true, IsBot: false, TeamNum: 2 or 3 })
            {
                count++;
            }
        }
        return count;
    }

    private string ReplaceColorTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        return input
            .Replace("{Prefix}", Prefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{White}", _colorWhite, StringComparison.OrdinalIgnoreCase)
            .Replace("{Green}", _colorGreen, StringComparison.OrdinalIgnoreCase)
            .Replace("{Lime}", _colorLime, StringComparison.OrdinalIgnoreCase)
            .Replace("{Red}", _colorRed, StringComparison.OrdinalIgnoreCase)
            .Replace("{LightRed}", _colorLightRed, StringComparison.OrdinalIgnoreCase)
            .Replace("{DarkRed}", _colorDarkRed, StringComparison.OrdinalIgnoreCase)
            .Replace("{Orange}", _colorOrange, StringComparison.OrdinalIgnoreCase)
            .Replace("{Yellow}", _colorYellow, StringComparison.OrdinalIgnoreCase)
            .Replace("{Blue}", _colorBlue, StringComparison.OrdinalIgnoreCase)
            .Replace("{Silver}", _colorSilver, StringComparison.OrdinalIgnoreCase);
    }
    #endregion
}
