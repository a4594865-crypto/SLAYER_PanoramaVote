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
    public override string ModuleVersion => "2.0_DualVote_NET10"; 
    public override string ModuleAuthor => "SLAYER / Optimized / DualVote";
    public override string ModuleDescription => "Panorama RTV & Shuffle Vote with MatchZy Integration";
    public CPanoramaVote voteHandler; 
    string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    // 💡 新增：用來讓系統分辨現在是哪種投票
    private enum VoteType { None, MapChange, Shuffle }
    private VoteType _currentVoteType = VoteType.None;

    private string _targetMap = string.Empty;
    private double _lastVoteTime = 0.0; 
    private const double CooldownTime = 120.0; 
    private const int MinPlayersRequired = 6; 

    // 防止投票通過後的空窗期被惡意發起新投票
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
        
        // 改用旁聽模式 (AddCommandListener)，不再霸佔原生聊天系統
        AddCommandListener("say", OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);

        RegisterEventHandler<EventVoteCast>((@event, info) =>
        {
            voteHandler.VoteCast(@event); 
            return HookResult.Continue;
        });
    }

    // 統一處理 say 與 say_team 的邏輯
    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        
        string text = info.GetArg(1).Trim('"').Trim().ToLower();
        
        // 1. 偵測到 .rtv 指令 (換圖)
        if (text.StartsWith(".rtv"))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ExecuteRtvLogic(player, parts);
            return HookResult.Handled; 
        }
        
        // 2. 💡 偵測到 .vshuffle 或 .vs 指令 (隨機洗牌)
        if (text.StartsWith(".vshuffle") || text.StartsWith(".vs"))
        {
            ExecuteShuffleVoteLogic(player);
            return HookResult.Handled;
        }

        // 如果是一般聊天，放行給系統處理
        return HookResult.Continue;
    }

    [ConsoleCommand("css_rtv", "發起 RTV 投票更換地圖")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid) return;
        string[] args = new string[info.ArgCount];
        for (int i = 0; i < info.ArgCount; i++)
        {
            args[i] = info.GetArg(i);
        }
        ExecuteRtvLogic(player, args);
    }

    // 💡 新增：隨機洗牌的 Console 指令
    [ConsoleCommand("css_vshuffle", "發起隨機分隊投票")]
    public void OnCommandShuffleVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid) return;
        ExecuteShuffleVoteLogic(player);
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
        _currentVoteType = VoteType.MapChange; // 標記為換圖投票
        voteHandler.Init(); 

        voteHandler.SendYesNoVoteToAll(
            30.0f, 
            player.Slot, 
            "#SFUI_vote_changelevel", 
            _targetMap, 
            VoteResultCallback, 
            VoteHandlerCallback
        );

        _lastVoteTime = Server.CurrentTime;
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White}");
    }

    // ==========================================
    // 邏輯二：隨機洗牌投票 (VShuffle)
    // ==========================================
    private void ExecuteShuffleVoteLogic(CCSPlayerController player)
    {
        if (!PassCommonVoteChecks(player)) return;

        _currentVoteType = VoteType.Shuffle; // 標記為洗牌投票
        voteHandler.Init(); 

        // 💡 核心魔改：直接塞入純文字作為 UI 標題！
        voteHandler.SendYesNoVoteToAll(
            30.0f, 
            player.Slot, 
            "是否同意開啟【隨機分隊】？", // 👈 自訂標題
            "",                           // 參數留空
            VoteResultCallback, 
            VoteHandlerCallback
        );

        _lastVoteTime = Server.CurrentTime;
        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 {ChatColors.Lime}隨 機 分 隊{ChatColors.White} 投 票！");
    }

    // ==========================================
    // 共用防護檢查 (提取出來讓代碼更簡潔)
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

        int activePlayerCount = Utilities.GetPlayers().Count(p => 
            p != null && 
            p.IsValid && 
            !p.IsBot && 
            (p.TeamNum == 2 || p.TeamNum == 3)
        );

        if (activePlayerCount < MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Green}{MinPlayersRequired} 人 {ChatColors.White}以 上 才 能 發 起 投 票 (當前: {activePlayerCount}人)。");
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
    // 投票結果處理中樞 (加入 8 成門檻分流)
    // ==========================================
    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        // 取得當前場上的「總活人數」(例如滿房就是 10 人)
        int activePlayerCount = Utilities.GetPlayers().Count(p => 
            p != null && 
            p.IsValid && 
            !p.IsBot && 
            (p.TeamNum == 2 || p.TeamNum == 3)
        );

        bool isVotePassed = false;

        // 門檻分流：換圖看半數 vs 洗牌看 8 成
        if (_currentVoteType == VoteType.MapChange)
        {
            // 原本的換圖邏輯：贊成票大於反對票 (過半)
            isVotePassed = info.yes_votes > info.no_votes;
        }
        else if (_currentVoteType == VoteType.Shuffle)
        {
            // 新增的洗牌邏輯：贊成票必須大於等於「場上總人數的 80%」
            // 假設場上 10 人，10 * 0.8 = 8，必須拿到 8 票！
            int requiredVotes = (int)Math.Ceiling(activePlayerCount * 0.8);
            isVotePassed = info.yes_votes >= requiredVotes;
        }

        // --- 結算執行 ---
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
                Server.PrintToChatAll($" {Prefix} 投 票 通 過！{ChatColors.Lime}系 統 將 自 動 開 啟 隨 機 分 隊。");
                Server.ExecuteCommand("css_shuffle");
            }

            _currentVoteType = VoteType.None; // 重置狀態
            return true;
        }
        else
        {
            if (_currentVoteType == VoteType.Shuffle)
            {
                int requiredVotes = (int)Math.Ceiling(activePlayerCount * 0.8);
                Server.PrintToChatAll($" {Prefix} 投 票 失 敗！需 達 8 成 同 意");
            }
            else
            {
                Server.PrintToChatAll($" {Prefix} 投 票 失 敗，維 持 現 狀。");
            }
            
            _currentVoteType = VoteType.None; // 重置狀態
            return false;
        }
    }

    private void VoteHandlerCallback(YesNoVoteAction action, int param1, int param2)
    {
        switch (action)
        {
            case YesNoVoteAction.VoteAction_Start:
            {
                Server.PrintToChatAll($" {Prefix} 投 票 開 始！請 在 左 上 角 選 擇{ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ]");
                break;
            }
            case YesNoVoteAction.VoteAction_Vote:
                break;
            case YesNoVoteAction.VoteAction_End:
            {
                if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_Cancelled)
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 已 被 系 統 取 消。");
                }
                break;
            }
        }
    }
}
