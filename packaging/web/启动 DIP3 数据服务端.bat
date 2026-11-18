@echo off
rem ============================================================
rem  DIP 3.0 Grouper - Data Server (portable) launcher
rem
rem  Forwards to the software shell: no console window, tray icon
rem  and a small panel that shows the LAN address to share.
rem
rem  NOTE: this file must stay ASCII-only (cmd.exe reads .bat with
rem        code page 936, so UTF-8 Chinese would be run as commands).
rem ============================================================
setlocal
set "PORT=%~1"
if "%PORT%"=="" set "PORT=3210"
set "ROOT=%~dp0"
set "SHELL=%ROOT%Dip3Grouper.WebShell.exe"

if not exist "%SHELL%" (
  echo [ERROR] Dip3Grouper.WebShell.exe not found in this folder.
  echo         Please unzip the WHOLE folder before running.
  pause
  exit /b 1
)

start "" "%SHELL%" --port %PORT%
exit /b 0
