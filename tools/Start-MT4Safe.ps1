#Requires -Version 5.1
<#
.SYNOPSIS
  以 AutoTrade 關閉(紅色)的狀態啟動一個或多個 MT4。

.DESCRIPTION
  對每個 terminal.exe 依序:
  1. 這個 MT4 已經在執行 → 直接略過(執行中修改設定檔沒有意義)
  2. 把 <MT4 資料夾>\config\experts.ini 位移 0x24 改成 0(AutoTrade 關閉)
     只就地改寫這 1 個位元組:不會截斷檔案,寫入中斷電最壞也只是沒改到
  3. 以 /portable 啟動 terminal.exe(之後的運作狀態交給 Lifeguard 監控)
  某一個失敗不影響其他的,最後列出總結。

  experts.ini 是 MetaQuotes 未公開的二進位格式。格式不符時不修改,
  並詢問是否仍要啟動,避免把檔案改壞。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Start-MT4Safe.ps1 "D:\MT4\MT4_Elev8\terminal.exe" "D:\MT4\MT4_Tickmill\terminal.exe"

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Start-MT4Safe.ps1 -DelaySeconds 10 "D:\MT4\MT4_Elev8\terminal.exe"
#>
# PositionalBinding=$false:只有明確標 Position 的參數才吃「沒有名稱」的引數。
# 否則第一個路徑會被當成 -DelaySeconds,出現「無法轉換成 Int32」的錯誤。
[CmdletBinding(PositionalBinding = $false)]
param(
    # 兩個 MT4 啟動之間的間隔(只有真的啟動時才等)
    [int]$DelaySeconds = 5,

    # 無人值守(開機自動執行):不詢問。experts.ini 無法改成關閉時不啟動那個 MT4,
    # 留給 Lifeguard 稍後啟動,避免開機時卡在問答畫面,也避免帶著 AutoTrade 開啟起來
    [switch]$Unattended,

    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$ExePath
)

$ErrorActionPreference = 'Stop'

$ExpectedSize    = 4388
$AutoTradeOffset = 0x24

# 回傳:'Patched' / 'AlreadyOff' / 'NotFound' / 'BadFormat'
function Set-AutoTradeOff([string]$iniPath) {
    if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) { return 'NotFound' }

    $bytes = [IO.File]::ReadAllBytes($iniPath)
    $formatOk = ($bytes.Length -eq $ExpectedSize) -and
                ($bytes[0] -eq 0x90) -and ($bytes[1] -eq 0x01) -and ($bytes[2] -eq 0x00) -and ($bytes[3] -eq 0x00) -and
                ($bytes[$AutoTradeOffset] -le 1)

    if (-not $formatOk) { return 'BadFormat' }
    if ($bytes[$AutoTradeOffset] -eq 0) { return 'AlreadyOff' }

    # 只寫 1 個位元組,和 Lifeguard 的做法一致
    $fs = [IO.File]::Open($iniPath, 'Open', 'Write', 'Read')
    try {
        $fs.Position = $AutoTradeOffset
        $fs.WriteByte(0)
        $fs.Flush($true)
    }
    finally { $fs.Dispose() }

    return 'Patched'
}

# 回傳一行結果字串
function Start-OneMT4([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host "[X] terminal.exe not found: $path" -ForegroundColor Red
        return "FAILED   $path (not found)"
    }

    $path       = (Resolve-Path -LiteralPath $path).Path
    $mt4Dir     = Split-Path -Parent $path
    $expertsIni = Join-Path $mt4Dir 'config\experts.ini'

    # 1. 已在執行 → 略過。用 Win32_Process 取路徑:比 Get-Process 的 .Path 更不受權限影響
    $running = Get-CimInstance Win32_Process -Filter "Name='terminal.exe'" |
        Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -ieq $path) }

    if ($running) {
        $pids = ($running | ForEach-Object ProcessId) -join ', '
        Write-Host "[=] Already running (PID: $pids), skipped: $path" -ForegroundColor Cyan
        return "SKIPPED  $path (already running)"
    }

    # 2. AutoTrade 關閉
    $result = Set-AutoTradeOff $expertsIni
    switch ($result) {
        'Patched'    { Write-Host '[+] AutoTrade forced OFF in experts.ini.' -ForegroundColor Green }
        'AlreadyOff' { Write-Host '[+] AutoTrade is already OFF in experts.ini.' -ForegroundColor Green }
        'NotFound'   { Write-Host "[!] experts.ini not found: $expertsIni" -ForegroundColor Yellow }
        'BadFormat'  { Write-Host '[!] experts.ini format is not recognised. File NOT modified.' -ForegroundColor Yellow }
    }

    if ($result -in @('NotFound', 'BadFormat')) {
        if ($Unattended) {
            Write-Host 'Unattended: not started (AutoTrade could not be forced OFF). Lifeguard will start it later.' -ForegroundColor Yellow
            return "SKIPPED  $path (AutoTrade not forced OFF, left to Lifeguard)"
        }

        Write-Host 'MT4 may start with AutoTrade ON.' -ForegroundColor Yellow
        $answer = Read-Host 'Start this MT4 anyway? (y/N)'
        if ($answer -notin @('y', 'Y')) {
            Write-Host 'Cancelled.'
            return "CANCELLED $path"
        }
    }

    # 3. 啟動。之後 MT4 有沒有正常運作(包含自動更新後的自我重啟)交給 Lifeguard 監控
    # 最小化啟動,和 Lifeguard 重啟 MT4 時一致
    $proc = Start-Process -FilePath $path -ArgumentList '/portable' -WorkingDirectory $mt4Dir -WindowStyle Minimized -PassThru
    Write-Host "[+] Started (PID: $($proc.Id)): $path /portable" -ForegroundColor Green
    return "STARTED  $path (PID $($proc.Id))"
}

$summary = @()
$lastStarted = $false

foreach ($p in $ExePath) {
    # 上一個真的有啟動才等,讓 MT4 錯開登入;略過的不用等
    if ($lastStarted) { Start-Sleep -Seconds $DelaySeconds }

    Write-Host ''
    Write-Host "=== $p" -ForegroundColor White
    try {
        $line = Start-OneMT4 $p
    }
    catch {
        Write-Host "[X] $($_.Exception.Message)" -ForegroundColor Red
        $line = "FAILED   $p ($($_.Exception.Message))"
    }

    $summary += $line
    $lastStarted = $line.StartsWith('STARTED')
}

Write-Host ''
Write-Host '=== Summary' -ForegroundColor White
foreach ($line in $summary) {
    $color = if ($line.StartsWith('STARTED')) { 'Green' }
             elseif ($line.StartsWith('SKIPPED')) { 'Cyan' }
             else { 'Red' }
    Write-Host "  $line" -ForegroundColor $color
}

# 有失敗或取消時回傳 1,方便 .bat 判斷
if ($summary | Where-Object { $_ -match '^(FAILED|CANCELLED)' }) { exit 1 }
exit 0
