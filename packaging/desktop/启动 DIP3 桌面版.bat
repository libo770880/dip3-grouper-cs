@echo off
rem ============================================================
rem  DIP 3.0 Grouper - Desktop (portable) launcher
rem  NOTE: this file must stay ASCII-only.
rem        cmd.exe reads .bat with the console OEM code page (936),
rem        so UTF-8 Chinese comments would be executed as commands.
rem ============================================================
setlocal
set "EXE=%~dp0Dip3Grouper.Desktop.exe"

if not exist "%EXE%" (
  echo [ERROR] Dip3Grouper.Desktop.exe not found in this folder.
  echo         Please unzip the whole folder before running.
  pause
  exit /b 1
)

start "" "%EXE%"
