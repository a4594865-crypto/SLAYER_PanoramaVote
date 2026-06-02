using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; // Add the PanoramaVote namespace

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618
public partial class SLAYER_PanoramaVote : BasePlugin
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "1.6"; 
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Panorama RTV with Player Count, Warmup, Cooldown and Dot-command support";
    public CPanoramaVote voteHandler; 
    string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private string _targetMap = string.Empty;
    
    private double _lastVoteTime = 0.0; 
    private const double CooldownTime = 120.0; // 冷卻時間 120 秒
    private const int MinPlayersRequired = 6; // 最低人數限制為 6 人

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
        
        RegisterEventHandler<EventVoteCast>((@event, info) =>
		{
            voteHandler.VoteCast(@event); 
			return HookResult.Continue;
		});
    }

    // ConsoleCommand 攔截全服聊天 say 指令
    [ConsoleCommand("say", "攔截公頻聊天")]
    public void OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        // 取得玩家輸入的完整對話字串 (去掉兩側引號與空格)
        string text = info.ArgString.Trim('"').Trim();

        // 如果玩家打的是 .rtv，幫他模擬成執行指令
        if (text.StartsWith(".rtv", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ExecuteRtvLogic(player, parts);
        }
    }

    // ConsoleCommand 攔截團隊聊天 say_team 指令
    [ConsoleCommand("say_team", "攔截團隊聊天")]
    public void OnPlayerSayTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        string text = info.ArgString.Trim('"').Trim();

        if (text.StartsWith(".rtv", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ExecuteRtvLogic(player, parts);
        }
    }

    // 保留後台指令入口以供前置支援
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

    // 將所有換圖檢查與邏輯抽取出來，供共同調用
    private void ExecuteRtvLogic(CCSPlayerController player, string[] args)
    {
        // 檢查當前是否為暖場時間
        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (gameRules == null || gameRules.GameRules == null || !gameRules.GameRules.WarmupPeriod)
        {
            player.PrintToChat($" {Prefix} 投 票 換 圖 只 能 在{ChatColors.Yellow} 暖 場 時 間 {ChatColors.White}內 使 用");
            return;
        }

        // 精確判斷輸入指令的人是不是在 CT 還是在 T (TS)
        if (player.TeamNum != (byte)CsTeam.Terrorist && player.TeamNum != (byte)CsTeam.CounterTerrorist)
        {
            player.PrintToChat($" {Prefix} 觀 察 者 玩 家 無 法 發 起 投 票");
            return;
        }

        // 檢查現場人數是否足夠
        int activePlayerCount = Utilities.GetPlayers().Count(p => 
            p != null && 
            p.IsValid && 
            !p.IsBot && 
            p.Connected == PlayerConnectedState.Connected && 
            (p.TeamNum == (byte)CsTeam.Terrorist || p.TeamNum == (byte)CsTeam.CounterTerrorist)
        );

        if (activePlayerCount < MinPlayersRequired)
        {
            player.PrintToChat($" {Prefix} 人 數 不 足！需 要 {ChatColors.Green}6 人 {ChatColors.White}以 上 才 能 發 起 投 票");
            return;
        }

        // 檢查冷卻時間是否已到
        double currentTime = Server.CurrentTime;
        if (currentTime - _lastVoteTime < CooldownTime)
        {
            int timeLeft = (int)Math.Ceiling(CooldownTime - (currentTime - _lastVoteTime));
            player.PrintToChat($" {Prefix} 投 票 冷 卻 中 請 等 待 {ChatColors.Green}{timeLeft} {ChatColors.White}秒 後 再 試。");
            return;
        }

        // 1. 檢查參數是否足夠
        if (args.Length < 2)
        {
            player.PrintToChat($" {Prefix} 使 用 方 法： .rtv  <地 圖 名 稱>  例 如： {ChatColors.Yellow}.RTV de_mirage{ChatColors.White}");
            return;
        }

        // 2. 檢查當前是否已經有投票在進行
        if (voteHandler.IsVoteInProgress())
        {
            player.PrintToChat($" {Prefix} {ChatColors.Red}當 前 已 有 投 票 正 在 進 行 中，請 稍 後 再 試");
            return;
        }

        // 3. 取得玩家輸入的地圖，並強制轉小寫
        string inputMap = args[1].Trim().ToLower();
        string? matchedMap = _allowedMaps.FirstOrDefault(m => m.ToLower() == inputMap);

        if (matchedMap == null)
        {
            player.PrintToChat($" {Prefix} 伺 服 器 不 支 援 地 圖 [{args[1]}] ！");
            player.PrintToChat($" {Prefix} 可用地圖： {ChatColors.Yellow}{string.Join(", ", _allowedMaps)}");
            return;
        }

        // 5. 驗證通過，儲存正確的地圖名稱
        _targetMap = matchedMap;
        voteHandler.Init(); 

        // 6. 發起全服 20 秒投票
        voteHandler.SendYesNoVoteToAll(
            20.0f, 
            player.Slot, 
            "#SFUI_vote_changelevel", 
            _targetMap, 
            VoteResultCallback, 
            VoteHandlerCallback
        );

        _lastVoteTime = Server.CurrentTime;
        Server.PrintToChatAll($" {Prefix} 玩家 {ChatColors.Green}{player.PlayerName}{ChatColors.White} 發 起 了 投 票 換 圖 至 {ChatColors.Green}{_targetMap}{ChatColors.White}");
    }

    private bool VoteResultCallback(YesNoVoteInfo info)
    {
        // 將日誌邏輯簡化，避免插值表達式衝突
        foreach (var kvp in info.clientInfo) 
        {
            int slot = kvp.Value.Item1;
            string isYes = (kvp.Value.Item2 == (int)CastVote.VOTE_OPTION1) ? "Yes" : "No";
            Console.WriteLine($"[RTV Log] Player Key: {kvp.Key} | Slot: {slot} | Vote: {isYes}");
        }

        if(info.yes_votes > info.no_votes) 
        {
            Server.PrintToChatAll($" {Prefix} 投 票 通 過 {ChatColors.Green}3 秒 {ChatColors.White}後 更 換 地 圖 至 {ChatColors.Green}{_targetMap}");
            string mapCmd = _targetMap;
            
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
            case YesNoVoteAction.VoteAction_Start:
            {
                Server.PrintToChatAll($" {Prefix} 投 票 開 始！請 在 左 上角 選 擇{ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ]");
                break;
            }
            case YesNoVoteAction.VoteAction_Vote:
                break;
            case YesNoVoteAction.VoteAction_End:
            {
                if ((YesNoVoteEndReason)param1 == YesNoVoteEndReason.VoteEnd_Cancelled)
                {
                    Server.PrintToChatAll($" {Prefix} {ChatColors.Red}投 票 已 被 系 統 或 管 理 員 取 消");
                }
                break;
            }
        }
    }
}
