@echo off
rem =====================================================================
rem  DIP 3.0 Grouper (C# edition) - launcher
rem
rem  Usage:  run-web.bat [port] [--jiceng] [--home <project-root>]
rem    e.g.  run-web.bat 3210 --jiceng   (same as Node edition: 3210 + jiceng fallback)
rem          run-web.bat 3220           (another port; without --jiceng jiceng fallback is off)
rem
rem  Data dir: defaults to the project root (the folder containing web\ and config\).
rem            Override with the DIP3_HOME environment variable.
rem
rem  NOTE: This file is intentionally ASCII-only. A .bat containing non-ASCII
rem        text is read by cmd.exe in the console OEM codepage (e.g. 936 on
rem        Chinese Windows); UTF-8 Chinese comments turn into mojibake and the
rem        garbage lines get executed as commands, breaking the script.
rem        Keep this file ASCII-only unless you save it in the right codepage.
rem =====================================================================
setlocal

set "ROOT=%~dp0"
set "PORT=%~1"
if "%PORT%"=="" set "PORT=3210"
shift

set "EXE=%ROOT%src\Dip3Grouper.Web\bin\Release\net8.0\Dip3Grouper.Web.exe"
if not exist "%EXE%" set "EXE=%ROOT%src\Dip3Grouper.Web\bin\Debug\net8.0\Dip3Grouper.Web.exe"
if not exist "%EXE%" (
  echo [ERROR] Compiled executable not found. Please build first:
  echo     dotnet build -c Release src\Dip3Grouper.Web\Dip3Grouper.Web.csproj
  exit /b 1
)

if "%DIP3_HOME%"=="" set "DIP3_HOME=%ROOT%"
echo exe : %EXE%
echo home: %DIP3_HOME%
echo port: %PORT%
"%EXE%" --port %PORT% %1 %2 %3 %4 %5
endlocal
