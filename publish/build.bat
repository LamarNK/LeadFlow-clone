@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
cd /d "%ROOT%" || exit /b 1

set "SCRIPT=%ROOT%scripts\build.ps1"
set "MENU_SCRIPT=%ROOT%scripts\build-menu.ps1"

if not exist "%SCRIPT%" (
    echo Build script not found: %SCRIPT%
    exit /b 1
)

if not exist "%MENU_SCRIPT%" (
    echo Build menu script not found: %MENU_SCRIPT%
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo Required command not found in PATH: dotnet
    exit /b 1
)

if not "%~1"=="" goto cli

:menu
set "MENU_RESULT=%TEMP%\leadflow-build-menu-%RANDOM%-%RANDOM%.txt"
if exist "%MENU_RESULT%" del /f /q "%MENU_RESULT%" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -File "%MENU_SCRIPT%" -ResultPath "%MENU_RESULT%"
if errorlevel 1 (
    echo Failed to open build menu.
    if exist "%MENU_RESULT%" del /f /q "%MENU_RESULT%" >nul 2>&1
    exit /b 1
)

set "choice="
if exist "%MENU_RESULT%" (
    set /p "choice="<"%MENU_RESULT%"
    del /f /q "%MENU_RESULT%" >nul 2>&1
)

if not defined choice goto menu
if /i "%choice%"=="0" goto done

echo.
echo [build] %choice%
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Target %choice% %BUILD_OPTS%
call :pause_prompt
goto menu

:cli
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
goto done

:pause_prompt
echo.
echo Press any key to return to the menu...
powershell -NoProfile -Command "$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') > $null"
exit /b 0

:done
exit /b %ERRORLEVEL%