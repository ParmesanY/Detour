<p align="center">
  <img src="docs/readme/hero.svg" alt="Detour — каждому сервису свой путь: через zapret, через VPN или напрямую" width="100%">
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/ParmesanY/Detour?label=%D0%B2%D0%B5%D1%80%D1%81%D0%B8%D1%8F&color=4f8cff" alt="Последняя версия"></a>
  <img src="https://img.shields.io/badge/Windows%2010%20%2F%2011-x64-4f8cff" alt="Windows 10 / 11">
  <img src="https://img.shields.io/badge/macOS%20%C2%B7%20Linux-%D0%B1%D0%B5%D1%82%D0%B0-6e7783" alt="macOS и Linux — бета">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-6e7783" alt="GPL-3.0"></a>
</p>

<p align="center">
  <a href="../../releases/latest"><b>Скачать</b></a> ·
  <a href="#как-это-работает">Как это работает</a> ·
  <a href="#возможности">Возможности</a> ·
  <a href="#установка">Установка</a> ·
  <a href="#безопасность">Безопасность</a> ·
  <a href="#english">English</a>
</p>

---

Detour — VPN-клиент для пользователей в России, форк [v2rayN](https://github.com/2dust/v2rayN). Он решает одну задачу:
**каждый сервис идёт самым быстрым путём, который у вас сейчас работает**, и вам не нужно вручную включать и выключать VPN.
YouTube и Discord открываются через zapret без сервера и без пинга, банки и Госуслуги — напрямую,
нейросети — через VPN в нужной стране. Если путь сломался, Detour сам переключит сервис на рабочий.

<!-- SCREENSHOT: главное окно (docs/readme/screen-main.png) -->

## Как это работает

<p align="center"><img src="docs/readme/rules.svg" alt="Приложения и сайты проходят через правила: zapret, VPN-сервер, напрямую или блок" width="100%"></p>

Правила пишутся одной строкой и проверяются сверху вниз. Для приложения, сайта, IP или готового набора
выбирается режим: `+vpn`, `+zapret`, `+zapret+vpn`, `+direct` или `+block`.

```text
Discord.exe +zapret+vpn
@youtube +zapret
@openai +vpn = США; Финляндия
@russia +direct
```

Discord идёт через zapret, а если тот не проходит — через VPN; YouTube — через zapret; ChatGPT — через сервер
в США, а если он умер — через Финляндию; российские сайты — мимо VPN.

Готовые наборы есть для Discord, YouTube, Telegram, ChatGPT, Claude, Instagram, X, Spotify, Roblox, Steam,
Minecraft и других, с рекомендованным режимом. Можно писать по-русски: `+впн`, `+запрет`, `+напрямую`.

<!-- SCREENSHOT: страница «Правила» (docs/readme/screen-rules.png) -->

### zapret → VPN: сервис работает всегда, VPN — только когда нужен

<p align="center"><img src="docs/readme/zapret-vpn.svg" alt="Сервис стартует через VPN, переходит на zapret после двух удачных проверок и возвращается на VPN, если zapret перестал помогать" width="100%"></p>

zapret ([сборка Flowseal](https://github.com/Flowseal/zapret-discord-youtube)) встроен: скачивается одной кнопкой,
стратегия подбирается автоматически под вашего провайдера. Сервис в режиме `+zapret+vpn` стартует через VPN,
Detour в фоне проверяет прямой путь и переводит его на zapret. Когда провайдер меняет блокировку, сервис
возвращается на VPN, а Detour подбирает новую стратегию — сам, без вас.

## Возможности

| Возможность | Что даёт |
|---|---|
| **Встроенный zapret** | Автоподбор стратегии, самовосстановление, стратегия запоминается для каждой сети (дом, работа, телефон). |
| **Свой сервер для сервиса** | `@youtube +vpn = Германия 1; Финляндия` — правило ходит через свой сервер и переключается между запасными, не трогая остальной VPN. |
| **Автосмена сервера** | Ловит не только обрывы, но и «заморозку» провайдером, когда сервер отвечает, а данные встают после 16 КБ. |
| **Сторож соединений** | Звонок Discord или игра, у которых перестали приходить данные, переоткрываются сами. |
| **TG WS Proxy** | Telegram Desktop и сторонние клиенты без VPN ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)). |
| **Сайты чинятся сами** | Сайт без правила не открывается — Detour сам проверит его напрямую, через zapret и через VPN и добавит правило. Для любого сайта есть и кнопка «Не открывается сайт?». |
| **Любые подписки** | Формат v2rayN, полные конфиги Xray (Happ-подобные), ключи `vless://`, `hysteria2://`, WireGuard и AmneziaWG 2.0–3.1. |
| **Detour Map** | По согласию: общая [карта блокировок](https://github.com/ParmesanY/Detour-Map) и подсказки, какие стратегии и протоколы работают у вашего провайдера. |

<!-- SCREENSHOT: zapret и автоподбор (docs/readme/screen-zapret.png) -->

### Автосмена сервера

<p align="center"><img src="docs/readme/failover.svg" alt="Порядок автосмены: тот же сервер с другим транспортом, затем та же страна в другой сети хостинга, затем остальные по пингу" width="100%"></p>

<details>
<summary><b>Все возможности</b></summary>

**Правила и маршруты**
- Режимы `VPN`, `zapret`, `zapret → VPN`, `напрямую`, `блок` для приложений, сайтов, IP и готовых наборов.
- **Режим обучения:** клиент запоминает, куда ходит приложение, и добавляет эти адреса в правило.
- **Проверка блокировок** на странице «Правила»: популярные сервисы проверяются мимо VPN, для каждого предлагается правило.
- **Списки доменов обновляются сами** раз в неделю из [itdoginfo/allow-domains](https://github.com/itdoginfo/allow-domains); набор `@blocked` — около тысячи заблокированных в РФ сайтов.
- **Свой сервер для правила:** `= Германия 1`, `= авто` (лучший пинг) или список через `;` с автосменой по порядку.
  Сервер ищется по названию из списка серверов: сначала точное совпадение, потом первое, где есть этот текст.
  Подходят и серверы-конфиги (полный JSON Xray), если основной сервер такой же и включён TUN.
  На главной видно, через какой сервер сейчас идёт каждое такое правило.
- **Cloudflare WARP** (эксперимент): бесплатная регистрация в один клик, правила `+warp` или набор `@warp`.

**zapret**
- Встроенная сборка Flowseal, скачивается одной кнопкой; мастер первого запуска подбирает стратегию сам.
- **Чинится сам:** если сайты из правил «zapret → VPN» надолго ушли на VPN, Detour подбирает новую стратегию, а если не помогло — ставит свежий Flowseal.
- **Стратегия для каждой сети:** запоминается по имени Wi‑Fi или роутеру и возвращается сама.
- **Подсказки Detour Map:** сначала пробуются стратегии, которые сработали у других на вашем провайдере.
- **Счётчик трафика мимо VPN:** сколько сегодня прошло через zapret и напрямую, не тратя подписку.

**VPN и серверы**
- Ядра Xray и sing-box, режимы TUN (весь компьютер) и системного прокси.
- Подписки v2rayN, Happ-подобные с полными конфигами Xray, ссылки `happ://`, `v2raytun://`; трафик и срок подписки на главной.
- Серверы из файлов: WireGuard `.conf` (например, из ProtonVPN), полные JSON Xray / sing-box, текстовые файлы со ссылками.
- **AmneziaWG 2.0–3.1** через ядро [sing-box-extended](https://github.com/shtorm-7/sing-box-extended) (GPL-3.0), скачивается при первом подключении и проверяется по SHA-256.
- **Подбор маскировки** под сервер: дробление рукопожатия, zapret-обманки, mux/xmux, TLS-отпечаток браузера; показывает, на скольких КБ провайдер замораживает соединение.
- **Белые списки** мобильных операторов распознаются: автосмена пробует только серверы через российский узел.

**Надёжность**
- **Автосмена сервера** с порядком «другой транспорт → другая сеть хостинга → по пингу» и подсказками Detour Map о протоколах вашего провайдера.
- **Сторож соединений** (TUN): соединения без ответа 20 секунд пересоздаются.
- **Игровой режим:** пока запущена игра, сервер не меняется (новый IP посреди матча рвёт игру и может привести к бану).
- **Переподключение после сна и смены сети.**
- **Kill switch**, защита локального порта прокси, проверка «видят ли сайты мой IP», журнал соединений.

**Удобство**
- Мастер первого запуска: вставить подписку → Detour подбирает стратегию zapret сам.
- **Справка** внутри программы, экскурсия по режимам, **архив для помощи** без адресов серверов и IP.
- **Обновление** из GitHub Releases прямо в программе; архив релиза уже содержит ядра, гео-базы и zapret.

</details>

## Установка

1. Скачайте архив из [Releases](../../releases/latest) и распакуйте в любую папку (не запускайте из архива).
2. Запустите `Detour.exe`. Установка не нужна. Для режима TUN и zapret программа попросит права администратора.
3. Скопируйте ссылку на подписку вашего VPN (или ключ) и нажмите `Ctrl+V` в окне программы.
4. Выберите сервер двойным щелчком и нажмите **Подключить**.

Требования: Windows 10 или 11, x64.

<details>
<summary><b>macOS и Linux (бета)</b></summary>

Архив `Detour-…-osx-arm64.tar.gz` (Mac на Apple Silicon), `…-osx-x64` (Intel) или `…-linux-x64.tar.gz`.
Распакуйте в домашнюю папку и запустите `./Detour`. zapret там работает через `tpws` (локальный прокси от автора
zapret, без прав администратора, только TCP). Пока нет kill switch и TG WS Proxy. Подробности в `README.txt`
внутри архива. Сборки для macOS иногда не успевают на очереди GitHub: если в релизе нет архива для Mac, возьмите
его из предыдущего релиза.

</details>

> [!NOTE]
> Защитник Windows может ругаться на файлы в `bin\zapret`. Это WinDivert — драйвер перехвата пакетов,
> на котором работает zapret, а не вирус. Исходники zapret и WinDivert открыты. Если антивирус удалил файлы,
> Detour заметит это и предложит исключение и повторную загрузку.

> [!WARNING]
> Не запускайте Detour одновременно с другим VPN-клиентом (Throne, Happ, Hiddify, v2rayN…):
> два туннеля мешают друг другу, и перестаёт работать всё. Detour предупредит, если заметит такой клиент.

## Безопасность

- **Открытая сборка.** Архивы релизов собирает GitHub Actions из этого репозитория. Рядом лежит
  `SHA256SUMS.txt`, версии ядер и zapret закреплены вместе с их хешами (`tools/cores.lock.json`).
- **Подписанные обновления.** Встроенное обновление ставит архив, только если его подпись проверяется
  ключом, вшитым в программу. Закрытый ключ хранится вне GitHub.
- **Проверенные версии ядер.** Xray, sing-box, zapret и TG WS Proxy программа ставит только в версиях из
  подписанного тем же ключом списка (`strategies/trusted-assets.json`): подменённый файл не установится
  даже при взломе аккаунта его автора.
- **Автозапуск без тихого повышения прав.** Если в папку Detour могут писать программы без прав
  администратора, автозапуск идёт через окно UAC. Чтобы Detour запускался от администратора без вопроса,
  положите его в `C:\Program Files`.
- **Без телеметрии.** Detour ходит в сеть за обновлениями, списками и ядрами (GitHub) и, если не выключено,
  за подсказками Detour Map: это запрос без данных о вас, сервер видит только ваш IP, чтобы понять провайдера.
  Замеры блокировок и протоколов отправляются только после вашего согласия и без адресов серверов.
- **Локальный прокси под паролем.** В режиме TUN порт локального прокси защищён случайным паролем на
  каждый запуск: другие программы на ПК не могут через него узнать сервер.

Нашли уязвимость — сообщите приватно, см. [SECURITY.md](SECURITY.md).

## Если что-то не работает

- Внутри программы есть страница **Справка** с ответами на частые вопросы.
- Кнопка **«Архив для помощи»** в справке собирает журналы и состояние без ключей и адресов серверов — его можно приложить к issue.
- Журнал программы и вывод ядер сохраняются в `guiLogs` рядом с `Detour.exe`.
- `tools\diag-live.ps1` снимает диагностику подключения (скорость и сайты разными путями, DNS, порты) в текстовый отчёт без адресов серверов.

## Сборка из исходников

Нужен [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone --recurse-submodules https://github.com/ParmesanY/Detour.git
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
| [sing-box](https://github.com/SagerNet/sing-box), [sing-box-extended](https://github.com/shtorm-7/sing-box-extended) | SagerNet, shtorm-7 | GPL-3.0 |
| [zapret](https://github.com/bol-van/zapret), [сборка Flowseal](https://github.com/Flowseal/zapret-discord-youtube) | bol-van, Flowseal | MIT |
| [WinDivert](https://github.com/basil00/WinDivert) | basil00 | LGPL-3.0 / GPL-2.0 |
| [TG WS Proxy](https://github.com/Flowseal/tg-ws-proxy) | Flowseal | MIT |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co | MIT |
| [Twemoji](https://github.com/twitter/twemoji) — флаги стран | Twitter | CC-BY 4.0 |

zapret, TG WS Proxy и ядра не входят в исходники: программа скачивает их с официальных страниц релизов.

---

## English

Detour is a fork of [v2rayN](https://github.com/2dust/v2rayN) for users in Russia (Windows; macOS and Linux in beta).
Per app, site, IP or ready-made preset you choose how traffic goes: VPN, zapret (DPI bypass without a server),
zapret with automatic fallback to VPN, direct, or block — and a rule can have its own VPN servers with failover.
zapret (Flowseal build) and TG WS Proxy are downloaded and run by the client; Xray and sing-box are the cores.
Failover catches ISP "freezes" (data stalls after ~16 KB) and tries another transport of the same server first.
Download from Releases, unzip, run `Detour.exe`, paste your subscription with `Ctrl+V`.
Licensed under GPL-3.0; see the table above for third-party components.
