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
    public override string ModuleVersion => "1.0";
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Panorama Votes";
    public CPanoramaVote voteHandler; // Global variable to hold the vote handler
    string Prefix = $" {ChatColors.Gold}[{ChatColors.DarkRed}★ {ChatColors.Green}SLAYER_PanoramaVote {ChatColors.DarkRed}★{ChatColors.Gold}]";
    
    // 🎯 宣告一個變數，用來記錄當前投票要換哪張地圖
    private string _targetMap = string.Empty;

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this); // Initialize the vote handler
        RegisterEventHandler<EventVoteCast>((@event, info) =>
		{
            voteHandler.VoteCast(@event); // Call the vote cast function that vote has been casted by player

			return HookResult.Continue;
		});
    }

    // 🎯 改為 css_vote 指令，玩家可在聊天框輸入 !vote <地圖名稱>
    [ConsoleCommand("css_vote", "發起投票更換地圖")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid)
            return;

        // 1. 檢查參數是否足夠（有沒有帶地圖名稱）
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}使用方法: !vote <地圖名稱> (例如: !vote de_mirage)");
            return;
        }

        // 2. 檢查當前是否已經有投票在進行，防止洗畫面
        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當前已有投票正在進行中，請稍後再試。");
            return;
        }

        // 3. 取得玩家輸入的地圖並暫存起來
        _targetMap = info.GetArg(1).Trim();

        voteHandler.Init(); // Initialize the vote handler

        // 4. 發起全服投票：這裡傳入 player.Slot，畫面上就會顯示是誰開啟投票的
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
            
            // 🎯 投票通過，讓伺服器在下一幀立刻執行換圖指令
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
