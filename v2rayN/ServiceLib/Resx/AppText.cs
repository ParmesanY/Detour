namespace ServiceLib.Resx;

/// <summary>
/// UI strings of the features added in this fork (connection rules, zapret).
/// Russian for ru-* UI culture, English otherwise. Kept apart from the generated ResUI so the
/// upstream resource files stay mergeable.
/// </summary>
public static class AppText
{
    private static bool Ru => Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                              || AppManager.Instance.Config?.UiItem?.CurrentLanguage?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;

    private static string T(string ru, string en) => Ru ? ru : en;

    // Navigation
    public static string NavServers => T("Серверы", "Servers");
    public static string NavRules => T("Правила", "Rules");
    public static string NavZapret => "zapret";
    public static string NavConnections => T("Соединения", "Connections");
    public static string NavGroups => T("Группы", "Groups");
    public static string NavLog => T("Журнал", "Log");
    public static string NavSettings => T("Настройки", "Settings");
    public static string NavAppearance => T("Оформление", "Appearance");
    public static string NavHelp => T("Справка", "Help");
    public static string FontFamily => T("Шрифт", "Font");

    // Connection panel
    public static string ConnConnected => T("Подключено", "Connected");
    public static string ConnDisconnected => T("Не подключено", "Not connected");
    public static string ConnConnect => T("Подключить", "Connect");
    public static string ConnDisconnect => T("Отключить", "Disconnect");
    public static string ConnSelected => T("Сервер: {0}", "Server: {0}");
    public static string ConnViaTun => T("Весь компьютер через {0}", "Whole computer via {0}");
    public static string ConnViaProxy => T("Программы с системным прокси через {0}", "System-proxy apps via {0}");
    public static string ConnNoServer => T("Сначала добавьте сервер или подписку (Ctrl+V со ссылкой).",
        "Add a server or a subscription first (Ctrl+V with a link).");
    public static string ConnModeTun => T("Весь компьютер (TUN)", "Whole computer (TUN)");
    public static string ConnModeProxy => T("Системный прокси", "System proxy");
    public static string ConnModeHint => T(
        "TUN пропускает через клиент все программы и нужен для правил по приложениям. Системный прокси работает только с браузерами и программами, которые его поддерживают.",
        "TUN routes every app through the client and is needed for per-app rules. System proxy only covers browsers and apps that support it.");

    public static string WatchdogToVpn => T("{0}: напрямую через zapret не открывается, теперь через VPN.",
        "{0}: doesn't open directly through zapret, now via VPN.");
    public static string WatchdogToZapret => T("{0}: zapret работает, теперь напрямую без VPN.",
        "{0}: zapret works, now direct without VPN.");

    public static string ConflictTunAdapter => T("сетевой туннель", "tunnel adapter");
    public static string ConflictWarning => T(
        "Запущен другой VPN или обход блокировок: {0}. Два таких приложения мешают друг другу, и сайты могут перестать открываться. Закройте лишнее перед подключением.",
        "Another VPN or bypass tool is running: {0}. Two of them get in each other's way and sites may stop loading. Close the extra one before connecting.");
    public static string AutoRunWindows => T("Запускать вместе с Windows", "Start with Windows");
    public static string AutoRunWindowsHint => T(
        "Лучше включать, когда клиент запущен от имени администратора: тогда он стартует с правами для TUN и zapret без запроса UAC.",
        "Best turned on while the client runs as administrator: it then starts with rights for TUN and zapret without a UAC prompt.");
    public static string ConnectOnStartup => T("Подключаться при запуске", "Connect on startup");
    public static string ConnectOnStartupHint => T("Восстанавливает последнее подключение: тот же сервер и тот же режим.",
        "Restores the last connection: the same server and mode.");
    public static string ZapretNeedsAdminRestart => T("zapret включён: перезапускаю клиент с правами администратора.",
        "zapret is on: restarting the client as administrator.");

    public static string IpCheck => T("Проверить IP", "Check IP");
    public static string IpChecking => T("Проверяю…", "Checking…");
    public static string IpCheckResult => T("Сайты видят вас как {0} ({1}, {2}).", "Sites see you as {0} ({1}, {2}).");
    public static string IpCheckRussian => T("Сайты видят российский адрес {0} ({1}, {2}): трафик идёт мимо VPN или выбран сервер с выходом в России.",
        "Sites see a Russian address {0} ({1}, {2}): traffic bypasses the VPN or the server exits in Russia.");
    public static string IpCheckFailed => T("Не удалось проверить: сервис проверки IP недоступен.", "Check failed: the IP service is unreachable.");

    public static string KillSwitch => T("Kill switch", "Kill switch");
    public static string KillSwitchHint => T(
        "Если соединение с VPN оборвётся, интернет блокируется, пока клиент не переподключится. Так ничего не уйдёт в обход VPN. Нужны права администратора.",
        "If the VPN connection drops, internet is blocked until the client reconnects, so nothing leaks outside the VPN. Needs administrator rights.");
    public static string KillSwitchEngaged => T("VPN отключился — интернет заблокирован до переподключения (kill switch).",
        "VPN dropped — internet is blocked until it reconnects (kill switch).");
    public static string KillSwitchReleased => T("Kill switch снят, интернет снова доступен.", "Kill switch released, internet is available again.");
    public static string KillSwitchNeedsAdmin => T("Kill switch не сработал: клиент запущен без прав администратора.",
        "Kill switch could not engage: the client isn't running as administrator.");
    public static string KillSwitchFailed => T("Kill switch не сработал:", "Kill switch failed:");
    public static string KillSwitchUnblock => T("Разблокировать интернет", "Unblock internet");

    // Server table
    public static string ColServer => T("Сервер", "Server");
    public static string ColType => T("Тип", "Type");
    public static string FilterServers => T("Поиск по названию (Enter)", "Search by name (Enter)");
    public static string CtxConnect => T("Подключиться", "Connect");
    public static string CtxProperties => T("Свойства сервера…", "Server properties…");
    public static string CtxUrlTest => T("URL-тест выделенных", "URL test selected");
    public static string CtxPickCore => T("Подобрать ядро (Xray / sing-box)", "Pick core (Xray / sing-box)");
    public static string PickCoreSelectedOrAll => T(
        "Подобрать ядро: проверить ключи на Xray и sing-box и оставить у каждого то, где он работает быстрее. Выделенные ключи или весь список.",
        "Pick core: test keys on Xray and sing-box and keep the faster one for each. Selected keys or the whole list.");
    public static string PickCoreTesting => T("Проверяю ключи на ядре {0}…", "Testing keys on {0}…");
    public static string PickCoreDone => T("Подбор ядра завершён, изменено ключей: {0}", "Core picking done, keys changed: {0}");
    public static string CtxSpeedTest => T("Тест скорости выделенных", "Speed test selected");
    public static string CtxTcping => T("TCP-пинг выделенных", "TCP ping selected");
    public static string CtxUdpTest => T("UDP-тест выделенных", "UDP test selected");
    public static string CtxClone => T("Клонировать", "Clone");
    public static string CtxDelete => T("Удалить", "Delete");
    public static string CtxOrder => T("Порядок", "Order");
    public static string CtxShare => T("Поделиться (QR-код)", "Share (QR code)");
    public static string TestSelectedOrAll => T("Тест задержки: выделенные серверы, а если выделен один — вся группа",
        "Latency test: selected servers, or the whole group if only one is selected");
    public static string MixedTestSelectedOrAll => T("Полный тест: выделенные серверы, а если выделен один — вся группа",
        "Full test: selected servers, or the whole group if only one is selected");

    // Servers page
    public static string Groups => T("Группы", "Groups");
    public static string GroupNew => T("Новая группа…", "New group…");
    public static string GroupRename => T("Переименовать текущую…", "Rename current…");
    public static string GroupDelete => T("Удалить текущую…", "Delete current…");
    public static string GroupMoveHint => T("Перенести серверы: выделите их → правая кнопка → «Переместить в группу»",
        "Move servers: select them → right click → Move to group");
    public static string GroupNewPrompt => T("Название новой группы", "New group name");
    public static string GroupRenamePrompt => T("Новое название группы", "New group name");
    public static string GroupDeleteConfirm => T("Удалить группу «{0}» вместе с её серверами ({1})?", "Delete group \"{0}\" with its {1} servers?");
    public static string GroupSelectFirst => T("Сначала выберите группу над списком серверов (не «Все»).", "Select a group above the server list first (not \"All\").");
    public static string Ok => "OK";
    public static string Cancel => T("Отмена", "Cancel");
    public static string ServersAdd => T("Добавить", "Add");
    public static string ServersSubscriptions => T("Подписки", "Subscriptions");
    public static string Reload => T("Перезапустить", "Restart");

    // Rules page
    public static string RulesTitle => T("Правила подключений", "Connection rules");
    public static string RulesSubtitle => T(
        "Что идёт через VPN, что через zapret, а что напрямую. Правила проверяются сверху вниз, срабатывает первое подходящее.",
        "What goes through VPN, through zapret or directly. Rules are checked top to bottom, the first match wins.");
    public static string RulesSyntaxTitle => T("Как писать", "Syntax");
    public static string RulesSyntax => T(
        "Discord.exe +zapret+vpn   — приложение: сначала zapret, если не работает — VPN\n" +
        "youtube.com +zapret       — домен и все поддомены через zapret\n" +
        "chatgpt.com +vpn          — через VPN\n" +
        "1.2.3.0/24 +direct        — напрямую\n" +
        "ads.example.com +block    — заблокировать\n" +
        "spotify.com +warp         — через Cloudflare WARP (страница WARP)\n" +
        "@telegram +vpn            — готовый набор\n" +
        "# строка                  — выключенное правило или комментарий",
        "Discord.exe +zapret+vpn   — app: zapret first, VPN if it doesn't work\n" +
        "youtube.com +zapret       — domain and subdomains through zapret\n" +
        "chatgpt.com +vpn          — through VPN\n" +
        "1.2.3.0/24 +direct        — directly\n" +
        "ads.example.com +block    — block\n" +
        "spotify.com +warp         — through Cloudflare WARP (WARP page)\n" +
        "@telegram +vpn            — preset\n" +
        "# line                    — switched-off rule or comment");
    public static string RulesPresets => T("Готовые наборы", "Presets");
    public static string RulesPresetsHint => T("Рекомендованный режим уже подставлен, его можно поменять в тексте.",
        "The recommended mode is filled in, change it in the text if needed.");
    public static string RulesAddPreset => T("Добавить", "Add");
    public static string RulesSave => T("Сохранить и применить", "Save and apply");
    public static string RulesRevert => T("Отменить изменения", "Discard changes");
    public static string RulesSaved => T("Правила сохранены и применены.", "Rules saved and applied.");
    public static string RulesErrors => T("Есть ошибки, правила не сохранены:", "Rules not saved, fix these lines:");
    public static string RulesLearnTitle => T("Режим обучения", "Learning mode");
    public static string RulesLearnHint => T(
        "zapret не видит программы, только домены. Запустите приложение и нажмите «Слушать»: клиент запишет, к каким доменам оно обращается. Нужен режим TUN.",
        "zapret doesn't see apps, only domains. Start the app and press Listen: the client records the domains it connects to. Needs TUN mode.");
    public static string RulesLearnStart => T("Слушать 60 секунд", "Listen for 60 seconds");
    public static string RulesLearnNoProcess => T("В правилах нет приложений (*.exe).", "There are no app (*.exe) rules.");
    public static string RulesLearnDone => T("{0}: найдено доменов {1}, IP {2}.", "{0}: found {1} domains, {2} IPs.");
    public static string RulesFallbackActive => T("сейчас через VPN: zapret не сработал", "now via VPN: zapret didn't work");

    public static string RulesProfile => T("Профиль", "Profile");
    public static string RulesProfileHint => T("Сохраните текущие правила под именем, чтобы быстро к ним вернуться.",
        "Save the current rules under a name to switch back quickly.");
    public static string RulesProfileNamePlaceholder => T("Название профиля", "Profile name");
    public static string RulesProfileSave => T("Сохранить профиль", "Save profile");
    public static string RulesProfileLoad => T("Загрузить", "Load");
    public static string RulesProfileDelete => T("Удалить", "Delete");
    public static string RulesProfileLoaded => T("Профиль «{0}» загружен и применён.", "Profile \"{0}\" loaded and applied.");
    public static string RulesProfileSaved => T("Профиль «{0}» сохранён.", "Profile \"{0}\" saved.");
    public static string RulesOverride => T("Режим для всех правил", "Mode for all rules");
    public static string RulesOverrideHint => T(
        "Быстро отправить всё из списка через VPN или через zapret, не меняя сами правила. Блокировки остаются.",
        "Quickly send everything in the list through VPN or zapret without editing the rules. Blocks stay.");
    public static string RulesOverrideNone => T("Как в правилах", "As in rules");
    public static string RulesOverrideVpn => T("Всё через VPN", "All via VPN");
    public static string RulesOverrideZapret => T("Всё через zapret", "All via zapret");
    public static string RulesOverrideDirect => T("Всё напрямую", "All direct");

    public static string ModeVpn => "VPN";
    public static string ModeZapret => "zapret";
    public static string ModeZapretThenVpn => T("zapret → VPN", "zapret → VPN");
    public static string ModeDirect => T("Напрямую", "Direct");
    public static string ModeBlock => T("Блок", "Block");

    public static string ModeName(EConnectionMode mode) => mode switch
    {
        EConnectionMode.Vpn => ModeVpn,
        EConnectionMode.Zapret => ModeZapret,
        EConnectionMode.ZapretThenVpn => ModeZapretThenVpn,
        EConnectionMode.Direct => ModeDirect,
        EConnectionMode.Block => ModeBlock,
        EConnectionMode.Warp => "WARP",
        _ => mode.ToString(),
    };

    // zapret page
    public static string ZapretTitle => "zapret";
    public static string ZapretSubtitle => T(
        "Обход DPI без сервера: трафик идёт напрямую, а zapret мешает провайдеру его распознать. Сборка Flowseal zapret-discord-youtube.",
        "DPI bypass without a server: traffic goes directly and zapret keeps the ISP from recognising it. Flowseal zapret-discord-youtube build.");
    public static string ZapretNotInstalled => T("Не установлен", "Not installed");
    public static string ZapretInstalled => T("Установлена версия {0}", "Version {0} installed");
    public static string ZapretRunning => T("Работает", "Running");
    public static string ZapretStopped => T("Остановлен", "Stopped");
    public static string ZapretInstall => T("Скачать и установить", "Download and install");
    public static string ZapretUpdate => T("Проверить обновление", "Check for update");
    public static string ZapretEnabled => T("Включать вместе с клиентом", "Run together with the client");
    public static string ZapretStart => T("Запустить", "Start");
    public static string ZapretStop => T("Остановить", "Stop");
    public static string ZapretStrategy => T("Стратегия", "Strategy");
    public static string ZapretStrategyHint => T(
        "Способ обмана DPI. Разные провайдеры требуют разные стратегии. «Подобрать» перепробует их и оставит лучшую.",
        "How DPI is tricked. Different ISPs need different strategies. Auto-select tries them and keeps the best.");
    public static string ZapretAutoSelect => T("Подобрать автоматически", "Auto-select");
    public static string ZapretGameFilter => T("Игровой фильтр", "Game filter");
    public static string ZapretGameFilterHint => T(
        "Обрабатывать и порты игр (1024–65535). Включайте, только если игры без этого не работают.",
        "Also process game ports (1024–65535). Turn on only if games don't work without it.");
    public static string ZapretGameOff => T("Выключен", "Off");
    public static string ZapretGameAll => "TCP + UDP";
    public static string ZapretNeedsAdmin => T(
        "Для zapret нужны права администратора (драйвер WinDivert). Перезапустите клиент от имени администратора.",
        "zapret needs administrator rights (WinDivert driver). Restart the client as administrator.");
    public static string ZapretAutoResult => T("Лучшая стратегия: {0} ({1} из {2} сайтов открылись).", "Best strategy: {0} ({1} of {2} sites opened).");
    public static string ZapretAutoNothing => T("Ни одна стратегия не помогла. Возможно, сайты заблокированы по IP — тогда нужен VPN.",
        "No strategy helped. The sites may be blocked by IP, then only VPN helps.");
    public static string ZapretSource => T("Источник", "Source");

    // Connection panel readouts and sidebar sections (mono, uppercase)
    public static string PanelStatus => T("СТАТУС", "STATUS");
    public static string PanelServer => T("СЕРВЕР", "SERVER");
    public static string PanelMode => T("РЕЖИМ", "MODE");
    public static string PanelUptime => T("В СЕТИ", "UPTIME");
    public static string PanelDown => T("↓ ПРИЁМ", "↓ DOWN");
    public static string PanelUp => T("↑ ОТДАЧА", "↑ UP");
    public static string PanelDirect => T("НАПРЯМУЮ", "DIRECT");
    public static string PanelTraffic => T("ТРАФИК, 90 С", "TRAFFIC, 90 S");
    public static string PanelOnline => T("ПОДКЛЮЧЕНО", "ONLINE");
    public static string PanelOffline => T("ОТКЛЮЧЕНО", "OFFLINE");
    public static string ConnModeOff => T("ВЫКЛ", "OFF");
    public static string NavSectionNetwork => T("СЕТЬ", "NETWORK");
    public static string NavSectionMonitor => T("НАБЛЮДЕНИЕ", "MONITOR");
    public static string NavSectionSystem => T("СИСТЕМА", "SYSTEM");

    // Import servers from files
    public static string ImportFromFile => T("Из файла (.conf, .json)…", "From file (.conf, .json)…");
    public static string ImportFileFilter => T(
        "Конфигурации серверов (*.conf;*.json;*.txt)|*.conf;*.json;*.txt|Все файлы (*.*)|*.*",
        "Server configs (*.conf;*.json;*.txt)|*.conf;*.json;*.txt|All files (*.*)|*.*");
    public static string ImportAdded => T("Добавлено серверов: {0} ({1})", "Servers added: {0} ({1})");
    public static string ImportFailed => T("Не удалось разобрать {0}", "Could not read {0}");
    public static string ImportUnknown => T(
        "{0}: формат не распознан. Поддерживаются WireGuard .conf, полный JSON-конфиг Xray или sing-box и файл со ссылками.",
        "{0}: unknown format. Supported: WireGuard .conf, full Xray or sing-box JSON config, a file with links.");
    public static string ImportOpenVpn => T(
        "{0}: это OpenVPN, его ядра не поддерживают. У Proton VPN и многих других сервисов можно скачать WireGuard-конфиг (.conf) — он подойдёт.",
        "{0}: OpenVPN is not supported by the cores. Proton VPN and many others offer a WireGuard config (.conf) instead.");
    public static string ImportAmneziaNote => T(
        "В файле есть параметры AmneziaWG (Jc, S1, H1…): ядра их не понимают, сервер добавлен как обычный WireGuard и в России может не подключиться.",
        "The file has AmneziaWG settings (Jc, S1, H1…): the cores ignore them, the server is added as plain WireGuard and may be blocked.");
    public static string UpdateAvailableNotice => T("Доступна новая версия Detour {0}: Справка → Обновить", "Detour {0} is available: Help → Update");
    public static string UpdateHeader => T("ВЕРСИЯ", "VERSION");
    public static string UpdateCheck => T("Проверить обновления", "Check for updates");
    public static string UpdateInstall => T("Обновить до {0}", "Update to {0}");
    public static string UpdateChecking => T("Проверяю…", "Checking…");
    public static string UpdateLatest => T("Установлена последняя версия", "You have the latest version");
    public static string UpdateUnreachable => T("GitHub недоступен или релизов нет. Попробуйте при включённом VPN.", "GitHub unreachable or no releases. Try with the VPN on.");
    public static string UpdateNotConfigured => T("Локальная сборка: самообновление отключено", "Local build: self-update is off");
    public static string UpdateConfirm => T("Скачать {0} и перезапустить Detour? Серверы, правила и настройки сохранятся.",
        "Download {0} and restart Detour? Servers, rules and settings are kept.");
    public static string UpdateFailed => T("Не удалось обновить: {0}", "Update failed: {0}");

    public static string AutoPickCoreSwitched => T("«{0}» лучше работает на ядре {1}: переподключаюсь", "\"{0}\" works better on {1}: reconnecting");
    public static string AutoFailover => T("Авто-смена сервера", "Auto failover");
    public static string AutoFailoverHint => T(
        "Если текущий сервер перестал пропускать трафик (3 проверки подряд, а обычный интернет есть), Detour переключится на другой сервер той же подписки: сначала с лучшим последним пингом.",
        "If the active server stops passing traffic (3 checks in a row while the plain internet works), Detour switches to another server of the same subscription, best last ping first.");
    public static string AutoFailoverSwitched => T("Сервер не отвечал, переключаюсь на «{0}»", "Server stopped responding, switching to \"{0}\"");

    // First-run wizard
    public static string WizTitle => T("Добро пожаловать в Detour", "Welcome to Detour");
    public static string WizIntro => T(
        "Два шага, и всё заработает. Любой шаг можно пропустить и сделать позже.",
        "Two steps and you're set. Any step can be skipped and done later.");
    public static string WizStep1 => T("1. Подписка или сервер", "1. Subscription or server");
    public static string WizStep1Hint => T(
        "Вставьте ссылку на подписку от вашего VPN-провайдера (https://…) или ссылку на сервер (vless://, vmess://, trojan://…).",
        "Paste the subscription link from your VPN provider (https://…) or a server link (vless://, vmess://, trojan://…).");
    public static string WizPaste => T("Вставить", "Paste");
    public static string WizAdd => T("Добавить", "Add");
    public static string WizAdding => T("Загружаю серверы…", "Loading servers…");
    public static string WizAdded => T("Готово: серверов — {0}", "Done: {0} servers");
    public static string WizAddFailed => T("Серверы не добавились. Проверьте ссылку или включите другой VPN на время загрузки.",
        "No servers were added. Check the link, or turn on another VPN while it loads.");
    public static string WizStep2 => T("2. zapret для YouTube и Discord", "2. zapret for YouTube and Discord");
    public static string WizStep2Hint => T(
        "zapret открывает YouTube и Discord без VPN: быстрее и без нагрузки на сервер. Detour сам переберёт стратегии и оставит ту, что работает у вашего провайдера (1–3 минуты).",
        "zapret opens YouTube and Discord without the VPN: faster and with no load on the server. Detour tries the strategies and keeps the one that works with your ISP (1–3 minutes).");
    public static string WizZapretAdmin => T("Нужны права администратора: Detour перезапустится и продолжит с этого шага.",
        "Administrator rights are needed: Detour restarts and continues from this step.");
    public static string WizZapretRestart => T("Перезапустить от администратора", "Restart as administrator");
    public static string WizZapretPick => T("Подобрать стратегию", "Find a strategy");
    public static string WizZapretNotInstalled => T("zapret не установлен: его можно скачать позже на странице zapret.",
        "zapret is not installed: download it later on the zapret page.");
    public static string WizDone => T("Готово", "Done");
    public static string WizSkip => T("Пропустить", "Skip");
    public static string WizFinishHint => T(
        "Дальше: выберите сервер в списке и нажмите «Подключить». Что идёт через VPN, а что напрямую, настраивается на странице «Правила».",
        "Next: pick a server in the list and press Connect. What goes through the VPN and what goes direct is set on the Rules page.");

    // WARP page
    public static string NavWarp => "WARP";
    public static string WarpTitle => "Cloudflare WARP";
    public static string WarpSubtitle => T(
        "Бесплатный WARP от Cloudflare как отдельный канал. Detour запускает свой локальный прокси (на встроенном Xray), " +
        "а что через него пойдёт, решают правила с +warp. Выход в ближайшем городе Cloudflare: от провайдера трафик скрыт, " +
        "но сайты, закрытые для России, могут остаться закрытыми.",
        "Free Cloudflare WARP as a separate route. Detour runs its own local proxy (on the bundled Xray), " +
        "and rules with +warp decide what goes through it. Exits in a nearby Cloudflare city: hidden from the ISP, " +
        "but sites closed to Russia may stay closed.");
    public static string WarpRegister => T("Зарегистрировать", "Register");
    public static string WarpReRegister => T("Новая регистрация", "Register again");
    public static string WarpRegisterHint => T(
        "Создаёт бесплатное устройство WARP. Делается один раз, ключи хранятся в настройках.",
        "Creates a free WARP device. Needed once; the keys are kept in the settings.");
    public static string WarpRegistering => T("Регистрирую устройство в Cloudflare…", "Registering a device with Cloudflare…");
    public static string WarpRegistered => T("Устройство WARP зарегистрировано", "WARP device registered");
    public static string WarpNotRegistered => T("Устройство не зарегистрировано", "No device registered");
    public static string WarpRegisterFailed => T(
        "Cloudflare не ответил на регистрацию. Попробуйте ещё раз при подключённом VPN.",
        "Cloudflare did not answer the registration. Try again with the VPN connected.");
    public static string WarpNoSingbox => T("Для ключей WireGuard нужен sing-box: скачайте ядра.", "sing-box is needed for WireGuard keys: download the cores.");
    public static string WarpNoXray => T("Не найден Xray: скачайте ядра.", "Xray not found: download the cores.");
    public static string WarpPortBusy => T("Порт {0} занят другой программой. Укажите другой порт.",
        "Port {0} is used by another program. Choose another port.");
    public static string WarpExited => T("Прокси WARP завершился с кодом {0}. Смотрите журнал.", "The WARP proxy exited with code {0}. See the log.");
    public static string WarpRunning => T("Работает", "Running");
    public static string WarpStopped => T("Остановлен", "Stopped");
    public static string WarpDevice => T("Устройство {0}", "Device {0}");
    public static string WarpEnabled => T("Запускать вместе с клиентом", "Start with the client");
    public static string WarpNoise => T("Шум перед рукопожатием", "Noise before the handshake");
    public static string WarpNoiseHint => T(
        "Случайные UDP-пакеты перед WireGuard: помогают пройти DPI, который режет обычный WireGuard.",
        "Random UDP packets before WireGuard: help get past DPI that drops plain WireGuard.");
    public static string WarpEndpoint => T("Адрес Cloudflare", "Cloudflare endpoint");
    public static string WarpEndpointHint => T(
        "Если WARP не подключается, попробуйте другой адрес или порт. Формат ip:порт.",
        "If WARP doesn't connect, try another address or port. Format ip:port.");
    public static string WarpEndpointInvalid => T("Нужен адрес вида 162.159.192.1:2408", "Expected an address like 162.159.192.1:2408");
    public static string WarpPortHint => T("Локальный SOCKS-порт на 127.0.0.1. Им можно пользоваться и в других программах.",
        "Local SOCKS port on 127.0.0.1. Other programs can use it too.");
    public static string WarpTest => T("Проверить", "Test");
    public static string WarpTesting => T("Проверяю через WARP…", "Testing through WARP…");
    public static string WarpTestOk => T("WARP работает: {0}, {1}, {2} мс", "WARP works: {0}, {1}, {2} ms");
    public static string WarpTestNotWarp => T("Ответ получен, но не через WARP", "Got an answer, but not through WARP");
    public static string WarpTestFailed => T("Не удалось: {0}", "Failed: {0}");
    public static string WarpTrayItem => "Cloudflare WARP";
    public static string WarpRegisterFirst => T("Сначала зарегистрируйте устройство на странице WARP", "Register a device on the WARP page first");
    public static string WarpNoRulesHint => T(
        "WARP запущен, но через него сейчас ничего не идёт: нет ни одного правила с +warp. " +
        "Добавьте на странице «Правила» набор @warp или своё правило, например example.com +warp.",
        "WARP is running, but nothing goes through it: there is no +warp rule. " +
        "Add the @warp preset on the Rules page or a rule of your own, e.g. example.com +warp.");
    public static string WarpRulesNote => T(
        "Пока WARP выключен, правила с +warp идут через VPN. Пример правила: spotify.com +warp",
        "While WARP is off, +warp rules go through the VPN. Example rule: spotify.com +warp");

    public static string DiagButton =>T("Диагностика", "Diagnostics");
    public static string DiagTitle => T("Диагностика Detour", "Detour diagnostics");
    public static string DiagRunning => T("Проверяю процессы, порты, DNS и доступ к сайтам (до минуты)…",
        "Checking processes, ports, DNS and site access (up to a minute)…");
    public static string DiagHint => T("Адреса серверов, IP и имя пользователя скрыты. Отчёт можно приложить к issue на GitHub.",
        "Server addresses, IPs and the user name are masked. Attach the report to a GitHub issue.");
    public static string DiagCopy => T("Копировать", "Copy");
    public static string DiagSave => T("Сохранить…", "Save…");
    public static string DiagRerun => T("Повторить", "Run again");
    public static string DiagCopied => T("Отчёт скопирован", "Report copied");

    public static string ImportDropHint => T("Отпустите, чтобы добавить серверы из файлов", "Drop to add servers from files");

    public static string CorePortBusy => T(
        "Не подключаюсь: порт {0} занят другой программой — {1}. Скорее всего, это второй VPN-клиент или старая копия программы. Закройте её и подключитесь снова.",
        "Not connecting: port {0} is used by another program, {1}. Most likely another VPN client or an old copy of this app. Close it and connect again.");

    // TG WS Proxy
    public static string NavTelegram => "Telegram";
    public static string TgTitle => "TG WS Proxy";
    public static string TgSubtitle => T(
        "Локальный прокси для Telegram Desktop. Трафик Telegram идёт к серверам Telegram через WebSocket по TLS, " +
        "без VPN и сторонних серверов. Работает только для приложения Telegram на этом компьютере.",
        "Local proxy for Telegram Desktop. Telegram traffic goes to Telegram's own servers over WebSocket/TLS, " +
        "no VPN or third-party servers. Works only for the Telegram app on this computer.");
    public static string TgInstall => T("Скачать", "Download");
    public static string TgUpdate => T("Обновить", "Update");
    public static string TgStart => T("Запустить", "Start");
    public static string TgStop => T("Остановить", "Stop");
    public static string TgEnabled => T("Запускать вместе с клиентом", "Start with the client");
    public static string TgInstalled => T("Установлена версия {0}", "Installed version {0}");
    public static string TgNotInstalled => T("Не установлен", "Not installed");
    public static string TgRunning => T("Работает", "Running");
    public static string TgStopped => T("Остановлен", "Stopped");
    public static string TgStartedNoPort => T("Запущен, но порт пока не открылся. Подождите несколько секунд.",
        "Started, but the port is not open yet. Wait a few seconds.");
    public static string TgExited => T("Прокси завершился с кодом {0}. Журнал: {1}", "The proxy exited with code {0}. Log: {1}");
    public static string TgForeignRunning => T("Уже запущен другой TG WS Proxy ({0}). Закройте его в трее и попробуйте снова.",
        "Another TG WS Proxy is running ({0}). Close it from the tray and try again.");
    public static string TgPortBusy => T("Порт {0} занят другой программой. Укажите другой порт.",
        "Port {0} is used by another program. Choose another port.");
    public static string TgConnect => T("Подключить Telegram", "Connect Telegram");
    public static string TgConnectHint => T(
        "Откроет Telegram Desktop с предложением добавить прокси. Нажмите «Подключить» в Telegram. Делается один раз.",
        "Opens Telegram Desktop offering to add the proxy. Press \"Connect\" in Telegram. Needed once.");
    public static string TgCopyLink => T("Скопировать ссылку", "Copy link");
    public static string TgLinkCopied => T("Ссылка скопирована", "Link copied");
    public static string TgPort => T("Порт", "Port");
    public static string TgPortHint => T("Локальный порт на 127.0.0.1. После смены подключите Telegram заново.",
        "Local port on 127.0.0.1. Connect Telegram again after changing it.");
    public static string TgNewSecret => T("Новый секрет", "New secret");
    public static string TgNewSecretHint => T(
        "Секрет защищает прокси от чужих программ на компьютере. После смены подключите Telegram заново.",
        "The secret keeps other programs on this computer off the proxy. Connect Telegram again after changing it.");
    public static string TgOpenLog => T("Журнал", "Log");
    public static string TgRoutingNote => T(
        "Пока прокси включён, его трафик всегда идёт напрямую, мимо VPN, даже с kill switch. " +
        "Правило @telegram на сам Telegram Desktop больше не влияет: он ходит в локальный прокси. " +
        "Звонки и веб-версия Telegram по-прежнему идут по правилу @telegram.",
        "While the proxy is on, its traffic always goes direct, bypassing the VPN, even with the kill switch. " +
        "The @telegram rule no longer affects Telegram Desktop itself: it talks to the local proxy. " +
        "Calls and Telegram Web still follow the @telegram rule.");
}
