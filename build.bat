@echo off
REM Скрипт для сборки проекта io

REM Поиск MSBuild
set MSBUILD_PATH=

REM Проверяем стандартные пути Visual Studio 2022 (версия 18)
if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
    goto :found
)

if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe
    goto :found
)

if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe
    goto :found
)

if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe
    goto :found
)

REM Проверяем Visual Studio 2019
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe
    goto :found
)

if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe
    goto :found
)

REM Проверяем старые версии
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe" (
    set MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe
    goto :found
)

REM Если не нашли, пробуем через vswhere
where vswhere.exe >nul 2>&1
if %ERRORLEVEL% == 0 (
    for /f "usebackq tokens=*" %%i in (`vswhere.exe -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        set MSBUILD_PATH=%%i
        goto :found
    )
)

echo ОШИБКА: MSBuild не найден!
echo Убедитесь, что установлен Visual Studio или Build Tools.
pause
exit /b 1

:found
echo Найден MSBuild: %MSBUILD_PATH%
echo.
echo Сборка проекта...
echo.

REM Переходим в папку с проектом
cd /d "%~dp0io"

REM Собираем проект в Debug конфигурации
"%MSBUILD_PATH%" io.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Rebuild /v:minimal

if %ERRORLEVEL% == 0 (
    echo.
    echo ========================================
    echo Сборка успешно завершена!
    echo Исполняемый файл: io\bin\Debug\io.exe
    echo ========================================
) else (
    echo.
    echo ========================================
    echo ОШИБКА при сборке!
    echo ========================================
    pause
    exit /b 1
)

cd /d "%~dp0"
pause

