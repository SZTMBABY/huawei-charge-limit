# 编译脚本：只用 Windows 自带的 .NET Framework 4.8 编译器，无需安装任何 SDK
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'Program.cs'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) '充电限制工具.exe'

$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $fw)) { $fw = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw '找不到系统自带的 csc.exe（需要 .NET Framework 4.x）' }

$refNames = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Management.dll', 'System.Drawing.dll',
    'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'System.Xaml.dll'
)

function Resolve-Ref([string]$name) {
    $base = [IO.Path]::GetFileNameWithoutExtension($name)
    $candidates = @(
        (Join-Path $fw $name),
        (Join-Path $fw ('WPF\' + $name)),
        (Join-Path $env:WINDIR ('Microsoft.NET\assembly\GAC_MSIL\' + $base + '\v4.0_4.0.0.0__31bf3856ad364e35\' + $name)),
        (Join-Path $env:WINDIR ('assembly\GAC_MSIL\' + $base + '\v4.0_4.0.0.0__b77a5c561934e089\' + $name))
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

$refArgs = @()
foreach ($n in $refNames) {
    $p = Resolve-Ref $n
    if (-not $p) { throw "找不到引用程序集: $n" }
    $refArgs += "/r:`"$p`""
}

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    '/nowarn:1591,0618',
    "/out:`"$out`""
)
$icon = Join-Path $PSScriptRoot 'icon.ico'
if (Test-Path $icon) { $cscArgs += "/win32icon:`"$icon`"" } else { Write-Host '警告：没有 icon.ico，将使用默认图标' }
$cscArgs = $cscArgs + $refArgs + @("`"$src`"")

Write-Host "csc: $csc"
Write-Host "out: $out"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
if (-not (Test-Path $out)) { throw '没有生成 exe' }
Write-Host ("编译成功：" + $out + "  (" + (Get-Item $out).Length + " 字节)")
