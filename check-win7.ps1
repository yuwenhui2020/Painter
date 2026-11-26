#requires -version 2.0
# Painter 启动自检（兼容 Windows 7 SP1 自带 PowerShell 2.0）
# 检查：.NET Framework 4.6.2+、关键运行文件、32 位 PE 头、SkiaSharp 2.88.x
# 退出码 0 = 全部通过；1 = 存在问题，禁止启动

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$dir = Join-Path $root 'bin\x86\Release'
$exe = Join-Path $dir 'painter.exe'
$failCount = 0

function Write-Ok { param([string]$text) Write-Host ('[通过] ' + $text) }
function Write-Bad { param([string]$text) Write-Host ('[失败] ' + $text) -ForegroundColor Red; $script:failCount++ }

Write-Host '=== Painter 启动自检 ==='
Write-Host ('程序目录: ' + $dir)

# 1. .NET Framework 4.6.2+ (Release >= 394802)
$net = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if ($net -and $net.Release -ge 394802) {
    Write-Ok ('.NET Framework 4.6.2+ 已安装 (Release=' + $net.Release + ')')
} else {
    Write-Bad '未检测到 .NET Framework 4.6.2+，请先安装 .NET Framework 4.8（Win7 需先装 SP1）。'
}

# 2. 关键文件
$required = @(
    'painter.exe', 'IACore.dll', 'IALoader.dll', 'IAWinFX.dll',
    'Newtonsoft.Json.dll', 'SkiaSharp.dll', 'libSkiaSharp.dll',
    'iNKORE.UI.WPF.Modern.dll', 'iNKORE.UI.WPF.Modern.Controls.dll', 'frame.bin'
)
foreach ($name in $required) {
    if (Test-Path -LiteralPath (Join-Path $dir $name)) {
        Write-Ok ('存在 ' + $name)
    } else {
        Write-Bad ('缺少 ' + $name + '，请确认整个 bin\x86\Release 目录完整。')
    }
}

# 3. 读取 PE 头 Machine 字段，必须为 0x014C (x86)
function Get-PeMachine {
    param([string]$path)
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0
        if ($reader.ReadUInt16() -ne 0x5A4D) { return 0 }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -le 0 -or $offset -ge $stream.Length) { return 0 }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { return 0 }
        return $reader.ReadUInt16()
    } finally {
        $stream.Close()
    }
}

if (Test-Path -LiteralPath $exe) {
    $machine = Get-PeMachine $exe
    if ($machine -eq 0x014C) {
        Write-Ok 'painter.exe 为 32 位 (x86) 程序。'
    } else {
        Write-Bad ('painter.exe 不是 32 位程序 (Machine=0x' + $machine.ToString('X4') + ')，墨迹识别依赖 x86 原生 DLL。')
    }
    $native = Join-Path $dir 'libSkiaSharp.dll'
    if (Test-Path -LiteralPath $native) {
        $nm = Get-PeMachine $native
        if ($nm -eq 0x014C) {
            Write-Ok 'libSkiaSharp.dll 为 32 位版本。'
        } else {
            Write-Bad ('libSkiaSharp.dll 不是 32 位版本 (Machine=0x' + $nm.ToString('X4') + ')。')
        }
    }
}

# 4. SkiaSharp 必须为 2.88.x（3.x 原生库依赖 Win10 独有 d3d12.dll）
$skiaPath = Join-Path $dir 'SkiaSharp.dll'
if (Test-Path -LiteralPath $skiaPath) {
    try {
        $ver = [System.Reflection.AssemblyName]::GetAssemblyName($skiaPath).Version
        if ($ver.Major -eq 2 -and $ver.Minor -le 88) {
            Write-Ok ('SkiaSharp ' + $ver.ToString() + ' 兼容 Windows 7。')
        } else {
            Write-Bad ('SkiaSharp ' + $ver.ToString() + ' 依赖 D3D12，请换回 2.88.8。')
        }
    } catch {
        Write-Bad ('无法读取 SkiaSharp 版本: ' + $_.Exception.Message)
    }
}

Write-Host '=== 自检结束 ==='
if ($failCount -eq 0) {
    Write-Host '全部通过，可以启动 Painter。' -ForegroundColor Green
    exit 0
}
Write-Host ('发现 ' + $failCount + ' 项问题，请处理后重试。') -ForegroundColor Red
exit 1
