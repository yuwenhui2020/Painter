@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0bin\x86\Release\painter.exe"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

echo === Painter Win7-compatible launcher ===

if exist "%PS%" (
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-win7.ps1"
    if errorlevel 1 (
        echo.
        echo Startup checks failed. Fix the problems above before running Painter.
        pause
        exit /b 1
    )
) else (
    if not exist "%EXE%" (
        echo [FAIL] painter.exe not found: %EXE%
        pause
        exit /b 1
    )
    echo [WARN] PowerShell unavailable; only basic file check was performed.
)

if not exist "%EXE%" (
    echo [FAIL] painter.exe not found: %EXE%
    pause
    exit /b 1
)

echo Starting Painter...
start "" "%EXE%"
endlocal
