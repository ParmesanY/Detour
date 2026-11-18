# Как помочь проекту / Contributing

## Сообщить о проблеме

Откройте issue и приложите **Архив для помощи** (окно «Диагностика»): адреса серверов и ключи
в нём скрыты. Напишите провайдера (город, домашний или мобильный интернет) и что именно не работает.
Уязвимости — только приватно, см. [SECURITY.md](SECURITY.md).

## Изменения в коде

- Нужен [.NET 10 SDK](https://dotnet.microsoft.com/download). Перед pull request прогоните тесты:
  `dotnet test --project v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj`.
- Логика — в `v2rayN/ServiceLib`, интерфейс Windows — в `v2rayN/v2rayN`. Пространства имён остаются
  `v2rayN`, чтобы забирать изменения из оригинального v2rayN.
- Строки интерфейса Detour — в `ServiceLib/Resx/AppText.cs` (русский и английский).
- Новые стратегии zapret и маскировки VPN — в `strategies/*.json`: эти списки обновляются у пользователей
  без нового релиза.
- Код, который запускает процессы (PowerShell, schtasks, netsh, winws), передаёт аргументы списком,
  а не одной строкой. Данные из сети (подписки, ссылки, списки) считаются чужими: никаких путей
  и команд из них без проверки.
- Новые версии ядер — только через `tools/update-cores-lock.ps1` с проверкой изменений в `tools/cores.lock.json`.

Отправляя изменения, вы соглашаетесь распространять их под GPL-3.0, как и весь проект.

---

Bug reports: attach the **Help archive** from the Diagnostics window (server addresses and keys are
masked) and name your ISP. Security issues go through [SECURITY.md](SECURITY.md). Pull requests: run the
tests, keep namespaces `v2rayN`, put UI strings in `AppText.cs`, pass process arguments as a list, treat
network input as untrusted, and bump cores only via `tools/update-cores-lock.ps1`. Contributions are GPL-3.0.
