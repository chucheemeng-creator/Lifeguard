@echo off
chcp 65001 > nul
cls
title Lifeguard Protector Manager

:: 💡 定義 ANSI 色彩變數
set "ESC="
set "GREEN=%ESC%[92m"
set "RED=%ESC%[91m"
set "YELLOW=%ESC%[93m"
set "CYAN=%ESC%[96m"
set "RESET=%ESC%[0m"

:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Please %YELLOW%right-click%RESET% and select "%RED%Run as administrator%RESET%"!
    echo.
    pause
    exit /b
)

:MENU
cls
echo ===================================================
echo             Lifeguard PROTECTION MANAGER            
echo ===================================================
echo.
echo    1. %CYAN%[+]%RESET% Enable Protection  (Turn ON Lifeguard)
echo    2. %GREEN%[-]%RESET% Disable Protection (Turn OFF Lifeguard)
echo.
echo    ================================================
echo. 
echo    0. %YELLOW%[X] %RESET% Exit
echo.
echo ===================================================
echo.
set /p choice="Enter your choice [0-2]: "

if "%choice%"=="1" goto ENABLE_TASK
if "%choice%"=="2" goto DISABLE_TASK
if "%choice%"=="0" goto EXIT_PROG

:: Input validation
echo.
echo [!] Invalid selection, please try again.
timeout /t 2 >nul
goto MENU


:ENABLE_TASK
echo.
echo ---------------------------------------------------
echo Enabling and restoring Lifeguard protection...
echo ---------------------------------------------------

:: 1. Re-enable the task scheduler
schtasks /change /tn "Lifeguard_Guardian" /enable
if %errorLevel% equ 0 (
    echo [+] Successfully enabled the task scheduler.
) else (
    echo [X] Failed to enable the task. Please check the task name.
)

:: 2. Manually trigger the task to run the Lifeguard immediately
schtasks /run /tn "Lifeguard_Guardian"
if %errorLevel% equ 0 (
    echo [+] Successfully triggered and started the Lifeguard. Protection RESTORED!
) else (
    echo [X] Failed to trigger the Lifeguard program.
)

echo.
echo Press any key to return to menu...
pause >nul
goto MENU


:DISABLE_TASK
echo.
echo ---------------------------------------------------
echo Stopping and disabling Lifeguard protection...
echo ---------------------------------------------------

:: 1. Force end the currently running task instance
schtasks /end /tn "Lifeguard_Guardian"
if %errorLevel% equ 0 (
    echo [+] Successfully stopped the active task instance.
) else (
    echo [-] Task might not be running or could not be found.
)

:: 2. Disable the task to prevent it from auto-starting again
schtasks /change /tn "Lifeguard_Guardian" /disable
if %errorLevel% equ 0 (
    echo [+] Successfully disabled the task scheduler. Protection PAUSED.
) else (
    echo [X] Failed to disable the task. Please check the task name.
)

echo.
echo Press any key to return to menu...
pause >nul
goto MENU


:EXIT_PROG
exit