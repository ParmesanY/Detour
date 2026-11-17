<p align="center"><img src="docs/branding/logo.png" width="96" alt="Detour"></p>

<h1 align="center">Detour</h1>

<p align="center">VPN-клиент для Windows (а также macOS и Linux, бета) со встроенным zapret и правилами «что через VPN, что через zapret, что напрямую».<br>
A Windows VPN client with built-in zapret and per-app / per-site routing rules.</p>

---

## Что это

Detour — форк [v2rayN](https://github.com/2dust/v2rayN) для пользователей в России. Он решает одну задачу:
чтобы каждый сервис шёл самым быстрым путём, который у вас сейчас работает, и вам не приходилось
вручную включать и выключать VPN.

- **Правила подключений.** Для приложения, сайта, IP или готового набора выбирается режим:
  `VPN`, `zapret`, `zapret → VPN`, `напрямую` или `блок`. Короткая запись: `Discord.exe +zapret+vpn`,
  `@youtube +zapret+vpn`, `@russia +direct`.
- **Встроенный zapret** (сборка [Flowseal](https://github.com/Flowseal/zapret-discord-youtube)): скачивается
  одной кнопкой, стратегия подбирается автоматически.
- **zapret → VPN со сторожем.** Сервис стартует через VPN; клиент сам проверяет прямой путь через zapret
  и переводит его туда, когда тот работает, а при поломке возвращает на VPN.
- **Готовые наборы** для Discord, YouTube, Telegram, ChatGPT, Claude, Instagram, X, Spotify, Roblox, Steam,
  Minecraft и других, с рекомендованным режимом.
- **Режим обучения:** клиент запоминает, куда ходит приложение, и добавляет эти адреса в правило.
- **TG WS Proxy** ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)): Telegram Desktop
  без VPN, кнопка «Подключить Telegram».
- **Подписки** в формате v2rayN, Happ-подобные подписки с полными конфигами Xray, ключи `vless://`,
  `hysteria2://` и другие. Ядра Xray и sing-box, режимы TUN (весь компьютер) и системного прокси.
- **Серверы из файлов:** WireGuard `.conf` (например, из ProtonVPN), полные JSON Xray / sing-box,
  текстовые файлы со ссылками. Меню «Добавить» или перетаскивание файла в окно.
- **Cloudflare WARP** (эксперимент): отдельная страница в меню, бесплатная регистрация в один клик,
  свой локальный прокси с UDP-шумом против DPI. Что идёт через WARP, решают правила `+warp`
  или готовый набор `@warp`; включается и из меню в трее.
- **Мастер первого запуска:** вставить подписку → Detour сам подбирает стратегию zapret.
- **Проверка блокировок** (страница «Правила»): Detour проверяет популярные сервисы напрямую, мимо VPN, и предлагает правило для каждого: напрямую, zapret или VPN.
- **«Не открывается сайт?»**: вставьте адрес, Detour проверит его напрямую, через zapret и через VPN и одной кнопкой добавит подходящее правило.
- **zapret чинится сам**: если сайты из правил «zapret→VPN» надолго ушли на VPN, Detour в фоне подбирает новую стратегию, а если не помогло — ставит свежий Flowseal.
- **Счётчик трафика мимо VPN**: на главной видно, сколько сегодня прошло через zapret и напрямую, не тратя VPN-подписку.
- **Списки доменов обновляются сами** раз в неделю из [itdoginfo/allow-domains](https://github.com/itdoginfo/allow-domains); готовый набор `@blocked` — около тысячи заблокированных в РФ сайтов.
- **Авто-смена сервера**, если текущий перестал пропускать трафик, а интернет есть: сначала серверы той же страны.
- **AmneziaWG**: `.conf` с параметрами Jc/S1/H1… подключается через ядро [sing-box-extended](https://github.com/shtorm-7/sing-box-extended) (GPL-3.0), которое скачивается только при первом подключении к такому серверу и проверяется по SHA-256.
- **Свой сервер для сервиса**: `@youtube +vpn = Германия 1` или `= авто` — YouTube, нейросети и любое правило через свой сервер.
- **Игровой режим**: пока запущена игра, сервер не меняется на ходу (новый IP посреди матча рвёт игру и может привести к бану).
- **Переподключение после сна и смены сети**: проснулся ноутбук или сменился Wi‑Fi — Detour дождётся интернета и переподключится сам.
- **Kill switch**, защита локального порта прокси, проверка «видят ли сайты мой IP», журнал соединений.
- **Диагностика** в разделе «Справка»: отчёт без адресов серверов и IP, который можно приложить к issue.
- **Обновление** из GitHub Releases прямо в программе; серверы и настройки сохраняются.
  Архив релиза уже содержит ядра, гео-базы и zapret: первый запуск не зависит от доступа к GitHub.

## Установка

1. Скачайте архив из [Releases](../../releases) и распакуйте в любую папку (не запускайте из архива).
2. Запустите `Detour.exe`. Установка не нужна. Для режима TUN и zapret программа попросит права администратора.
3. Скопируйте ссылку на подписку вашего VPN (или ключ) и нажмите `Ctrl+V` в окне программы.
4. Выберите сервер двойным щелчком и нажмите **Подключить**.

Требования: Windows 10 или 11, x64.

**macOS и Linux (бета, с 0.2.0):** архив `Detour-…-osx-arm64.tar.gz` (Mac на Apple Silicon), `…-osx-x64` (Intel)
или `…-linux-x64.tar.gz`. Распакуйте в домашнюю папку и запустите `./Detour`. zapret там работает через `tpws`
(локальный прокси от автора zapret, без прав администратора, только TCP). Пока нет kill switch и TG WS Proxy.
Подробности в `README.txt` внутри архива.

> [!NOTE]
> Защитник Windows может ругаться на файлы в `bin\zapret`. Это WinDivert — драйвер перехвата пакетов,
> на котором работает zapret, а не вирус. Исходники zapret и WinDivert открыты.

> [!WARNING]
> Не запускайте Detour одновременно с другим VPN-клиентом (Throne, Happ, Hiddify, v2rayN…):
> два туннеля мешают друг другу, и перестаёт работать всё. Detour предупредит, если заметит такой клиент.

## Если что-то не работает

- Внутри программы есть страница **Справка** с ответами на частые вопросы.
- Журнал программы и вывод ядер сохраняются в `guiLogs` рядом с `Detour.exe`.
- `tools\diag-live.ps1` снимает диагностику подключения (скорость и сайты разными путями, DNS, порты)
  в текстовый отчёт. Адреса серверов в отчёте скрыты, его можно приложить к issue.

## Сборка из исходников

Нужен [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet test --project v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj
dotnet publish v2rayN\v2rayN\v2rayN.csproj -c Release -r win-x64 --self-contained true -o publish\Detour
```

Ядра (Xray, sing-box), гео-базы и zapret программа скачивает сама при первом запуске.
Проект и пространства имён по-прежнему называются `v2rayN`, чтобы было проще забирать изменения из оригинала.

## Лицензии и благодарности

Detour распространяется под **GPL-3.0** (см. [LICENSE](LICENSE)), как и v2rayN, на котором он основан.

| Компонент | Автор | Лицензия |
|---|---|---|
| [v2rayN](https://github.com/2dust/v2rayN) — основа клиента | 2dust и участники | GPL-3.0 |
| [Xray-core](https://github.com/XTLS/Xray-core) | XTLS | MPL-2.0 |
| [sing-box](https://github.com/SagerNet/sing-box) | SagerNet | GPL-3.0 |
| [zapret](https://github.com/bol-van/zapret), [сборка Flowseal](https://github.com/Flowseal/zapret-discord-youtube) | bol-van, Flowseal | MIT |
| [WinDivert](https://github.com/basil00/WinDivert) | basil00 | LGPL-3.0 / GPL-2.0 |
| [TG WS Proxy](https://github.com/Flowseal/tg-ws-proxy) | Flowseal | MIT |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co | MIT |
| [Twemoji](https://github.com/twitter/twemoji) — флаги стран | Twitter | CC-BY 4.0 |

zapret, TG WS Proxy и ядра не входят в исходники: программа скачивает их с официальных страниц релизов.

---

## English

Detour is a Windows fork of [v2rayN](https://github.com/2dust/v2rayN) aimed at users in Russia. Per app, site,
IP or ready-made preset you choose how traffic goes: VPN, zapret (DPI bypass without a server), zapret with
automatic fallback to VPN, direct, or block. zapret (Flowseal build) and TG WS Proxy are downloaded and run
by the client; Xray and sing-box are the cores. Download from Releases, unzip, run `Detour.exe`, paste your
subscription with `Ctrl+V`. Licensed under GPL-3.0; see the table above for third-party components.
