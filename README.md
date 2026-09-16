# Native Bot Create

Native Bot Create — нативное Windows-приложение для создания и управления Telegram-ботами через визуальные сценарии без ручного написания кода.

Проект находится в разработке. Текущий прототип содержит WPF-интерфейс, базовый движок сценариев, SQLite-хранилище, интеграцию с Telegram Bot API 10.3 и отдельный для каждого бота выбор между webhook и long polling.

## Целевая архитектура

Для постоянной работы каждый пользователь предоставляет собственный сервер и домен с HTTPS:

```text
Telegram
  → https://bots.example.com/webhook/{botId}
  → NativeBot.Agent на сервере пользователя
  → очередь обновлений, сценарии и хранилище

NativeBot.App на Windows
  → защищённый API NativeBot.Agent
```

Для разработки, internal-ботов и небольшой нагрузки можно выбрать long polling: домен и SSL в этом режиме не нужны. Режим выбирается при добавлении бота и меняется в его настройках.

Cloudflare, публичный туннель и открытый порт на компьютере пользователя не являются обязательными частями целевой архитектуры. Серверный режим и удалённое управление ещё предстоит завершить; сейчас `NativeBot.Agent` запускается локально вместе с приложением.

## Состав решения

- `NativeBot.App` — WPF-интерфейс конструктора и управления ботами.
- `NativeBot.Agent` — runtime ботов, webhook endpoint, long polling и выполнение сценариев.
- `NativeBot.Core` — модели, переменные, валидация и движок сценариев.
- `NativeBot.Telegram` — клиент и модели Telegram Bot API.
- `NativeBot.Storage` — SQLite-хранилище и защита секретов.
- `NativeBot.Tests` — исполняемые интеграционные проверки без отдельного тестового фреймворка.

## Требования для разработки

- Windows 10 или новее;
- PowerShell 7;
- .NET SDK 10;
- Telegram-бот, созданный через BotFather, для ручной проверки интеграции.

## Быстрый старт

```powershell
dotnet restore .\NativeBot.slnx
dotnet build .\NativeBot.slnx
dotnet run --project .\src\NativeBot.App\NativeBot.App.csproj
```

Запуск автоматических проверок:

```powershell
dotnet run --project .\tests\NativeBot.Tests\NativeBot.Tests.csproj
```

## Документация

- [Индекс документации](docs/README.md)
- [Разработка и тестирование](docs/development.md)
- [Подготовка пользовательского сервера](docs/server-setup.md)
- [Настройка Telegram webhook](docs/webhook-setup.md)
- [Выбор webhook или long polling](docs/update-modes.md)
- [Подключение Telegram-бота](docs/connect-bot.md)
- [Возможности Telegram Adapter](docs/telegram-features.md)
- [План разработки](plan-dev.md)

## Безопасность

Не добавляйте в репозиторий токены Telegram, SSH-ключи, пароли и резервные копии рабочей базы. Входящие webhook-запросы должны проверяться по `X-Telegram-Bot-Api-Secret-Token`, а публичный endpoint должен быть доступен только по HTTPS. Экспорт `.nativebot` по умолчанию не содержит токенов и Secret variables.
