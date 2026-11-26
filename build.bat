@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ===== 定位 MSBuild（不依赖 PATH，直接用 VS 安装器里的 vswhere）=====
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%VSWHERE%" (
    echo 未找到 vswhere.exe，请先安装 Visual Studio（.NET 桌面开发工作负载）。
    pause
    exit /b 1
)

set "MSBUILD="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
    if not defined MSBUILD set "MSBUILD=%%i"
)

if not defined MSBUILD (
    echo 未找到 MSBuild，请确认已安装 Visual Studio 的 MSBuild 组件。
    pause
    exit /b 1
)

echo 使用 MSBuild: %MSBUILD%
echo.

REM ===== 全量重新编译（清掉旧的再完整构建），输出到 Release 配置 / x86 =====
"%MSBUILD%" "Painter.csproj" /restore /t:Rebuild /p:Configuration=Release /p:Platform=x86 /nologo /v:minimal
if errorlevel 1 (
    echo.
    echo 编译失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo.
echo 编译成功！程序位于：bin\x86\Release\painter.exe
pause
