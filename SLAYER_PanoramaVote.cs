using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; // Add the PanoramaVote namespace

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618
public partial class SLAYER_PanoramaVote : BasePlugin //, IPluginConfig<SLAYER_VotesConfig>
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "1.7"; // 升級版本號
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Panorama Votes - Warmup Only Edition";
    public CPanoramaVote voteHandler; // Global variable to hold the vote handler
    string Prefix = $" {ChatColors.Gold}[{ChatColors.DarkRed}★ {ChatColors.Green}投票通知 {ChatColors.DarkRed}★{ChatColors.Gold}]";
    
    private string _targetMap = string.Empty;

    // 投票冷卻時間 90 秒
    private readonly double _cooldownDuration = 90.0;
    // 用來記錄上一次投票結束的時間點
    private DateTime _lastVoteEndTime = DateTime.MinValue;

    // 允許的地圖清單
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
        voteHandler = new CPanoramaVote(this); // Initialize the vote handler
        RegisterEventHandler<EventVoteCast>((@event, info) =>
		{
            voteHandler.VoteCast(@event); // Call the vote cast function that vote has been casted by player

			return HookResult.Continue;
		});
    }

    [ConsoleCommand("css_vote", "發起投票更換地圖")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid)
            return;

        // 🎯 1. 限制暖場檢查：只有在暖場階段才能使用此指令
        if (GameRules().WarmupPeriod == false)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}目 前 不 是 暖 場 時 間，無 法 發 起 換 圖 投 票");
            return;
        }

        // 🎯 2. 人數限制檢查：如果伺服器人數低於 4 人，直接攔截
        int currentPlayerCount = Utilities.GetPlayers().Count;
        if (currentPlayerCount < 4)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 伺服器內人數不足");
            return;
        }

        // 3. 檢查參數是否足夠
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}使 用 方 法: .vote <地圖名稱> (例如: .vote de_mirage)");
            return;
        }

        // 4. 檢查當前是否已經有投票在進行
        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試。");
            return;
        }

        // 5. 檢查投票冷卻時間（90秒限制）
        TimeSpan timeSinceLastVote = DateTime.Now - _lastVoteEndTime;
        if (timeSinceLastVote.TotalSeconds < _cooldownDuration)
        {
            int remainingSeconds = (int)(_cooldownDuration - timeSinceLastVote.TotalSeconds);
            player.PrintToChat($" {Prefix} {ChatColors.Red}投 票 冷 卻 中 請 等 待 {ChatColors.Yellow}{remainingSeconds}{ChatColors.Red} 秒 後 發 起 投 票。");
            return;
        }

        // 6. 取得玩家輸入的地圖，並強制轉小寫辨認
        string inputMap = info.GetArg(1).Trim().ToLower();

        // 7. 地圖清單驗證
        if (!_allowedMaps.Contains(inputMap))
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 伺 服 器 不 支 援 地 圖 [{inputMap}] ");
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}可 用 地 圖: {string.Join(", ", _allowedMaps)}");
            return;
        }

        // 8. 驗證通過，儲存地圖名稱
        _targetMap = inputMap;

        voteHandler.Init(); // Initialize the vote handler

        // 9. 發起全服投票
        voteHandler.SendYesNoVoteToAll(
            30.0f,
            player.Slot,
            "#SFUI_vote_changelevel",
            _targetMap,
            VoteResultCallback,
            VoteHandlerCallback
        );
    }

    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        if(info.yes_votes > info.no_votes) // Check if the vote passed
        {
            string mapCmd = _targetMap;
            Server.NextFrame(() =>
            {
                Server.ExecuteCommand($"changelevel {mapCmd}");
            });

            return true;
        }
        else
        {
            return false;
        }
    }

    private void VoteHandlerCallback(YesNoVoteAction action, int param1, int param2)
    {
        switch (action)
        {
            case YesNoVoteAction.VoteAction_Start: // On Vote Start
            {
                Server.PrintToChatAll($" {Prefix} {ChatColors.Green}投票開始！請在左上角選擇 [F1 是] 或 [F2 否]");
                break;
            }
            case YesNoVoteAction.VoteAction_Vote:
            {
                break;
            }
            case YesNoVoteAction.VoteAction_End: 
            {
                // 只要投票結束，不論任何原因都重置冷卻時間
                _lastVoteEndTime = DateTime.Now;
                break;
            }
        }
    }
}
