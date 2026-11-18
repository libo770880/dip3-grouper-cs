@echo off
rem ============================================================
rem  DIP 3.0 Grouper - Desktop (portable) launcher, jiceng fallback ON
rem  Same grouping behaviour as the production Node instance
rem  (bin/web.js --jiceng).
rem
rem  NOTE: this file must stay ASCII-only (cmd.exe reads .bat with
rem        the console OEM code page 936).
rem ============================================================
setlocal
set "EXE=%~dp0Dip3Grouper.Desktop.exe"

if not exist "%EXE%" (
  echo [ERROR] Dip3Grouper.Desktop.exe not found in this folder.
  echo         Please unzip the whole folder before running.
  pause
  exit /b 1
)

start "" "%EXE%" --jiceng
