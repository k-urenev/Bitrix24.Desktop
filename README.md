# Bitrix24.Desktop

Настольный клиент чата Битрикс24 для одного портала. Это WPF-приложение на .NET 8: список диалогов, переписка, вложения, уведомления и опрос задач, где текущий пользователь ответственный. Портал, код приложения и секрет вводит пользователь.

Запускать нужно опубликованный файл `Bitrix24.Desktop.exe` из `%USERPROFILE%\Desktop\Bitrix24`. Сборка в `bin\Debug` от этого файла не подменяет его: это отдельный framework-dependent exe.

## Проекты

| Проект | Роль |
| --- | --- |
| `src/Bitrix24.Desktop` | Окно, трей, вью-модели, контролы |
| `src/Bitrix24.Desktop.Core` | REST, OAuth, SQLite, опрос портала |
| `tests/Bitrix24.Desktop.Core.Tests` | Тесты ядра |

## Сборка

Из корня репозитория:

```powershell
dotnet test Bitrix24.Desktop.sln -c Release
```

Публикация в папку, из которой приложение открывают. Процесс нужно остановить: каталог совпадает с запущенным exe.

```powershell
$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:Path
Stop-Process -Name Bitrix24.Desktop -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
dotnet publish src\Bitrix24.Desktop\Bitrix24.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o "$env:USERPROFILE\Desktop\Bitrix24"
Remove-Item "$env:USERPROFILE\Desktop\Bitrix24\*.xml" -ErrorAction SilentlyContinue
```

Среда .NET и WPF лежит рядом с exe отдельными файлами, exe их не пакует. Картинки смайлов — в `Assets\emoji`.

Второй экземпляр не стартует: мьютекс `Local\Bitrix24.Desktop.SingleInstance`. Повторный запуск показывает уже открытое окно.

## Выкладка

Публичный репозиторий не содержит адрес портала, код, секрет, логин и пароль. Готовый установщик — `release\Bitrix24-Setup-1.0.4.exe`, он же прикреплён к выпуску GitHub с номером версии. В свойствах файла и на странице «О программе» подпись «Неофициальная сборка Битрикс24 Мессенджера».

## Документация

Поведение и устройство описаны в каталоге `docs`. При изменении поведения обновляется соответствующий файл, а не только код.

- [Техническое задание](docs/tz.md) — оглавление; части в `docs/tz/`, снимки окна в `docs/mockup`
- [Устройство](docs/architecture.md)
- [Чат](docs/chat.md)
- [Данные и вход](docs/data.md)
