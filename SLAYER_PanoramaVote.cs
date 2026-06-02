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
    public override string ModuleVersion => "1.1"; // 升級版本號
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Panorama Votes with Map List Validation";
    public CPanoramaVote voteHandler; // Global variable to hold the vote handler
    string Prefix = $" {ChatColors.Gold}[{ChatColors.DarkRed}★ {ChatColors.Green}SLAYER_PanoramaVote {ChatColors.DarkRed}★{ChatColors.Gold}]";
    
    private string _targetMap = string.Empty;

    // 🎯 建立允許的地圖清單（你可以自由增減這裡的地圖名稱，記得都要維持小寫）
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

        // 1. 檢查參數是否足夠
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}使用方法: !vote <地圖名稱> (例如: !vote de_mirage)");
            return;
        }

        // 2. 檢查當前是否已經有投票在進行
        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當前已有投票正在進行中，請稍後再試。");
            return;
        }

        // 3. 取得玩家輸入的地圖，並強制轉小寫辨認
        string inputMap = info.GetArg(1).Trim().ToLower();

        // 🎯 4. 地圖清單驗證：檢查輸入的地圖有沒有在允許名單內
        if (!_allowedMaps.Contains(inputMap))
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 伺服器不支援地圖 [{inputMap}] ！");
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}可用地圖: {string.Join(", ", _allowedMaps)}");
            return; // 辨認失敗，直接中斷，不發起投票
        }

        // 5. 驗證通過，儲存地圖名稱
        _targetMap = inputMap;

        voteHandler.Init(); // Initialize the vote handler

        // 6. 發起全服投票
        voteHandler.SendYesNoVoteToAll(
            30.0f, 
            player.Slot, 
            "#SFUI_vote_changelevel", // 官方內建的「更換地圖」本地化標題
            _targetMap, 
            VoteResultCallback, 
            VoteHandlerCallback
        );

        Server.PrintToChatAll($" {Prefix} 玩家 {ChatColors.Lime}{player.PlayerName}{ChatColors.White} 發起了換圖至 {ChatColors.Green}{_targetMap}{ChatColors.White} 的投票！");
    }

    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        Server.PrintToChatAll($" {Prefix} {ChatColors.Green}投票結果: {ChatColors.Red}反對 = {ChatColors.Green}{info.no_votes} {ChatColors.White}| {ChatColors.Red}贊成 = {ChatColors.Green}{info.yes_votes} {ChatColors.White}| {ChatColors.Red}總投票數 = {ChatColors.Green}{info.num_votes} {ChatColors.White}| {ChatColors.Red}可投票人數 = {ChatColors.Green}{info.num_clients}");

        foreach (var kvp in info.clientInfo) // Print the vote info for each player
        {
            Console.WriteLine($"Player in Key: {kvp.Key}: Player Slot = {kvp.Value.Item1}, Player Vote = {(kvp.Value.Item2 == (int)CastVote.VOTE_OPTION1 ? "Yes" : "No")}");
        }

        if(info.yes_votes > info.no_votes) // Check if the vote passed
        {
            Server.PrintToChatAll($" {Prefix} {ChatColors.Green}投票通過！即將更換地圖至 {ChatColors.Gold}{_targetMap}");
            
            string mapCmd = _targetMap;
            Server.NextFrame(() =>
            {
                Server.ExecuteCommand($"changelevel {mapCmd}");
            });

            return true;
        }
        else
        {
            Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投票失敗，維持當前地圖。");
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
            case YesNoVoteAction.VoteAction_Vote: // On Player Vote: param1 = client slot, param2 = choice (VOTE_OPTION1=yes, VOTE_OPTION2=no)
            {
                CCSPlayerController player = Utilities.GetPlayerFromSlot(param1)!;
                if (player == null || !player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
                    break;
                player.PrintToChat($" {Prefix} {ChatColors.White}感謝您的投票！您投了：{(param2 == (int)CastVote.VOTE_OPTION1 ? $"{ChatColors.Green}是 (Yes)" : $"{ChatColors.Red}否 (No)")}");
                break;
            }
            case YesNoVoteAction.VoteAction_End:
            {
                if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_Cancelled) // Vote Cancelled
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投票已被系統或管理員取消。");
                }
                else if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_AllVotes) // Everyone Voted
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Green}所有人皆已投票完畢，正在結算...");
                }
                else if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_TimeUp) // Time is up
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投票時間結束，正在結算...");
                }

                break;
            }
        }
    }
}
