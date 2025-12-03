# PowerShell script for building io project
# Run: powershell -ExecutionPolicy Bypass -File build.ps1

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building io project" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

function Find-MSBuild {
    Write-Host "Searching for MSBuild..." -ForegroundColor Yellow
    $paths = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe"
    )
    
    foreach ($path in $paths) {
        Write-Host "  Checking: $path" -ForegroundColor Gray
        if (Test-Path $path) {
            Write-Host "  Found!" -ForegroundColor Green
            return $path
        }
    }
    
    Write-Host "  Trying vswhere..." -ForegroundColor Gray
    $vswhere = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($vswhere) {
        $msbuild = & vswhere.exe -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
        if ($msbuild -and (Test-Path $msbuild)) {
            Write-Host "  Found via vswhere!" -ForegroundColor Green
            return $msbuild
        }
    }
    
    return $null
}

$msbuildPath = Find-MSBuild

if (-not $msbuildPath) {
    Write-Host ""
    Write-Host "ERROR: MSBuild not found!" -ForegroundColor Red
    Write-Host "Make sure Visual Studio or Build Tools are installed." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

Write-Host ""
Write-Host "Found MSBuild: $msbuildPath" -ForegroundColor Green
Write-Host ""

if (-not (Test-Path "io")) {
    Write-Host "ERROR: Folder 'io' not found!" -ForegroundColor Red
    Write-Host "Make sure script is run from project root folder." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

if (-not (Test-Path "io\io.csproj")) {
    Write-Host "ERROR: File 'io\io.csproj' not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

Write-Host "Found project file: io\io.csproj" -ForegroundColor Green
Write-Host ""
Write-Host "Starting build..." -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$originalLocation = Get-Location
Set-Location "io"

try {
    Write-Host "Command: $msbuildPath io.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Rebuild /v:normal" -ForegroundColor Gray
    Write-Host ""
    
    # Захватываем весь вывод в переменную
    $buildOutput = & $msbuildPath io.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Rebuild /v:normal 2>&1 | Out-String
    
    # Выводим вывод на экран
    Write-Host $buildOutput
    
    $exitCode = $LASTEXITCODE
    
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    
    if ($exitCode -eq 0) {
        Write-Host "Build completed successfully!" -ForegroundColor Green
        Write-Host ""
        
        $exePath = Join-Path (Get-Location) "bin\Debug\io.exe"
        if (Test-Path $exePath) {
            $exeInfo = Get-Item $exePath
            Write-Host "Executable: $exePath" -ForegroundColor Green
            Write-Host "Size: $([math]::Round($exeInfo.Length / 1KB, 2)) KB" -ForegroundColor Green
            Write-Host "Last modified: $($exeInfo.LastWriteTime)" -ForegroundColor Green
        } else {
            Write-Host "WARNING: io.exe not found in bin\Debug\" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Build FAILED! Exit code: $exitCode" -ForegroundColor Red
        Write-Host ""
        Write-Host "Check error messages above." -ForegroundColor Yellow
        
        # Копируем ошибки в буфер обмена
        try {
            $errorText = "Build FAILED! Exit code: $exitCode`n`n$buildOutput"
            Set-Clipboard -Value $errorText
            Write-Host ""
            Write-Host "ERROR OUTPUT COPIED TO CLIPBOARD!" -ForegroundColor Cyan
            Write-Host "You can now paste it (Ctrl+V) to share the error." -ForegroundColor Cyan
        } catch {
            Write-Host ""
            Write-Host "Could not copy to clipboard: $_" -ForegroundColor Yellow
        }
    }
    
    Write-Host "========================================" -ForegroundColor Cyan
} catch {
    Write-Host ""
    Write-Host "CRITICAL ERROR: $_" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    
    # Копируем критическую ошибку в буфер обмена
    try {
        $errorText = "CRITICAL ERROR: $_`n`n$($_.Exception.Message)`n`n$($_.Exception.StackTrace)"
        Set-Clipboard -Value $errorText
        Write-Host ""
        Write-Host "ERROR OUTPUT COPIED TO CLIPBOARD!" -ForegroundColor Cyan
    } catch {
        Write-Host ""
        Write-Host "Could not copy to clipboard: $_" -ForegroundColor Yellow
    }
} finally {
    Set-Location $originalLocation
}

Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
