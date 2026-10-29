@echo off
rem Publish the repository to GitHub and generate market/index.json.
rem The token is read from the GH_TOKEN environment variable; it is never
rem printed, never written to disk and never stored in .git/config.
rem
rem   set GH_TOKEN=ghp_xxxxxxxx
rem   tools\publish.cmd
chcp 65001 >nul
setlocal
cd /d "%~dp0.."

if "%GH_TOKEN%"=="" (
  echo GH_TOKEN is not set.
  echo   set GH_TOKEN=ghp_your_token_here
  echo   tools\publish.cmd
  exit /b 2
)

set "PROXY_PORT=7891"
start "" /b node tools\gitproxy.mjs %PROXY_PORT% > "%TEMP%\dsh-naiwa-gitproxy.log" 2>&1
timeout /t 2 /nobreak >nul

node tools\publish.mjs 991903428 dsh-naiwa-pet
set "CODE=%ERRORLEVEL%"

taskkill /f /fi "WINDOWTITLE eq gitproxy*" >nul 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | Where-Object { $_.CommandLine -like '*gitproxy.mjs*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }" >nul 2>&1
exit /b %CODE%
