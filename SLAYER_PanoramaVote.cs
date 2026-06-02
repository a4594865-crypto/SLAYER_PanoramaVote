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
    string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private string _targetMap = string.Empty;
    
    private double _lastVoteTime = 0.0; 
    private const double CooldownTime = 120.0; // 冷卻時間 90 秒
    private const int MinPlayersRequired = 2; // 最低人數限制為 2 人

    // 建立允許的地圖清單
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

    // 同时绑定 css_rtv (!rtv) 与 .rtv 两个指令入口
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
            player.PrintToChat($" {Prefix} 換 圖 投 票 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用");
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
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Yellow}6 人 {ChatColors.White}以 上 才 能 發 起 投 票");
            return;
        }

        // 0.5. 檢查冷卻時間是否已到
        double currentTime = Server.CurrentTime;
        if (currentTime - _lastVoteTime < CooldownTime)
        {
            int timeLeft = (int)Math.Ceiling(CooldownTime - (currentTime - _lastVoteTime));
            player.PrintToChat($" {Prefix} 投 票 冷 卻 中 請 等 待 {ChatColors.Yellow}{timeLeft} {ChatColors.White}秒 後 再 試。");
            return;
        }

        // 1. 檢查參數是否足夠
        if (info.ArgCount < 2)
        {
            // 根據玩家輸入的指令前綴，智慧顯示提示（如果是點號開頭就顯示 .rtv）
            string usedCmd = info.GetArg(0).StartsWith(".") ? ".rtv" : "!rtv";
            player.PrintToChat($" {Prefix} 使 用 方 法: {usedCmd} <地 圖 名 稱> 例 如: {usedCmd} de_mirage");
            return;
        }

        // 2. 檢查當前是否已經有投票在進行
        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試");
            return;
        }

        // 3. 取得玩家輸入的地圖，並強制轉小寫
        string inputMap = info.GetArg(1).Trim().ToLower();

        // 【修改】不論地圖清單是大寫還是小寫，統一轉換為小寫進行比對 (大小寫通吃)
        string? matchedMap = _allowedMaps.FirstOrDefault(m => m.ToLower() == inputMap);

        if (matchedMap == null)
        {
            player.PrintToChat($" {Prefix} 伺 服 器 不 支 援 地 圖 [{info.GetArg(1)}] ！");
            player.PrintToChat($" {Prefix} {ChatColors.Yellow}可用地圖: {string.Join(", ", _allowedMaps)}");
            return; // 辨認失敗，直接中斷
        }

        // 5. 驗證通過，儲存正確的地圖名稱（維持原本清單上的寫法）
        _targetMap = matchedMap;

        voteHandler.Init(); // Initialize the vote handler

        // 6. 發起全服投票
        voteHandler.SendYesNoVoteToAll(
            20.0f, 
            player.Slot, 
            "#SFUI_vote_changelevel", // 官方內建的「更換地圖」本地化標題
            _targetMap, 
            VoteResultCallback, 
            VoteHandlerCallback
        );

        // 成功發起投票，刷新最後投票時間
        _lastVoteTime = Server.CurrentTime;

        Server.PrintToChatAll($" {Prefix} 玩 家 {ChatColors.Lime}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White} 投 票");
    }

    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        foreach (var kvp in info.clientInfo) 
        {
            Console.WriteLine($"Player in Key: {kvp.Key}: Player Slot = {kvp.Value.Item1}, Player Vote = {(kvp.Value.Item2 == (int)CastVote.VOTE_OPTION1 ? "Yes" : "No")}");
        }

        if(info.yes_votes > info.no_votes) // Check if the vote passed
        {
            // 提示改為「3 秒後」
            Server.PrintToChatAll($" {Prefix} {ChatColors.Green}投 票 通 過 {ChatColors.Yellow}3 秒 {ChatColors.White}後更 換 地 圖 至 {ChatColors.Gold}{_targetMap}");
            
            string mapCmd = _targetMap;
            
            // 延遲 3.0 秒後才執行換圖指令
            AddTimer(3.0f, () =>
            {
                Server.ExecuteCommand($"changelevel {mapCmd}");
            });

            return true;
        }
        else
        {
            Server.PrintToChatAll($" {Prefix} 投 票 失 敗，維 持 當 前 地 圖");
            return false;
        }
    }

    private void VoteHandlerCallback(YesNoVoteAction action, int param1, int param2)
    {
        switch (action)
        {
            case YesNoVoteAction.VoteAction_Start: // On Vote Start
            {
                Server.PrintToChatAll($" {Prefix} 投 票 開 始！請 在 左 上 角 選 擇{ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ]");
                break;
            }
            case YesNoVoteAction.VoteAction_Vote: // On Player Vote
            {
                // 【已刪除】不發送個人投票感謝訊息
                break;
            }
            case YesNoVoteAction.VoteAction_End:
            {
                if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_Cancelled) // Vote Cancelled
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 已 被 系 統 或 管 理 員 取 消");
                }
                else if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_AllVotes) // Everyone Voted
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Green}所 有 人 皆 已 投 票 完 畢，正 在 結 算...");
                }
                else if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_TimeUp) // Time is up
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 時 間 結 束，正 在 結 算...");
                }

                break;
            }
        }
    }
}
