namespace ServiceLib.Resx;

/// <summary>First-run tour: a few short pages about what Detour does and where things are.</summary>
public static class TourContent
{
    private static bool Ru => AppText.IsRussian;

    public record Step(string Title, string Text);

    public static string WindowTitle => Ru ? "Как пользоваться Detour" : "How to use Detour";
    public static string Back => Ru ? "Назад" : "Back";
    public static string Next => Ru ? "Дальше" : "Next";
    public static string Done => Ru ? "Начать" : "Start";
    public static string Skip => Ru ? "Пропустить" : "Skip";
    public static string OpenTour => Ru ? "Обучение" : "Tour";

    public static IReadOnlyList<Step> Steps => Ru ? _ru : _en;

    private static readonly Step[] _ru =
    [
        new("Что делает Detour",
            "Detour сам решает, как пускать каждый сайт и программу: через VPN, через zapret (обход блокировок без VPN, напрямую, на полной скорости) или просто напрямую.\n\n" +
            "YouTube и Discord можно открывать через zapret, нейросети и заблокированные по IP сайты — через VPN, а банки и Госуслуги — напрямую, чтобы они не ругались на VPN."),
        new("1. Добавьте VPN",
            "Скопируйте ссылку на подписку от вашего VPN-сервиса (https://…, happ://…, vless://…) и нажмите Ctrl+V в окне Detour, или откройте «Серверы» → «Добавить».\n\n" +
            "Подписка обновляется сама. Если провайдер показывает остаток трафика и срок, они появятся на главном экране."),
        new("2. Подключитесь",
            "На главном экране большая кнопка «Подключить».\n\n" +
            "• VPN (TUN) — весь компьютер, включая игры и мессенджеры. Нужны права администратора.\n" +
            "• Прокси — только программы, которые слушают системный прокси (браузеры).\n\n" +
            "Рядом переключатели: kill switch, авто-смена сервера, игровой режим, переподключение после сна."),
        new("3. Правила",
            "Страница «Правила» — текст, по строке на сайт или программу:\n\n" +
            "youtube.com +zapret\n" +
            "Discord.exe +zapret+vpn   (сначала zapret, если не работает — VPN)\n" +
            "chatgpt.com +vpn = авто   (свой сервер с лучшим пингом)\n" +
            "@telegram +vpn            (готовый набор)\n\n" +
            "Готовые наборы добавляются кнопками справа. Блок «Не открывается сайт?» сам подскажет, какое правило нужно."),
        new("4. zapret",
            "zapret обходит блокировки без VPN: не тратит трафик подписки и не меняет ваш IP.\n\n" +
            "На странице zapret нажмите «Подобрать автоматически»: Detour переберёт варианты и оставит тот, что работает у вашего провайдера. Если провайдер что-то поменяет, Detour переподберёт сам."),
        new("5. Если что-то не работает",
            "На главном экране — «Инструменты»: проверка сайта, проверка блокировок, диагностика сети и карта блокировок.\n\n" +
            "В окне диагностики кнопка «Архив для помощи» собирает логи в один zip без ключей и паролей — его можно отправить тому, кто помогает.\n\n" +
            "Это обучение всегда можно открыть снова на странице «Справка»."),
    ];

    private static readonly Step[] _en =
    [
        new("What Detour does",
            "Detour decides how each site and app goes: through the VPN, through zapret (bypasses blocks without a VPN, directly, at full speed) or plain direct.\n\n" +
            "YouTube and Discord can go through zapret, AI services and IP-blocked sites through the VPN, banks directly so they don't complain about a VPN."),
        new("1. Add a VPN",
            "Copy the subscription link from your VPN provider (https://…, happ://…, vless://…) and press Ctrl+V in the Detour window, or open Servers → Add.\n\n" +
            "The subscription updates by itself. If the provider reports traffic and expiry, they show on the home page."),
        new("2. Connect",
            "The big Connect button is on the home page.\n\n" +
            "• VPN (TUN) — the whole computer, games and messengers included. Needs administrator rights.\n" +
            "• Proxy — only apps that use the system proxy (browsers).\n\n" +
            "Next to it: kill switch, auto failover, game mode, reconnect after sleep."),
        new("3. Rules",
            "The Rules page is text, one line per site or app:\n\n" +
            "youtube.com +zapret\n" +
            "Discord.exe +zapret+vpn   (zapret first, VPN if it doesn't work)\n" +
            "chatgpt.com +vpn = auto   (its own server, best ping)\n" +
            "@telegram +vpn            (preset)\n\n" +
            "Presets are added with the buttons on the right. \"Check a site\" suggests the rule you need."),
        new("4. zapret",
            "zapret bypasses blocks without a VPN: no subscription traffic, your IP stays the same.\n\n" +
            "On the zapret page press \"Auto-select\": Detour tries the variants and keeps the one that works with your ISP. If the ISP changes something, Detour re-picks by itself."),
        new("5. If something doesn't work",
            "The home page has Tools: site check, block check, network diagnostics and the block map.\n\n" +
            "In the diagnostics window \"Help archive\" packs the logs into one zip without keys and passwords — send it to whoever helps you.\n\n" +
            "This tour can be opened again on the Help page."),
    ];
}
