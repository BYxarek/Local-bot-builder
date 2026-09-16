# Разработка и тестирование

## Требования

- Windows 10 или новее;
- PowerShell 7;
- .NET SDK 10.

Проверка окружения:

```powershell
pwsh --version
dotnet --version
```

## Восстановление и сборка

Из корня репозитория выполните:

```powershell
dotnet restore .\NativeBot.slnx
dotnet build .\NativeBot.slnx
```

## Запуск приложения

```powershell
dotnet run --project .\src\NativeBot.App\NativeBot.App.csproj
```

Приложение хранит локальные данные прототипа в `%LOCALAPPDATA%\NativeBot\nativebot.db`. Не публикуйте эту базу: она может содержать токены и другие секреты.

## Запуск проверок

```powershell
dotnet run --project .\tests\NativeBot.Tests\NativeBot.Tests.csproj
```

Проверки покрывают переменные, выполнение и валидацию сценариев, SQLite, Execution и восстановление, дедупликацию обновлений, безопасный экспорт/импорт, запросы long polling, разбор Telegram API, каталог Bot API 10.3, автоматические `allowed_updates`, специальные Telegram-контексты и запуск отдельного процесса `NativeBot.Agent`.

Для проверки релизной сборки:

```powershell
dotnet build .\NativeBot.slnx -c Release
```
