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
dotnet run --project .\tests\NativeBot.UiTests\NativeBot.UiTests.csproj
```

Проверки покрывают переменные, выполнение и валидацию сценариев, SQLite, Execution и восстановление, дедупликацию обновлений, безопасный экспорт/импорт, запросы long polling, разбор Telegram API, каталог Bot API 10.3, автоматические `allowed_updates`, специальные Telegram-контексты и запуск отдельного процесса `NativeBot.Agent`.

UI-проверка запускается в STA и рендерит 12 состояний интерфейса Material Design 3: главные экраны, добавление бота, все вкладки управления ботом и карточки пользователя в светлой и тёмной темах. Дополнительно проверяется, что светлый фон страницы не сливается с карточками, а тёмная тема не содержит цветных акцентов. Контрольные снимки сохраняются в `bin\ui-tests\`.

При изменении XAML просмотрите созданные PNG и вручную проверьте актуальную сборку как нативное Windows-приложение: изменение размера окна, переключение вкладок, светлой и тёмной тем, а также диалог добавления бота.

Все вторичные WPF-окна должны иметь владельца, `WindowStartupLocation="CenterOwner"` и `ShowInTaskbar="False"`. Системные сообщения и файловые диалоги также открываются с владельцем текущего окна.

Для проверки релизной сборки:

```powershell
dotnet build .\NativeBot.slnx -c Release
```

## Сборка MSI

Сначала опубликуйте self-contained приложения для Windows x64:

```powershell
dotnet publish .\src\NativeBot.Agent\NativeBot.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\bin\NativeBot-win-x64
dotnet publish .\src\NativeBot.App\NativeBot.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\bin\NativeBot-win-x64
dotnet build .\installer\NativeBot.Installer.wixproj -c Release
```

Готовый установщик находится в `installer\bin\x64\Release\NativeBotSetup-0.1.0-beta.1.msi`. Установка выполняется для текущего пользователя и не требует отдельно установленного .NET. Вместе с приложением устанавливается `Uninstall.cmd`; при выборе ярлыков в меню «Пуск» добавляется команда удаления.
