# Инструкция по сборке проекта

## Быстрая сборка

### Вариант 1: Использование bat-файла (Windows)
Просто запустите в терминале:
```bash
build.bat
```

### Вариант 2: Использование PowerShell скрипта
```powershell
.\build.ps1
```

### Вариант 3: Прямая команда MSBuild
```powershell
cd io
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" io.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build
```

## Результат сборки

После успешной сборки исполняемый файл будет находиться в:
```
io\bin\Debug\io.exe
```

## Примечания

- Скрипты автоматически найдут MSBuild в стандартных местах установки Visual Studio
- Если MSBuild не найден, убедитесь, что установлен Visual Studio или Build Tools
- Для Release сборки измените `/p:Configuration=Debug` на `/p:Configuration=Release`

