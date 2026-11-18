@echo off
rem ============================================================
rem  Build the portable (green / no-install) desktop client.
rem
rem  Usage:  publish-desktop.bat
rem  Output: dist\DIP3-Desktop\
rem
rem  NOTE 1: this file must stay ASCII-only. cmd.exe reads .bat with
rem          the console OEM code page (936), so UTF-8 Chinese comments
rem          would be parsed as commands and break the script.
rem  NOTE 2: this file must use CRLF line endings. With LF-only endings
rem          cmd.exe mangles every line (tokens get swallowed).
rem  NOTE 3: the output folder is rebuilt from scratch, but the files that
rem          belong to the RUNNING installation under data\ are stashed first
rem          and put back at the end (see KEEP list). Without this, every
rem          republish would silently wipe the local settings.json
rem          (app name / sub name / admin password / data source) and the
rem          personal price favourites.
rem ============================================================
setlocal enabledelayedexpansion
rem ---- target runtime: x64 (default) or x86 ----
rem      usage:  publish-desktop.bat [x64^|x86]
set "RID=%~1"
if "%RID%"=="" set "RID=x64"
set "ROOT=%~dp0"
set "OUT=%ROOT%dist\DIP3-Desktop"
if /I "%RID%"=="x86" set "OUT=%ROOT%dist\DIP3-Desktop-x86"
set "PROJ=%ROOT%src\Dip3Grouper.Desktop\Dip3Grouper.Desktop.csproj"

rem ---- runtime-owned files inside data\ : never overwritten by a republish ----
rem      wuhao.user.json is the per-machine manual maintenance layer of the
rem      basic-consumables list (add / edit / delete). It is NOT part of the
rem      data distribution whitelist, but it DOES belong to the running
rem      installation: without this line every republish would silently wipe
rem      whatever the operator typed in.
set "KEEPFILES=settings.json notice.json dezhou-favorites.json notice-read.json wuhao.user.json price-ai.json"
set "KEEP=%TEMP%\dip3_desktop_keep"

echo.
echo === DIP 3.0 desktop client - portable publish ===
echo project : %PROJ%
echo output  : %OUT%
echo.

rem ---- locate dotnet (PATH first, then the default install folder) ----
set "DOTNET=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
  ) else (
    echo [ERROR] dotnet not found in PATH nor in "%ProgramFiles%\dotnet".
    echo         Install the .NET 8 SDK and re-run.
    exit /b 1
  )
)
echo using dotnet : %DOTNET%
echo.

rem ---- [0/7] stash the runtime data of the existing installation ----
rem NOTE: a previous failed run may have left an unclaimed stash. Only drop it
rem when this deployment actually has a data\ folder to re-stash from.
if exist "%OUT%\data" (
  echo [0/7] stashing runtime data of the existing installation ...
  if exist "%KEEP%" rmdir /S /Q "%KEEP%"
  mkdir "%KEEP%" >nul 2>nul
  for %%f in (%KEEPFILES%) do (
    if exist "%OUT%\data\%%f" copy /Y "%OUT%\data\%%f" "%KEEP%\%%f" >nul
  )
  if exist "%OUT%\data\_backup" xcopy /E /I /Y /Q "%OUT%\data\_backup" "%KEEP%\_backup\" >nul
) else (
  echo [0/7] no existing installation found - nothing to stash.
)

if exist "%OUT%" (
  echo [1/7] cleaning previous output ...
  rmdir /S /Q "%OUT%"
)

echo [2/7] dotnet publish (self-contained win-%RID%, no trimming) ...
"%DOTNET%" publish "%PROJ%" -c Release -r win-%RID% /p:SelfContained=true -o "%OUT%" --nologo
if errorlevel 1 goto fail

echo [3/7] copying config\ ...
xcopy /E /I /Y /Q "%ROOT%config" "%OUT%\config\" >nul
if errorlevel 1 goto fail

echo [4/7] copying data\ ...
xcopy /E /I /Y /Q "%ROOT%data" "%OUT%\data\" >nul
if errorlevel 1 goto fail

echo [5/7] copying launchers and readme ...
xcopy /E /I /Y /Q "%ROOT%packaging\desktop" "%OUT%\" >nul
if errorlevel 1 goto fail

rem keep the portable folder clean: no pdb / xml docs
del /Q "%OUT%\*.pdb" >nul 2>nul
del /Q "%OUT%\*.xml" >nul 2>nul

rem ---- [6/7] put the runtime data of the installation back ----
if exist "%KEEP%" (
  echo [6/7] restoring runtime data of the existing installation ...
  for %%f in (%KEEPFILES%) do (
    if exist "%KEEP%\%%f" copy /Y "%KEEP%\%%f" "%OUT%\data\%%f" >nul
  )
  if exist "%KEEP%\_backup" xcopy /E /I /Y /Q "%KEEP%\_backup" "%OUT%\data\_backup\" >nul
  rmdir /S /Q "%KEEP%"
)

echo [7/7] running self-test on the published build ...
"%OUT%\Dip3Grouper.Desktop.exe" --selftest
if errorlevel 1 (
  echo.
  echo [WARN] self-test reported failures.
  echo        see: %%TEMP%%\dip3-desktop-selftest.log
  echo        published folder kept at: %OUT%
  exit /b 1
)

echo.
echo === DONE ===
echo portable folder : %OUT%
echo   - launchers    : the two Chinese-named .bat in the folder
echo   - self-test    : Dip3Grouper.Desktop.exe --selftest
echo   - runtime data : data\settings.json was preserved
echo.
echo Zip the whole folder to distribute. Do NOT ship a single exe alone:
echo the client needs config\ and data\ next to it.
exit /b 0

:fail
echo.
echo [FAIL] publish aborted.
rem best effort: put the runtime data back so the installation stays usable even
rem though the publish died half way. The stash itself is kept for safety.
if exist "%KEEP%" (
  echo        restoring runtime data ...
  if not exist "%OUT%\data" mkdir "%OUT%\data" >nul 2>nul
  for %%f in (%KEEPFILES%) do (
    if exist "%KEEP%\%%f" copy /Y "%KEEP%\%%f" "%OUT%\data\%%f" >nul
  )
  if exist "%KEEP%\_backup" xcopy /E /I /Y /Q "%KEEP%\_backup" "%OUT%\data\_backup\" >nul
  echo        restored from: %KEEP%
) else (
  echo        no runtime data had been stashed.
)
echo        fix the cause above (usually: the app is still running) and re-run.
exit /b 1
