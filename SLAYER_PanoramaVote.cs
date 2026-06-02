using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin; // 🎯 確保引入管理員模組
using PanoramaVote; 

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618
public partial class SLAYER_PanoramaVote : BasePlugin 
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "1.13"; // 升級版本號
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Panorama Votes - Perms Fixed Edition";
    public CPanoramaVote voteHandler; 
    string Prefix = $" {ChatColors.Gold}[{ChatColors.DarkRed}★ {ChatColors.Green}SLAYER_PanoramaVote {ChatColors.DarkRed}★{ChatColors.Gold}]";
    
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
        "de_vertigo" 
    };

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this); 
        RegisterEventHandler<EventVoteCast>((@event, info) =>
		{
            voteHandler.VoteCast(@event); 
			return HookResult.Continue;
		});
    }

    // 🎯 關鍵修正：明確宣告此指令「不需要任何特殊權限」，任何一般玩家（All）都可以自由使用！
    [ConsoleCommand("css_vote", "發起投票更換地圖")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid)
            return;

        // 1. 限暖場時間判定：對齊 1v1 插件的完美 .NET 10 標準
        var gameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (gameRulesProxy?.GameRules?.WarmupPeriod ?? true == false)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 目前不是暖場時間，無法發起換圖投票！");
            return;
        }

        // 2. 人數限制檢查：如果伺服器人數低於 4 人，直接攔截
        int currentPlayerCount = Utilities.GetPlayers().Count;
        if (currentPlayerCount < 4)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 伺服器內人數不足 {ChatColors.Yellow}4{ChatColors.Red} 人，無法發起換圖投票！(當前人數: {currentPlayerCount})");
            return;
        }

        // 3. 檢查參數是否足夠
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}使用方法: .vote <地圖名稱> (例如: .vote de_mirage)");
            return;
        }

        // 4. 檢查當前是否已經有投票在進行
        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當前已有投票正在進行中，請稍後再試。");
            return;
        }

        // 5. 檢查投票冷卻時間（90秒限制）
        TimeSpan timeSinceLastVote = DateTime.Now - _lastVoteEndTime;
        if (timeSinceLastVote.TotalSeconds < _cooldownDuration)
        {
            int remainingSeconds = (int)(_cooldownDuration - timeSinceLastVote.TotalSeconds);
            player.PrintToChat($" {Prefix} {ChatColors.Red}投票冷卻中！請等待 {ChatColors.Yellow}{remainingSeconds}{ChatColors.Red} 秒後再發起投票。");
            return;
        }

        // 6. 取得玩家輸入的地圖，並強制轉小寫辨認
        string inputMap = info.GetArg(1).Trim().ToLower();

        // 7. 地圖清單驗證
        if (!_allowedMaps.Contains(inputMap))
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 伺服器不支援地圖 [{inputMap}] ！");
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}可用地圖: {string.Join(", ", _allowedMaps)}");
            return;
        }

        // 8. 驗證通過，儲存地圖名稱
        _targetMap = inputMap;

        voteHandler.Init(); 

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
        if(info.yes_votes > info.no_votes) 
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
            case YesNoVoteAction.VoteAction_Start: 
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
                _lastVoteEndTime = DateTime.Now;
                break;
            }
        }
    }
}
