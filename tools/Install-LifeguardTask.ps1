#Requires -Version 5.1
<#
.SYNOPSIS
  建立(或覆蓋)Lifeguard 的工作排程器工作:每 5 分鐘檢查一次,沒在執行就啟動。

.NOTES
  - 必須以系統管理員身分執行(/rl HIGHEST)。
  - 「只在使用者登入時執行」(/it):Lifeguard 要和 MT4 在同一個 Windows session,
    Local\ 開頭的事件才看得到彼此。
  - 若 Lifeguard 與 MT4 的權限層級(一般 / 系統管理員)不同,Local\ 事件也可能互相看不到,
    所以兩邊請保持一致。
#>
param(
    [string]$TaskName = 'Lifeguard_Guardian',
    [string]$ExePath  = 'C:\MT4\Lifeguard\Lifeguard.exe',
    [int]$IntervalMinutes = 5
)

$ErrorActionPreference = 'Stop'

# 1) 必須是系統管理員
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '請用「以系統管理員身分執行」開啟 PowerShell 後再執行此腳本。'
}

# 2) 執行檔必須存在
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "找不到 Lifeguard 執行檔: $ExePath"
}

# 3) 建立工作(/f 覆蓋舊的;/it 只在使用者登入時執行,不需要密碼)
$user = "$env:USERDOMAIN\$env:USERNAME"
schtasks /create /tn $TaskName /tr "`"$ExePath`"" /sc minute /mo $IntervalMinutes /ru $user /it /rl HIGHEST /f
if ($LASTEXITCODE -ne 0) { throw "schtasks /create 失敗 (exit code $LASTEXITCODE)" }

# 4) 調整設定(只改 Settings,不動觸發器與執行身分)
$task = Get-ScheduledTask -TaskName $TaskName
$s = $task.Settings
$s.MultipleInstances           = 'IgnoreNew'   # 已在執行就不要再開第二個
$s.ExecutionTimeLimit          = 'PT0S'        # 不限制執行時間(預設會在 72 小時後被停止)
$s.DisallowStartIfOnBatteries  = $false
$s.StopIfGoingOnBatteries      = $false
$s.StartWhenAvailable          = $true         # 錯過觸發時間後補跑
Set-ScheduledTask -TaskName $TaskName -Settings $s | Out-Null

# 5) 顯示結果
$result = Get-ScheduledTask -TaskName $TaskName
Write-Host ''
Write-Host "已建立工作: $TaskName"
Write-Host "  執行檔           : $ExePath"
Write-Host "  執行身分         : $($result.Principal.UserId) (LogonType=$($result.Principal.LogonType), RunLevel=$($result.Principal.RunLevel))"
Write-Host "  重複間隔         : 每 $IntervalMinutes 分鐘"
Write-Host "  已在執行時       : $($result.Settings.MultipleInstances)"
Write-Host "  執行時間限制     : $($result.Settings.ExecutionTimeLimit)"
Write-Host ''
Write-Host '注意: 要停止 Lifeguard 時,請用 Ctrl+C 或 taskkill,不要在工作排程器按「結束」,'
Write-Host '      以免連 Lifeguard 啟動的 MT4 一起關掉。'
