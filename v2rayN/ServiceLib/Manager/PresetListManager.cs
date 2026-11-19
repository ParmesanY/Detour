namespace ServiceLib.Manager;

/// <summary>
/// Keeps the presets' domain lists fresh: once a week downloads the community lists from
/// itdoginfo/allow-domains (plain text, one domain per line) and adds them to the built-in presets.
/// The result is cached in guiConfigs, so the lists work offline and survive restarts.
/// </summary>
public sealed class PresetListManager
{
    private const string _tag = "PresetLists";
    private const string _baseUrl = "https://raw.githubusercontent.com/itdoginfo/allow-domains/main/";
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(7);

    /// <summary>Preset id → list file in the repository.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["blocked"] = "Russia/inside-raw.lst",
        ["discord"] = "Services/discord.lst",
        ["youtube"] = "Services/youtube.lst",
        ["telegram"] = "Services/telegram.lst",
        ["instagram"] = "Services/meta.lst",
        ["twitter"] = "Services/twitter.lst",
        ["roblox"] = "Services/roblox.lst",
        ["gemini"] = "Services/google_ai.lst",
    };

    private static readonly Lazy<PresetListManager> _instance = new(() => new());
    public static PresetListManager Instance => _instance.Value;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public sealed class Cache
    {
        public DateTime Updated { get; set; }
        public Dictionary<string, List<string>> Domains { get; set; } = [];
    }

    private static string CachePath => Utils.GetConfigPath("presetLists.json");

    public DateTime? LastUpdated { get; private set; }
    public int DomainCount { get; private set; }

    /// <summary>Raised after the lists were loaded or updated.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Applies the cached lists (no network). Call once at start-up.</summary>
    public void LoadCached()
    {
        VpnStrategyManager.LoadCachedList();
        ServerNetworkCheck.LoadCachedList();
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }
            var cache = JsonUtils.Deserialize<Cache>(File.ReadAllText(CachePath));
            if (cache?.Domains is { Count: > 0 })
            {
                Apply(cache);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public bool IsStale => LastUpdated is not { } updated || DateTime.Now - updated > UpdateInterval;

    /// <summary>Downloads every list; keeps the old copy of a list that failed. Returns an error text or null.</summary>
    public async Task<string?> UpdateAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var old = File.Exists(CachePath) ? JsonUtils.Deserialize<Cache>(File.ReadAllText(CachePath)) : null;
            var cache = new Cache { Updated = DateTime.Now };
            var failed = 0;
            using var handler = HttpClientHelper.Instance.CreateFallbackHandler(true);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
            foreach (var (id, file) in Sources)
            {
                List<string>? domains = null;
                try
                {
                    var text = await client.GetStringAsync(_baseUrl + file, cancellationToken);
                    domains = ParseList(text);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    Logging.SaveLog($"{_tag}: {file}: {ex.Message}");
                }
                // An empty or broken answer must not wipe a good list.
                if (domains is not { Count: > 0 })
                {
                    failed++;
                    domains = old?.Domains.GetValueOrDefault(id);
                }
                if (domains is { Count: > 0 })
                {
                    cache.Domains[id] = domains;
                }
            }
            await VpnStrategyManager.UpdateListAsync(client, cancellationToken);
            await ServerNetworkCheck.UpdateListAsync(client, cancellationToken);
            if (failed == Sources.Count)
            {
                return "не удалось скачать ни один список";
            }
            File.WriteAllText(CachePath, JsonUtils.Serialize(cache));
            Apply(cache);
            return failed > 0 ? $"не скачались {failed} из {Sources.Count} списков, оставлены старые" : null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return ex.Message;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Updates in the background when the cache is older than a week, then rebuilds the core config.</summary>
    public void UpdateInBackgroundIfStale()
    {
        if (!IsStale)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            // Let the core come up first: the download prefers the proxy.
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (await UpdateAsync() is null)
            {
                AppEvents.CoreReloadRequested.Publish();
            }
        });
    }

    private void Apply(Cache cache)
    {
        ConnectionPresets.SetExtraDomains(cache.Domains.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value));
        LastUpdated = cache.Updated;
        DomainCount = cache.Domains.Values.Sum(l => l.Count);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One domain per line. ".ua" means the whole zone and becomes "ua" (our plain domains already cover
    /// subdomains); comments, IPs, wildcards and anything that isn't a host name are skipped.
    /// </summary>
    public static List<string> ParseList(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
            if (line.Length == 0 || line.StartsWith('#') || line.Length > 253)
            {
                continue;
            }
            if (IPAddress.TryParse(line, out _) || !line.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.'))
            {
                continue;
            }
            if (seen.Add(line))
            {
                result.Add(line);
            }
        }
        return result;
    }
}
