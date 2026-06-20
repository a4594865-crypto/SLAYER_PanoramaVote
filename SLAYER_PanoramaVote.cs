using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; 
using System;
using System.Linq;

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618
public partial class SLAYER_PanoramaVote : BasePlugin
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "2.1_UltimateVote_NET10"; 
    public override string ModuleAuthor => "SLAYER / Optimized / UltimateVote";
    public override string ModuleDescription => "Panorama RTV, Shuffle & Unshuffle Vote";
    public CPanoramaVote voteHandler; 
    string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    // 💡 升級：加入 Unshuffle (取消洗牌) 狀態
    private enum VoteType { None, MapChange, Shuffle, Unshuffle }
    private VoteType _currentVoteType = VoteType.None;

    private string _targetMap = string.Empty;
    private double _lastVoteTime = 0.0; 
    private const double CooldownTime = 120.0; 
    private const int MinPlayersRequired = 6; 

    private bool _isMapChanging = false;

    private readonly string[] _allowedMaps = { 
        "de_mirage", 
        "de_inferno", 
        "de_dust2", 
        "de_nuke", 
        "de_anubis", 
        "de_ancient", 
        "de_overpass" 
    };

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this); 
        _isMapChanging = false;
        _currentVoteType = VoteType.None;
        
        AddCommandListener("say", OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);

        // 終極修補：當新地圖載入時，強制清空上一張圖留下來的防護鎖與冷卻時間！
        RegisterListener<Listeners.OnMapStart>(mapName => 
        {
            _isMapChanging = false;
            _currentVoteType = VoteType.None;
            _lastVoteTime = 0.0; // 冷卻時間歸零
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
        
        if (text.StartsWith(".rtv"))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ExecuteRtvLogic(player, parts);
            return HookResult.Handled; 
        }
        
        if (text.StartsWith(".vote"))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            //  升級版選單：加入取消洗牌說明
            if (parts.Length == 1)
            {
                player.PrintToChat($" {Prefix} 投 票 系 統 說 明：");
                player.PrintToChat($" {Prefix} 發 起 換 圖：請 輸 入 {ChatColors.Yellow}.rtv <地圖名稱>{ChatColors.White}");
                player.PrintToChat($" {Prefix} 發 起 洗 牌：請 輸 入 {ChatColors.Yellow}.vote shuffle{ChatColors.White}");
                player.PrintToChat($" {Prefix} 取 消 洗 牌：請 輸 入 {ChatColors.Yellow}.vote unshuffle{ChatColors.White}");
                return HookResult.Handled;
            }

            if (parts.Length >= 2 && parts[1] == "shuffle")
            {
                ExecuteShuffleVoteLogic(player);
                return HookResult.Handled;
            }

            // 新增：偵測取消洗牌指令
            if (parts.Length >= 2 && parts[1] == "unshuffle")
            {
                ExecuteUnshuffleVoteLogic(player);
                return HookResult.Handled;
            }
            
            player.PrintToChat($" {Prefix} 無 效 的 投 票 指 令！請 單 獨 輸 入 {ChatColors.Yellow}.vote{ChatColors.White} 查 看 說 明。");
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

    //新增後台指令：發起取消洗牌投票
    [ConsoleCommand("css_vunshuffle", "發起取消隨機分隊投票")]
    public void OnCommandUnshuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid) return;
        ExecuteUnshuffleVoteLogic(player);
    }

    // ==========================================
    // 邏輯一：換地圖投票 (RTV)
    // ==========================================
    private void ExecuteRtvLogic(CCSPlayerController player, string[] args)
    {
        if (!PassCommonVoteChecks(player)) return;

        if (args.Length < 2)
        {
            player.PrintToChat($" {Prefix} 使 用 方 法： {ChatColors.Yellow}.rtv de_mirage{ChatColors.White}");
            player.PrintToChat($" {Prefix} 可 用 地 圖 池： {ChatColors.LightPurple}{string.Join(", ", _allowedMaps)}{ChatColors.White}");
            return;
        }

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

        voteHandler.SendYesNoVoteToAll(30.0f, player.Slot, "#SFUI_vote_changelevel", _targetMap, VoteResultCallback, VoteHandlerCallback);

        _lastVoteTime = Server.CurrentTime;
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White}");
    }

    // ==========================================
    // 邏輯二：隨機洗牌投票 (VShuffle)
    // ==========================================
    private void ExecuteShuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player)) return;

        _currentVoteType = VoteType.Shuffle; 
        voteHandler.Init(); 

        voteHandler.SendYesNoVoteToAll(30.0f, player.Slot, "是否同意開啟【隨機分隊】？", "", VoteResultCallback, VoteHandlerCallback);

        _lastVoteTime = Server.CurrentTime;
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.Lime}隨 機 分 隊{ChatColors.White} 投 票！");
    }

    // ==========================================
    // 邏輯三：取消隨機洗牌投票 (VUnshuffle)
    // ==========================================
    private void ExecuteUnshuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player)) return;

        _currentVoteType = VoteType.Unshuffle; 
        voteHandler.Init(); 

        voteHandler.SendYesNoVoteToAll(30.0f, player.Slot, "是否同意【取消隨機分隊】？", "", VoteResultCallback, VoteHandlerCallback);

        _lastVoteTime = Server.CurrentTime;
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.LightRed}取 消 隨 機 分 隊{ChatColors.White} 投 票！");
    }

    // ==========================================
    // 共用防護檢查 
    // ==========================================
    private bool PassCommonVoteChecks(CCSPlayerController player)
    {
        if (player.TeamNum != (byte)CsTeam.Terrorist && player.TeamNum != (byte)CsTeam.CounterTerrorist)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}只 有 在 CT 或 T 的 玩 家 才 能 發 起 投 票！");
            return false;
        }

        if (_isMapChanging)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，請 稍 候。");
            return false;
        }

        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試。");
            return false;
        }

        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (gameRules == null || gameRules.GameRules == null || !gameRules.GameRules.WarmupPeriod)
        {
            player.PrintToChat($" {Prefix} 投 票 系 統 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用。");
            return false;
        }

        int activePlayerCount = Utilities.GetPlayers().Count(p => p != null && p.IsValid && !p.IsBot && (p.TeamNum == 2 || p.TeamNum == 3));

        if (activePlayerCount < MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Green}{MinPlayersRequired} 人 {ChatColors.White}以 上 才 能 發 起 投 票");
            return false;
        }

        double currentTime = Server.CurrentTime;
        if (currentTime - _lastVoteTime < CooldownTime)
        {
            int timeLeft = (int)Math.Ceiling(CooldownTime - (currentTime - _lastVoteTime));
            player.PrintToChat($" {Prefix} 投 票 冷 卻 中，請 等 待 {ChatColors.Green}{timeLeft} {ChatColors.White}秒 後 再 試。");
            return false;
        }

        return true;
    }

    // ==========================================
    // 投票結果處理中樞 
    // ==========================================
    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        int activePlayerCount = Utilities.GetPlayers().Count(p => p != null && p.IsValid && !p.IsBot && (p.TeamNum == 2 || p.TeamNum == 3));
        bool isVotePassed = false;

        if (_currentVoteType == VoteType.MapChange)
        {
            isVotePassed = info.yes_votes > info.no_votes;
        }
        else if (_currentVoteType == VoteType.Shuffle || _currentVoteType == VoteType.Unshuffle)
        {
            // 升級：洗牌與「取消洗牌」都必須嚴格遵守 8 成門檻！
            int requiredVotes = (int)Math.Ceiling(activePlayerCount * 0.8);
            isVotePassed = info.yes_votes >= requiredVotes;
        }

        if (isVotePassed) 
        {
            if (_currentVoteType == VoteType.MapChange)
            {
                _isMapChanging = true; 
                Server.PrintToChatAll($" {Prefix} 投 票 通 過！ {ChatColors.Green}5 秒 {ChatColors.White}後 更 換 地 圖 至 {ChatColors.Green}{_targetMap}");
                string mapCmd = _targetMap;
                AddTimer(5.0f, () => { Server.ExecuteCommand($"changelevel {mapCmd}"); });
            }
            else if (_currentVoteType == VoteType.Shuffle)
            {
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.Lime}已 開 啟 隨 機 隊 伍 分 配 {ChatColors.Default}」 將 自 動 洗 牌");
                Server.ExecuteCommand("css_shuffle");
            }
            else if (_currentVoteType == VoteType.Unshuffle)
            {
                //  升級：通過後自動呼叫 MatchZy 的取消指令
                Server.PrintToChatAll($" {Prefix} 投 票 通 過「 {ChatColors.LightRed}已 取 消 隨 機 隊 伍 分 配 {ChatColors.Default}」 維 持 隊 伍 不 變");
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
                int requiredVotes = (int)Math.Ceiling(activePlayerCount * 0.8);
                Server.PrintToChatAll($" {Prefix} {voteName} 投 票 失 敗！需 達 8 成 同 意");
            }
            else
            {
                Server.PrintToChatAll($" {Prefix} 投 票 失 敗，維 持 現 狀。");
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
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 已 被 系 統 取 消。");
                break;
        }
    }
}
