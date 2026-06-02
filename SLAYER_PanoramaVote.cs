using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using PanoramaVote;

namespace SLAYER_PanoramaVote;

public partial class SLAYER_PanoramaVote : BasePlugin
{
    public override string ModuleName => "SimpleVote";
    public override string ModuleVersion => "1.0";
    public CPanoramaVote voteHandler;

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this);
        RegisterEventHandler<EventVoteCast>((@event, info) => {
            voteHandler.VoteCast(@event);
            return HookResult.Continue;
        });
    }

    // 玩家指令：!vt de_nuke
    [ConsoleCommand("vt", "玩家發起換圖投票")]
    [CommandHelper(minArgs: 1, usage: "<地圖名>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCommandVote(CCSPlayerController? player, CommandInfo info) 
    {
        if (player == null) return;
        string targetMap = info.GetArg(1);

        voteHandler.Init();
        
        // 發送投票給全體玩家，標題設為 SFUI_vote_changelevel
        // 使用白色或金色標籤 (根據你 platform_english.txt 的設定)
        voteHandler.SendYesNoVoteToAll(30.0f, VoteConstants.VOTE_CALLER_SERVER, 
            "#SFUI_vote_panorama_vote_white", 
            $"是否更換地圖至: {targetMap}?", 
            (resultInfo) => {
                // 投票結果處理
                if (resultInfo.yes_votes > resultInfo.no_votes) {
                    Server.PrintToChatAll($" {ChatColors.Green}投票通過！準備切換至: {targetMap}");
                    Server.ExecuteCommand($"changelevel {targetMap}");
                    return true;
                }
                Server.PrintToChatAll($" {ChatColors.Red}投票未通過。");
                return false;
            }, 
            (action, p1, p2) => { /* 這裡可以留空，或者加簡單的狀態廣播 */ });
    }
}
