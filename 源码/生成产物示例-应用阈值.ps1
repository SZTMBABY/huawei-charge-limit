$ErrorActionPreference = 'Stop'
$LogPath = "$env:LOCALAPPDATA\HuaweiChargeLimit\操作记录.log"
$FailPath = "$env:LOCALAPPDATA\HuaweiChargeLimit\最近失败.txt"
$CfgPath = "$env:LOCALAPPDATA\HuaweiChargeLimit\配置.ini"
trap {
    $m = $_.Exception.Message
    try { [IO.File]::AppendAllText($LogPath, '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] FAIL 脚本异常: ' + $m + [Environment]::NewLine, (New-Object Text.UTF8Encoding $false)) } catch { }
    try { [IO.File]::WriteAllText($FailPath, '时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + [Environment]::NewLine + '脚本异常: ' + $m + [Environment]::NewLine + '位置: ' + $_.InvocationInfo.PositionMessage, (New-Object Text.UTF8Encoding $false)) } catch { }
    exit 1
}
$startup = $args -contains '-Startup'
function Write-Log([string]$status, [int]$u, [int]$l, [string]$mode) {
    $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $status + ' 上限=' + $u + ' 下限=' + $l + ' 接口=' + $mode
    try { [IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding $false)) } catch { }
}
function Save-Failure([string]$detail) {
    try { [IO.File]::WriteAllText($FailPath, '时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + [Environment]::NewLine + '详情: ' + $detail, (New-Object Text.UTF8Encoding $false)) } catch { }
}
$upper = 90; $lower = 80; $auto = 1; $warn = New-Object System.Collections.ArrayList
if (Test-Path -LiteralPath $CfgPath) {
    try {
        foreach ($raw in [IO.File]::ReadAllLines($CfgPath, [Text.Encoding]::UTF8)) {
            $t = $raw.Trim()
            if ($t -eq '' -or $t.StartsWith('#')) { continue }
            $i = $t.IndexOf('=')
            if ($i -le 0) { continue }
            $k = $t.Substring(0, $i).Trim().ToLower()
            $v = $t.Substring($i + 1).Trim()
            $n = 0
            if ($k -eq 'upper') {
                if ([int]::TryParse($v, [ref]$n)) { $upper = $n } else { [void]$warn.Add('upper=' + $v) }
            } elseif ($k -eq 'lower') {
                if ([int]::TryParse($v, [ref]$n)) { $lower = $n } else { [void]$warn.Add('lower=' + $v) }
            } elseif ($k -eq 'auto') {
                if ($v -eq '1' -or $v -eq 'true' -or $v -eq 'True') { $auto = 1 } else { $auto = 0 }
            }
        }
    } catch { [void]$warn.Add('读取配置失败: ' + $_.Exception.Message) }
} else { [void]$warn.Add('配置文件不存在，使用默认 90/80') }
if ($upper -lt 50) { $upper = 50; [void]$warn.Add('上限越界，改为 50') }
if ($upper -gt 100) { $upper = 100; [void]$warn.Add('上限越界，改为 100') }
if ($lower -lt 0) { $lower = 0; [void]$warn.Add('下限越界，改为 0') }
if ($lower -gt ($upper - 5)) { $lower = [Math]::Max(0, $upper - 5); [void]$warn.Add('下限过大，已调整') }
if ($startup -and $auto -ne 1) { exit 0 }
if ($warn.Count -gt 0 -and -not $startup) { Write-Log 'WARN' $upper $lower ('配置问题: ' + ($warn -join '; ')) }
function Invoke-Threshold([byte[]]$data) {
    $all = @(Get-WmiObject -Namespace 'root\wmi' -Class OemWMIMethod)
    $o = $null
    foreach ($x in $all) { if ($x.InstanceName -eq 'ACPI\PNP0C14\HWMI_0') { $o = $x; break } }
    if ($o -eq $null) { throw '未找到 OemWMIMethod 实例（ACPI\PNP0C14\HWMI_0）' }
    $r = $o.OemWMIfun($data)
    return ,$r.u8Output
}
$old = New-Object byte[] 64
$old[0] = 3; $old[1] = 16; $old[2] = $lower; $old[3] = $upper
$new = New-Object byte[] 64
$new[0] = 3; $new[1] = 21; $new[2] = 1; $new[3] = 72; $new[4] = $lower; $new[5] = $upper
$mode = ''
$err = ''
try {
    $out = Invoke-Threshold $old
    if ($out[0] -eq 0) { $mode = '默认接口' } else { $err = '默认接口返回 ' + (($out | Select-Object -First 3) -join ',') }
} catch { $err = '默认接口异常: ' + $_.Exception.Message }
if ($mode -eq '') {
    try {
        $out2 = Invoke-Threshold $new
        if ($out2[0] -eq 0) { $mode = '新版接口' } else { $err = $err + ' | 新版接口返回 ' + (($out2 | Select-Object -First 3) -join ',') }
    } catch { $err = $err + ' | 新版接口异常: ' + $_.Exception.Message }
}
if ($mode -ne '') {
    Write-Log 'OK' $upper $lower $mode
    try { if (Test-Path -LiteralPath $FailPath) { Remove-Item -LiteralPath $FailPath -Force } } catch { }
    exit 0
}
if ($warn.Count -gt 0) { $err = $err + ' | 配置警告: ' + ($warn -join '; ') }
Write-Log 'FAIL' $upper $lower ('失败 ' + $err)
Save-Failure $err
exit 1
