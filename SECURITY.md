# Безопасность / Security

## Как сообщить об уязвимости

Не открывайте публичный issue. Используйте приватный отчёт GitHub:
вкладка **Security → Report a vulnerability** в этом репозитории
([Private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)).

Опишите версию Detour, что именно можно сделать и как это повторить. Если в отчёте есть конфиги или
журналы, уберите из них ссылки подписок, ключи и адреса серверов.

Ответ — в течение недели. Исправление выходит отдельным релизом. Если хотите, вас упомянут в описании.

## Что считается уязвимостью

- выполнение кода или повышение прав через Detour (он работает с правами администратора);
- подмена обновления, ядер или zapret, которую Detour не заметил;
- утечка ссылок подписок, ключей серверов или реального IP мимо включённого VPN / kill switch;
- управление Detour извне: из браузера, другой программы на ПК или из локальной сети без вашего разрешения;
- падения и зависания на чужих данных из сети (подписки, ссылки, списки).

Блокировка конкретного протокола или сервера провайдером — не уязвимость Detour, об этом пишите в обычные issues.

## Как устроены релизы

- Архивы собирает GitHub Actions из открытых исходников (`.github/workflows/build.yml`). Рядом с архивами
  лежит `SHA256SUMS.txt`.
- Версии Xray, sing-box и zapret закреплены в `tools/cores.lock.json` вместе с SHA-256. Сборка падает,
  если файл ядра на GitHub изменился.
- Встроенное обновление ставит архив, только если его подпись (`.sig`) проверяется ключом, вшитым в Detour.
  Закрытый ключ хранится у сопровождающего вне GitHub, поэтому взлом аккаунта или подмена файла в релизе
  не даёт поставить пользователям чужой код.

---

## Reporting a vulnerability

Please don't open a public issue. Use GitHub's private reporting: **Security → Report a vulnerability**.
Include the Detour version, what an attacker can do and how to reproduce it; strip subscription links,
keys and server addresses from configs and logs. Expect a reply within a week.

In scope: code execution or privilege escalation through Detour (it runs as administrator); tampered
updates, cores or zapret that Detour accepts; leaks of subscription links, server keys or the real IP past
an active VPN / kill switch; controlling Detour from a browser, another local program or the LAN without
consent; crashes or hangs on untrusted network input. Releases are built by GitHub Actions with pinned,
hash-checked cores and `SHA256SUMS.txt`; the built-in updater installs only archives signed with an offline key.
