namespace ServiceLib.Handler;

/// <summary>A ready-made set of domains/IPs/processes for a popular service, with a recommended mode.</summary>
public record ConnectionPreset(
    string Id,
    string Name,
    EConnectionMode RecommendedMode,
    string Why,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Ips,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string>? ProbeUrls = null)
{
    public string Target => ConnectionRuleParser.PresetPrefix + Id;
}

/// <summary>
/// Built-in presets ("recommendations"). Domains are plain (example.com = domain and subdomains),
/// so the same list feeds both the core routing rules and zapret hostlists.
/// </summary>
public static class ConnectionPresets
{
    private static readonly IReadOnlyList<ConnectionPreset> _builtin =
    [
        new("discord", "Discord", EConnectionMode.ZapretThenVpn,
            "Блокируется по DPI. zapret обычно справляется и с чатом, и с голосом, пинг ниже. VPN — запасной вариант.",
            ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com",
             "discord.dev", "discord.gift", "discord.new", "discord.co", "discordstatus.com", "dis.gd",
             "discord-activities.com", "discordactivities.com", "discordsays.com"],
            [],
            ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "Vesktop.exe"],
            // The site may load while the gateway, CDN or media hosts are blocked.
            ["https://discord.com/", "https://gateway.discord.gg/", "https://cdn.discordapp.com/", "https://media.discordapp.net/"]),

        new("youtube", "YouTube", EConnectionMode.ZapretThenVpn,
            "Замедляется по DPI. zapret даёт полную скорость без промежуточного сервера. VPN — запасной вариант.",
            ["youtube.com", "youtu.be", "yt.be", "googlevideo.com", "ytimg.com", "ggpht.com",
             "youtube-nocookie.com", "youtubei.googleapis.com", "youtube.googleapis.com", "youtubekids.com",
             "wide-youtube.l.google.com", "youtubeembeddedplayer.googleapis.com"],
            [],
            [],
            // The page may load while video (googlevideo) and thumbnails (ytimg) are throttled.
            ["https://www.youtube.com/", "https://i.ytimg.com/generate_204", "https://redirector.googlevideo.com/generate_204"]),

        new("telegram", "Telegram", EConnectionMode.Vpn,
            "Звонки и медиа режут по IP, а против блокировок по IP zapret бессилен.",
            ["telegram.org", "t.me", "telegram.me", "telegra.ph", "telesco.pe", "tdesktop.com", "cdn-telegram.org"],
            ["91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22", "91.108.20.0/22",
             "91.108.56.0/22", "95.161.64.0/20", "149.154.160.0/20", "185.76.151.0/24"],
            ["Telegram.exe", "AyuGram.exe", "Kotatogram.exe"]),

        new("openai", "ChatGPT / OpenAI", EConnectionMode.Vpn,
            "OpenAI сам не пускает российские IP, поможет только VPN.",
            ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "sora.com"],
            [],
            ["ChatGPT.exe"]),

        new("claude", "Claude / Anthropic", EConnectionMode.Vpn,
            "Недоступен с российских IP, поможет только VPN.",
            ["claude.ai", "anthropic.com", "claude.com", "claudeusercontent.com"],
            [],
            ["Claude.exe"]),

        new("instagram", "Instagram / Facebook", EConnectionMode.Vpn,
            "Блокируется по IP, zapret не поможет.",
            ["instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net", "facebook.net", "fb.com", "threads.net"],
            [],
            []),

        new("twitter", "X (Twitter)", EConnectionMode.Vpn,
            "Блокируется по IP, zapret не поможет.",
            ["x.com", "twitter.com", "twimg.com", "t.co"],
            [],
            []),

        new("whatsapp", "WhatsApp", EConnectionMode.Vpn,
            "Звонки и медиа ограничены по IP, zapret не поможет.",
            ["whatsapp.com", "whatsapp.net", "wa.me"],
            [],
            ["WhatsApp.exe"]),

        new("signal", "Signal", EConnectionMode.Vpn,
            "Заблокирован в России, поможет только VPN.",
            ["signal.org", "whispersystems.org", "signal.art", "signal.group"],
            [],
            ["Signal.exe"]),

        new("viber", "Viber", EConnectionMode.Vpn,
            "Заблокирован в России, поможет только VPN.",
            ["viber.com", "viber.io"],
            [],
            ["Viber.exe"]),

        new("roblox", "Roblox", EConnectionMode.ZapretThenVpn,
            "Блокируется по DPI. zapret часто помогает и не добавляет пинг в играх. VPN — запасной вариант.",
            ["roblox.com", "rbxcdn.com", "rbx.com", "robloxlabs.com", "roblox.qq.com"],
            [],
            ["RobloxPlayerBeta.exe", "RobloxStudioBeta.exe"]),

        new("rutracker", "Rutracker", EConnectionMode.ZapretThenVpn,
            "Блокируется по домену, обычно хватает zapret.",
            ["rutracker.org", "rutracker.cc", "rutracker.net", "t-ru.org"],
            [],
            []),

        new("spotify", "Spotify", EConnectionMode.Vpn,
            "Spotify не работает с российскими IP, поможет только VPN.",
            ["spotify.com", "scdn.co", "spotifycdn.com", "spotify.design", "pscdn.co", "spotilocal.com"],
            [],
            ["Spotify.exe"]),

        new("soundcloud", "SoundCloud", EConnectionMode.Vpn,
            "Заблокирован в России, поможет только VPN.",
            ["soundcloud.com", "sndcdn.com", "soundcloud.app.goo.gl"],
            [],
            []),

        new("gemini", "Google Gemini / AI Studio", EConnectionMode.Vpn,
            "ИИ-сервисы Google недоступны с российских IP.",
            ["gemini.google.com", "aistudio.google.com", "notebooklm.google.com", "generativelanguage.googleapis.com",
             "bard.google.com", "labs.google"],
            [],
            []),

        new("linkedin", "LinkedIn", EConnectionMode.Vpn,
            "Заблокирован с 2016 года, поможет только VPN.",
            ["linkedin.com", "licdn.com", "lnkd.in"],
            [],
            []),

        new("notion", "Notion", EConnectionMode.Vpn,
            "Notion ушёл из России и ограничивает пользователей отсюда, лучше через VPN.",
            ["notion.so", "notion.site", "notion.com", "notion-static.com"],
            [],
            ["Notion.exe"]),

        new("figma", "Figma", EConnectionMode.Vpn,
            "Figma ограничивает пользователей из России, лучше через VPN.",
            ["figma.com", "figmausercontent.com", "figma.net"],
            [],
            ["Figma.exe"]),

        new("twitch", "Twitch", EConnectionMode.Direct,
            "Не заблокирован, напрямую быстрее всего. Если начнёт тормозить — поставьте +zapret+vpn.",
            ["twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net", "ext-twitch.tv"],
            [],
            []),

        new("steam", "Steam", EConnectionMode.Direct,
            "Работает напрямую. Через VPN загрузки будут только медленнее.",
            ["steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com", "steamserver.net",
             "steamgames.com", "steamusercontent.com"],
            [],
            ["steam.exe", "steamwebhelper.exe"]),

        // Game traffic is not TLS, so cores can't see the server name: only the process rule matches.
        new("minecraft", "Minecraft", EConnectionMode.Direct,
            "Крупные серверы (CubeCraft, Hypixel и др.) банят IP-адреса VPN: «Are you using a VPN?». Напрямую пинг ниже. " +
            "Правило по процессу Java, поэтому напрямую пойдут и другие Java-программы.",
            ["minecraft.net", "mojang.com", "minecraftservices.com", "cubecraft.net", "hypixel.net"],
            [],
            ["javaw.exe", "java.exe", "Minecraft.Windows.exe", "Minecraft.exe", "prismlauncher.exe", "MinecraftLauncher.exe",
             "Lunar Client.exe", "CurseForge.exe"]),

        new("russia", "Российские сайты и сервисы", EConnectionMode.Direct,
            "Банки, Госуслуги и маркетплейсы часто не пускают зарубежные IP, поэтому напрямую.",
            ["geosite:category-ru", "ru", "xn--p1ai", "su", "yandex.net", "yandex.com", "vk.com", "vk.ru",
             "userapi.com", "mail.ru", "ok.ru", "ozon.ru", "wildberries.ru", "wb.ru", "avito.ru", "gosuslugi.ru"],
            ["geoip:ru"],
            []),

        new("torrent", "Торрент-клиенты", EConnectionMode.Direct,
            "Торренты через VPN перегружают сервер и часто запрещены его правилами.",
            [],
            [],
            ["qbittorrent.exe", "uTorrent.exe", "BitTorrent.exe", "transmission-qt.exe", "tixati.exe"]),

        // Filled from the public list on the first update (PresetListManager); these few keep it useful offline.
        new("blocked", "Все заблокированные в РФ (список)", EConnectionMode.Vpn,
            "Около тысячи сайтов, заблокированных Роскомнадзором, из открытого списка itdoginfo/allow-domains. " +
            "Detour обновляет список сам раз в неделю. Правила выше в списке важнее этого.",
            ["rutracker.org", "nnmclub.to", "linkedin.com", "instagram.com", "facebook.com", "x.com", "twitter.com",
             "meduza.io", "4pda.to", "rezka.ag", "kinozal.tv", "signal.org", "viber.com", "soundcloud.com"],
            [],
            []),
    ];

    private static volatile IReadOnlyList<ConnectionPreset> _all = _builtin;
    private static volatile ConnectionPreset? _warp;

    /// <summary>Built-in presets with the domains from the downloaded lists added (see <see cref="SetExtraDomains"/>).</summary>
    public static IReadOnlyList<ConnectionPreset> All => _all;

    /// <summary>
    /// Adds domains from updated public lists to the built-in presets (preset id → domains). The built-in
    /// domains always stay, so a broken download can only add, never remove.
    /// </summary>
    public static void SetExtraDomains(IReadOnlyDictionary<string, IReadOnlyList<string>>? extra)
    {
        var merged = _builtin.Select(p => extra != null && extra.TryGetValue(p.Id, out var more) && more.Count > 0
                ? p with { Domains = [.. p.Domains.Concat(more).Distinct(StringComparer.OrdinalIgnoreCase)] }
                : p)
            .ToList();
        _all = merged;
        _warp = null;
    }

    /// <summary>
    /// "@warp": services blocked by the ISP (Roskomnadzor) that don't refuse Russian users themselves.
    /// WARP hides the traffic from the ISP, which is all they need; its exit may still look Russian,
    /// so services that refuse Russia (OpenAI, Spotify…) stay on VPN. Built from the presets above.
    /// </summary>
    private static readonly string[] _warpPresetIds = ["instagram", "twitter", "linkedin", "rutracker", "signal", "viber"];

    public static ConnectionPreset Warp => _warp ??= BuildWarp(_all);

    private static ConnectionPreset BuildWarp(IReadOnlyList<ConnectionPreset> all) => new("warp", "Через WARP: заблокированные в РФ", EConnectionMode.Warp,
        "Сайты, которые блокирует провайдер, но сами пускают россиян: Instagram, X, LinkedIn, Rutracker, Signal, Viber. " +
        "WARP скрывает трафик от провайдера и не нагружает ваш VPN-сервер. Нужна включённая страница WARP, без неё — через VPN.",
        [.. _warpPresetIds.SelectMany(id => all.First(p => p.Id == id).Domains).Distinct(StringComparer.OrdinalIgnoreCase)],
        [],
        [.. _warpPresetIds.SelectMany(id => all.First(p => p.Id == id).Processes)]);

    /// <summary>All presets, including the composite ones.</summary>
    public static IEnumerable<ConnectionPreset> AllWithComposite => All.Append(Warp);

    public static ConnectionPreset? Find(string target)
    {
        var id = target.StartsWith(ConnectionRuleParser.PresetPrefix, StringComparison.Ordinal)
            ? target[ConnectionRuleParser.PresetPrefix.Length..]
            : target;
        return AllWithComposite.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rules a new user starts with.</summary>
    public static List<ConnectionRule> DefaultRules()
    {
        return
        [
            new() { Target = "@russia", Mode = EConnectionMode.Direct },
            new() { Target = "@torrent", Mode = EConnectionMode.Direct },
            new() { Target = "@minecraft", Mode = EConnectionMode.Direct },
            new() { Target = "@discord", Mode = EConnectionMode.ZapretThenVpn },
            new() { Target = "@youtube", Mode = EConnectionMode.ZapretThenVpn },
        ];
    }
}
