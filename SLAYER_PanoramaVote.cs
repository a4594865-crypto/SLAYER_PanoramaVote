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
    public override string ModuleVersion => "1.7_Optimized_NET10"; 
    public override string ModuleAuthor => "SLAYER / Optimized";
    public override string ModuleDescription => "Panorama RTV with Player Count, Warmup, Cooldown and Dot-command support";
    public CPanoramaVote voteHandler; 
    string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private string _targetMap = string.Empty;
    private double _lastVoteTime = 0.0; 
    private const double CooldownTime = 120.0; 
    private const int MinPlayersRequired = 6; 

    // 防止投票通過後的 5 秒空窗期被惡意發起新投票
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
        
        string text = info.GetArg(1).Trim('"').Trim();
        
        // 偵測到 .rtv 指令
        if (text.StartsWith(".rtv", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ExecuteRtvLogic(player, parts);
            
            // 回傳 Handled，直接沒收這句話，讓聊天室保持乾淨！
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

    private void ExecuteRtvLogic(CCSPlayerController player, string[] args)
    {
        if (player.TeamNum != (byte)CsTeam.Terrorist && player.TeamNum != (byte)CsTeam.CounterTerrorist)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}只 有 在 CT 或 T 的 玩 家 才 能 發 起 投 票！");
            return;
        }

        // 檢查是否已經準備換圖，徹底封死 5 秒空窗期
        if (_isMapChanging)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，請 稍 候。");
            return;
        }

        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試。");
            return;
        }

        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (gameRules == null || gameRules.GameRules == null || !gameRules.GameRules.WarmupPeriod)
        {
            player.PrintToChat($" {Prefix} 投 票 換 圖 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用。");
            return;
        }

        // .NET 10 黃金標準寫法，精準抓出場上活人，不會被幽靈連線干擾
        int activePlayerCount = Utilities.GetPlayers().Count(p => 
            p != null && 
            p.IsValid && 
            !p.IsBot && 
            (p.TeamNum == 2 || p.TeamNum == 3)
        );

        if (activePlayerCount < MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Green}{MinPlayersRequired} 人 {ChatColors.White}以 上 才 能 發 起 投 票 (當前: {activePlayerCount}人)。");
            return;
        }

        double currentTime = Server.CurrentTime;
        if (currentTime - _lastVoteTime < CooldownTime)
        {
            int timeLeft = (int)Math.Ceiling(CooldownTime - (currentTime - _lastVoteTime));
            player.PrintToChat($" {Prefix} 投 票 冷 卻 中，請 等 待 {ChatColors.Green}{timeLeft} {ChatColors.White}秒 後 再 試。");
            return;
        }

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

    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        if(info.yes_votes > info.no_votes) 
        {
            _isMapChanging = true; //投票一通過，立刻把系統上鎖
            
            // 把文字廣播和計時器統一設定為 5 秒
            Server.PrintToChatAll($" {Prefix} 投 票 通 過！ {ChatColors.Green}5 秒 {ChatColors.White}後 更 換 地 圖 至 {ChatColors.Green}{_targetMap}");
            string mapCmd = _targetMap;
            
            AddTimer(5.0f, () =>
            {
                Server.ExecuteCommand($"changelevel {mapCmd}");
            });

            return true;
        }
        else
        {
            Server.PrintToChatAll($" {Prefix} 投 票 失 敗，維 持 當 前 地 圖。");
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
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 已 被 系 統 或 管 理 員 取 消。");
                }
                break;
            }
        }
    }
}
