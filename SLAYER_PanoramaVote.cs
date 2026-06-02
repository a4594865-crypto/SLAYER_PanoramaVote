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
    public override string ModuleVersion => "1.6"; // 升級版本號
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Panorama RTV with Player Count, Warmup, Cooldown and Dot-command support";
    public CPanoramaVote voteHandler; // Global variable to hold the vote handler
    string Prefix = $" {ChatColors.Gold}[{ChatColors.DarkRed}★ {ChatColors.Green}SLAYER_RTV {ChatColors.DarkRed}★{ChatColors.Gold}]";
    
    private string _targetMap = string.Empty;
    
    private double _lastVoteTime = 0.0; 
    private const double CooldownTime = 90.0; // 冷卻時間 90 秒
    private const int MinPlayersRequired = 4; // 最低人數限制為 4 人

    //建立允許的地圖清單
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

    // 【修改】同時綁定 css_rtv (!rtv) 與 .rtv 兩個指令入口
    [ConsoleCommand("css_rtv", "發起 RTV 投票更換地圖")]
    [ConsoleCommand(".rtv", "發起 RTV 投票更換地圖")]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null || !player.IsValid)
            return;

        // 0. 檢查當前是否為暖場時間
        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (gameRules == null || gameRules.GameRules == null || !gameRules.GameRules.WarmupPeriod)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: RTV 換圖投票只能在【暖場時間】內使用！");
            return;
        }

        // 0.3. 檢查現場人數是否足夠 (不含觀察者、不含 BOT)
        int activePlayerCount = Utilities.GetPlayers().Count(p => 
            p != null && 
            p.IsValid && 
            !p.IsBot && 
            p.Connected == PlayerConnectedState.PlayerConnected && 
            (p.TeamNum == (byte)CsTeam.Terrorist || p.TeamNum == (byte)CsTeam.CounterTerrorist)
        );

        if (activePlayerCount < MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: 現場遊玩人數不足！需要 {ChatColors.Yellow}{MinPlayersRequired}{ChatColors.Red} 人以上才能發起 RTV (目前: {ChatColors.Yellow}{activePlayerCount}{ChatColors.Red} 人，不含觀察者)。");
            return;
        }

        // 0.5. 檢查冷卻時間是否已到
        double currentTime = Server.CurrentTime;
        if (currentTime - _lastVoteTime < CooldownTime)
        {
            int timeLeft = (int)Math.Ceiling(CooldownTime - (currentTime - _lastVoteTime));
            player.PrintToChat($" {Prefix} {ChatColors.Red}錯誤: RTV 冷卻中！請等待 {ChatColors.Yellow}{timeLeft}{ChatColors.Red} 秒後再試。");
            return;
        }

        // 1. 檢查參數是否足夠
        if (info.ArgCount < 2)
        {
            // 根據玩家輸入的指令前綴，智慧顯示提示（如果是點號開頭就顯示 .rtv）
            string usedCmd = info.GetArg(0).StartsWith(".") ? ".rtv" : "!rtv";
            player.PrintToChat($" {Prefix} {ChatColors.Red}使用方法: {usedCmd} <地圖名稱> (例如: {usedCmd} de_mirage)");
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

        // 檢查輸入的地圖有沒有在允許名單內
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

        // 成功發起投票，刷新最後投票時間
        _lastVoteTime = Server.CurrentTime;

        Server.PrintToChatAll($" {Prefix} 玩家 {ChatColors.Lime}{player.PlayerName}{ChatColors.White} 發起了 RTV 換圖至 {ChatColors.Green}{_targetMap}{ChatColors.White} 的投票！");
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
