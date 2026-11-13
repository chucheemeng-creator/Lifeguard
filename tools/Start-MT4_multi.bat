@echo off
setlocal
:: Start MT4 instances with AutoTrade OFF, then start HaNodeClient
:: Usage: double-click (interactive), or "Start-MT4_multi.bat -Unattended" for startup at logon
set "MODE="
if /i "%~1"=="-Unattended" set "MODE=-Unattended"

:: HaNodeClient location (change to the real path on each machine)
set "HANODE_EXE=C:\MT4\HaNodeClient\HaNodeClient.exe"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-MT4Safe.ps1" %MODE% ^
    "D:\MT4\MT4_Tickmill\terminal.exe" ^
    "D:\MT4\MT4_Elev8\terminal.exe" ^
    "D:\MT4\MT4_XMGlobal\terminal.exe"
set "MT4_RESULT=%errorlevel%"

echo.
echo === HaNodeClient
if not exist "%HANODE_EXE%" (
    echo [X] HaNodeClient not found: %HANODE_EXE%
    set "MT4_RESULT=1"
    goto :END
)

tasklist /fi "imagename eq HaNodeClient.exe" | find /i "HaNodeClient.exe" >nul
if not errorlevel 1 (
    echo [=] HaNodeClient is already running, skipped.
    goto :END
)

:: Give the MT4 windows a moment to appear before HaNodeClient starts syncing
timeout /t 5 /nobreak >nul
for %%I in ("%HANODE_EXE%") do start "" /min /d "%%~dpI" "%HANODE_EXE%"
echo [+] HaNodeClient started: %HANODE_EXE%

:END
echo.
if defined MODE (
    rem Unattended: never wait for a key; leave the window open briefly so it can be read over RDP
    timeout /t 30
) else if "%MT4_RESULT%"=="0" (
    timeout /t 3
) else (
    rem Something failed or was cancelled: keep the window open until a key is pressed
    pause
)
endlocal
