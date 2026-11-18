@echo off
rem ============================================================
rem  Build the portable (green / no-install) data server.
rem
rem  Usage:  publish-web.bat
rem  Output: dist\DIP3-Web\
rem
rem  NOTE 1: this file must stay ASCII-only. cmd.exe reads .bat with
rem          the console OEM code page (936), so UTF-8 Chinese comments
rem          would be parsed as commands and break the script.
rem  NOTE 2: this file must use CRLF line endings. With LF-only endings
rem          cmd.exe mangles every line (tokens get swallowed).
rem  NOTE 3: the output folder is rebuilt from scratch, but the files that
rem          belong to the RUNNING deployment under data\ are stashed first
rem          and put back at the end (see KEEP list). Without this, every
rem          republish would silently wipe the deployed settings.json
rem          (app name / sub name / admin password / data source), the
rem          notice board and every update backup.
rem ============================================================
setlocal enabledelayedexpansion
rem ---- target runtime: x64 (default) or x86 ----
rem      usage:  publish-web.bat [x64^|x86]
set "RID=%~1"
if "%RID%"=="" set "RID=x64"
set "ROOT=%~dp0"
set "OUT=%ROOT%dist\DIP3-Web"
if /I "%RID%"=="x86" set "OUT=%ROOT%dist\DIP3-Web-x86"
set "PROJ=%ROOT%src\Dip3Grouper.Web\Dip3Grouper.Web.csproj"
set "SHELLPROJ=%ROOT%src\Dip3Grouper.WebShell\Dip3Grouper.WebShell.csproj"

rem ---- runtime-owned files inside data\ : never overwritten by a republish ----
rem      wuhao.user.json is the per-machine manual maintenance layer of the
rem      basic-consumables list. Never distributed by the data catalog, but it
rem      must survive a republish of this deployment.
set "KEEPFILES=settings.json notice.json dezhou-favorites.json notice-read.json wuhao.user.json price-ai.json"
set "KEEP=%TEMP%\dip3_web_keep"

echo.
echo === DIP 3.0 data server - portable publish ===
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

rem ---- [0/8] stash the runtime data of the existing deployment ----
rem NOTE: a previous failed run may have left an unclaimed stash. Only drop it
rem when this deployment actually has a data\ folder to re-stash from.
if exist "%OUT%\data" (
  echo [0/8] stashing runtime data of the existing deployment ...
  if exist "%KEEP%" rmdir /S /Q "%KEEP%"
  mkdir "%KEEP%" >nul 2>nul
  for %%f in (%KEEPFILES%) do (
    if exist "%OUT%\data\%%f" copy /Y "%OUT%\data\%%f" "%KEEP%\%%f" >nul
  )
  if exist "%OUT%\data\_backup" xcopy /E /I /Y /Q "%OUT%\data\_backup" "%KEEP%\_backup\" >nul
) else (
  echo [0/8] no existing deployment found - nothing to stash.
)

if exist "%OUT%" (
  echo [1/8] cleaning previous output ...
  rmdir /S /Q "%OUT%"
)

echo [2/8] dotnet publish (self-contained win-%RID%, no trimming) ...
"%DOTNET%" publish "%PROJ%" -c Release -r win-%RID% /p:SelfContained=true -o "%OUT%" --nologo
if errorlevel 1 goto fail

echo [3/8] dotnet publish software shell (no console window, tray + panel) ...
rem NOTE: must run AFTER the web host. The shell project references Dip3Grouper.Web,
rem       so this publish re-copies Dip3Grouper.Web.exe into the same folder while the
rem       matching .deps.json / .runtimeconfig.json from step [2/8] are already there;
rem       the console entry therefore stays fully runnable for service / scheduled-task use.
"%DOTNET%" publish "%SHELLPROJ%" -c Release -r win-%RID% /p:SelfContained=true -o "%OUT%" --nologo
if errorlevel 1 goto fail

echo [4/8] copying config\ ...
xcopy /E /I /Y /Q "%ROOT%config" "%OUT%\config\" >nul
if errorlevel 1 goto fail

echo [5/8] copying data\ ...
xcopy /E /I /Y /Q "%ROOT%data" "%OUT%\data\" >nul
if errorlevel 1 goto fail

echo [6/8] copying web\ and launchers ...
xcopy /E /I /Y /Q "%ROOT%web" "%OUT%\web\" >nul
if errorlevel 1 goto fail
xcopy /E /I /Y /Q "%ROOT%packaging\web" "%OUT%\" >nul
if errorlevel 1 goto fail

rem keep the portable folder clean: no pdb / xml docs
del /Q "%OUT%\*.pdb" >nul 2>nul
del /Q "%OUT%\*.xml" >nul 2>nul

rem ---- [7/8] put the runtime data of the deployment back ----
if exist "%KEEP%" (
  echo [7/8] restoring runtime data of the existing deployment ...
  for %%f in (%KEEPFILES%) do (
    if exist "%KEEP%\%%f" copy /Y "%KEEP%\%%f" "%OUT%\data\%%f" >nul
  )
  if exist "%KEEP%\_backup" xcopy /E /I /Y /Q "%KEEP%\_backup" "%OUT%\data\_backup\" >nul
  rmdir /S /Q "%KEEP%"
)

echo [8/8] smoke test ...
set "PRT=3399"
start "" /B "%OUT%\Dip3Grouper.Web.exe" --port %PRT%
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command ^
  "$ok=$false; for($i=0;$i -lt 30;$i++){ try{ $r=Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 'http://127.0.0.1:%PRT%/api/data/manifest'; if($r.StatusCode -eq 200){ $ok=$true; break } }catch{} ; Start-Sleep -Milliseconds 500 }; if($ok){ $n=(($r.Content | ConvertFrom-Json).files).Count; Write-Host \"[OK] manifest reachable, files=$n\" } else { Write-Host '[FAIL] manifest not reachable' }"
taskkill /F /IM Dip3Grouper.Web.exe >nul 2>nul

echo.
echo === DONE ===
echo portable folder : %OUT%
echo   - user entry    : Dip3Grouper.WebShell.exe  ^(no console, tray + panel^)
echo   - ops entry     : Dip3Grouper.Web.exe --port 3210  ^(console / service^)
echo   - local pages   : http://localhost:3210   (admin: /admin)
echo   - runtime data  : data\settings.json, notice.json and _backup were preserved
echo.
echo Zip the whole folder to deploy. Do NOT ship a single exe alone:
echo the server needs config\ , data\ and web\ next to it.
exit /b 0

:fail
echo.
echo [FAIL] publish aborted.
rem best effort: put the runtime data back so the deployment stays usable even
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
