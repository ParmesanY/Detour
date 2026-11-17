using System.Diagnostics;

namespace ServiceLib.Manager;

/// <summary>
/// Game mode: notices a running game and, while it runs, stops the auto failover from switching servers.
/// A new server means a new IP (often a new country) mid-session: games drop the match, and anti-fraud
/// systems ban accounts for it (Roblox "Enforcement Evasion", Riot). The game's traffic keeps its own
/// connection rule: some games (SCP: Secret Laboratory and others) can't reach their servers from a
/// Russian IP at all and need zapret or the VPN, so nothing is forced to direct here.
/// </summary>
public sealed class GameModeManager
{
    private static readonly Lazy<GameModeManager> _instance = new(() => new());
    public static GameModeManager Instance => _instance.Value;
    private const string _tag = "GameMode";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>Built-in games and anti-cheat clients (exe names, case-insensitive).</summary>
    public static readonly IReadOnlyList<string> KnownGames =
    [
        "cs2.exe", "csgo.exe", "faceit.exe", "faceitclient.exe", "dota2.exe", "deadlock.exe", "project8.exe",
        "VALORANT-Win64-Shipping.exe", "vgc.exe", "League of Legends.exe",
        "RobloxPlayerBeta.exe", "SCPSL.exe", "r5apex.exe", "r5apex_dx12.exe",
        "FortniteClient-Win64-Shipping.exe", "EscapeFromTarkov.exe", "TslGame.exe", "RainbowSix.exe",
        "RainbowSix_Vulkan.exe", "Overwatch.exe", "GTA5.exe", "GTA5_Enhanced.exe", "RustClient.exe",
        "destiny2.exe", "cod.exe", "ModernWarfare.exe", "BlackOps6.exe", "Marvel-Win64-Shipping.exe",
        "eldenring.exe", "TheFinals.exe", "Discovery.exe", "WorldOfTanks.exe", "Tanki.exe",
        "WorldOfWarships.exe", "Warframe.x64.exe", "aces.exe", "PathOfExile.exe", "PathOfExileSteam.exe",
        "Wow.exe", "Hearthstone.exe", "Diablo IV.exe", "MonsterHunterWilds.exe", "HuntGame.exe",
        "DeadByDaylight-Win64-Shipping.exe", "Among Us.exe", "Phasmophobia.exe", "Lethal Company.exe",
        "Terraria.exe", "Rocket League.exe", "RocketLeague.exe", "FC25.exe", "FC26.exe",
    ];

    private CancellationTokenSource? _cts;
    private string? _activeGame;

    /// <summary>Exe name of the running game, or null.</summary>
    public string? ActiveGame => _activeGame;

    /// <summary>Game mode is on and a game is running: don't change the server under it.</summary>
    public bool HoldsServer => AppManager.Instance.Config.GameMode != false && _activeGame != null;

    /// <summary>Raised (any thread) when a game starts (its exe name) or stops (null).</summary>
    public event Action<string?>? StateChanged;

    public void Start()
    {
        if (_cts != null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var game = AppManager.Instance.Config.GameMode == false
                    ? null
                    : FindGame(RunningProcessNames(), KnownGames.Concat(AppManager.Instance.Config.GameProcesses ?? []));
                if (game != _activeGame)
                {
                    Logging.SaveLog(game != null ? $"{_tag}: {game} started, the server stays fixed" : $"{_tag}: {_activeGame} closed");
                    _activeGame = game;
                    StateChanged?.Invoke(game);
                }
                await Task.Delay(Interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
                await Task.Delay(Interval, token);
            }
        }
    }

    /// <summary>First known game among the running processes (names with or without ".exe"), or null.</summary>
    public static string? FindGame(IEnumerable<string> running, IEnumerable<string> known)
    {
        var set = new HashSet<string>(known.Select(Normalize).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        return running.Select(Normalize).FirstOrDefault(set.Contains) is { } hit ? hit + ".exe" : null;
    }

    private static string Normalize(string name)
    {
        var n = Path.GetFileName(name.Trim());
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    private static List<string> RunningProcessNames()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                names.Add(p.ProcessName);
            }
            catch
            {
                // exited meanwhile
            }
            finally
            {
                p.Dispose();
            }
        }
        return names;
    }
}
