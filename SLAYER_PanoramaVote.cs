using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using PanoramaVote; 
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Collections.Frozen; // 【.NET 10 升級】：引入凍結集合

// 【已移除 using System.Linq;】 貫徹 0 記憶體垃圾極致版

namespace SLAYER_PanoramaVote;

#pragma warning disable CS8618

public class PanoramaVoteConfig : BasePluginConfig
{
    [JsonPropertyName("VoteDurationSeconds")]
    public float VoteDurationSeconds { get; set; } = 60.0f; 
    [JsonPropertyName("VoteCooldownSeconds")]
    public float VoteCooldownSeconds { get; set; } = 90.0f; 
    [JsonPropertyName("MinPlayersRequired")]
    public int MinPlayersRequired { get; set; } = 6; 
    [JsonPropertyName("RequiredVotePercentage")]
    public float RequiredVotePercentage { get; set; } = 0.8f; 
    [JsonPropertyName("JoinMessageDelaySeconds")]
    public float JoinMessageDelaySeconds { get; set; } = 4.0f; 
    
    // 【.NET 10 升級】：集合表達式
    [JsonPropertyName("JoinMessages")]
    public List<string> JoinMessages { get; set; } = [
        "{Prefix} {White}DEMO下載以及相關指令請至網站 {Lime}c2t.clouds.tw",
        "{Prefix} {Orange}隨 機 隊 伍 {White}以及 {Orange}投 票 換 圖 {White}請參考網站相關指令",
        "{Prefix} {Lime}建 議 反 饋 {White}與 {Lime}異 常 問 題 {White}請至網站填寫問題回報"
    ];
}

public partial class SLAYER_PanoramaVote : BasePlugin, IPluginConfig<PanoramaVoteConfig>
{
    public override string ModuleName => "SLAYER_PanoramaVote";
    public override string ModuleVersion => "2.7_ZeroGC_Pro"; 
    public override string ModuleAuthor => "SLAYER / Optimized / UltimateVote";
    public override string ModuleDescription => "Panorama RTV, Shuffle Vote with Zero LINQ Garbage";
    
    public PanoramaVoteConfig Config { get; set; }
    public CPanoramaVote voteHandler; 
    
    private readonly string Prefix = $" [{ChatColors.Green}系統訊息{ChatColors.White}]";
    
    private enum VoteType { None, MapChange, Shuffle, Unshuffle }
    private VoteType _currentVoteType = VoteType.None;
    private string _targetMap = string.Empty;
    
    private double _lastMapVoteTime = -9999.0;      
    private double _lastShuffleVoteTime = -9999.0;  
    
    private bool _isMapChanging = false;

    // 【.NET 10 升級】：改用 FrozenSet 搭配不分大小寫比對，O(1) 物理極限查詢
    private readonly FrozenSet<string> _allowedMaps = new[] { 
        "de_mirage", "de_inferno", "de_dust2", "de_nuke", "de_anubis", "de_ancient", "de_cache" 
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public void OnConfigParsed(PanoramaVoteConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        voteHandler = new CPanoramaVote(this); 
        _isMapChanging = false;
        _currentVoteType = VoteType.None;
        
        AddCommandListener("say", OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);

        RegisterListener<Listeners.OnClientPutInServer>(playerSlot =>
        {
            // 【.NET 10 升級】：現代化模式匹配
            if (Utilities.GetPlayerFromSlot(playerSlot) is not { IsValid: true, IsBot: false, IsHLTV: false }) return;

            AddTimer(Config.JoinMessageDelaySeconds, () => 
            {
                if (Utilities.GetPlayerFromSlot(playerSlot) is { IsValid: true } p)
                {
                    foreach (var msg in Config.JoinMessages)
                    {
                        p.PrintToChat(ReplaceColorTags(msg));
                    }
                }
            });
        });

       RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            CCSGameRulesProxy? gameRules = null;
            foreach (var rule in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
            {
                gameRules = rule;
                break;
            }
            
            // 【.NET 10 升級】：巢狀屬性模式匹配
            if (gameRules is { GameRules.WarmupPeriod: false })
            {
                if (_currentVoteType != VoteType.None)
                {
                    voteHandler.CancelVote();
                    Server.PrintToChatAll($" {Prefix} 比賽已正式開始，{ChatColors.Green}投票{ChatColors.White} 被 {ChatColors.Orange}強制{ChatColors.White} 取消");
                    _currentVoteType = VoteType.None;
                }
            }
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnMapStart>(mapName => 
        {
            _isMapChanging = false;
            _currentVoteType = VoteType.None;
            _lastMapVoteTime = -9999.0;      
            _lastShuffleVoteTime = -9999.0;  
        });

        RegisterEventHandler<EventVoteCast>((@event, info) =>
        {
            voteHandler.VoteCast(@event); 
            return HookResult.Continue;
        });
    }

    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true }) return HookResult.Continue;
        
        string text = info.GetArg(1).Trim('"').Trim().ToLower();

        // 檢查準備指令
        if (text == ".r" || text == ".ready" || text == "!r" || text == "!ready" || text == ".unready" || text == "!unready")
        {
            if (_currentVoteType != VoteType.None || _isMapChanging)
            {
                if (_isMapChanging)
                {
                    player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，禁 止 輸 入 準 備 指 令{ChatColors.White}");
                    player.PrintToCenter("地 圖 即 將 更 換 ， 禁 止 輸 入 .R");
                }
                else
                {
                    player.PrintToChat($" {Prefix} 投票進行中 {ChatColors.Green} [ F 1 是 ]{ChatColors.White} 或 {ChatColors.DarkRed}[ F 2 否 ] {ChatColors.White}投票結束再準備");
                    player.PrintToCenter("請 投 票 結 束 ， 再 輸 入 .R 準 備");
                }
                return HookResult.Stop; 
            }
        }

        // 【新增防護】：如果地圖正在更換中，全面禁止輸入 .vote 或 .rtv 發起新投票
        if (text == ".rtv" || text == "!rtv" || text == ".vote" || text == "!vote")
        {
            if (_isMapChanging)
            {
                player.PrintToChat($" {Prefix} {ChatColors.Red}地 圖 即 將 更 換，無 法 發 起 投 票{ChatColors.White}");
                player.PrintToCenter("地 圖 即 將 更 換 ， 無 法 發 起 投 票");
                return HookResult.Stop;
            }
        }
        
        if (text.StartsWith(".rtv"))
        {
            // 【.NET 10 升級】：改用 ReadOnlySpan<char> 進行 0 GC 的切片解析，徹底消滅 string.Split
            ReadOnlySpan<char> textSpan = text.AsSpan
