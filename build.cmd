@echo off
rem Build the desktop pet from src\ into bin\ with the .NET Framework compiler.
rem No SDK, no package restore: csc.exe ships with Windows 10/11.
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set "OUT=%~dp0bin"
set "PET=%~dp0plugin\dsh-naiwa-pet\pet"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Cannot find csc.exe. Install .NET Framework 4.x first.
  exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"
set "TEMP=%~dp0temp"
set "TMP=%TEMP%"
if not exist "%TEMP%" mkdir "%TEMP%"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 ^
  /win32icon:"%~dp0src\naiwa.ico" /win32manifest:"%~dp0src\Pet.manifest" ^
  /out:"%OUT%\奶蛙桌宠.exe" ^
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /reference:System.Web.Extensions.dll ^
  "%~dp0src\NaiwaPet.cs" "%~dp0src\BalanceCard.cs" "%~dp0src\DshAccount.cs" "%~dp0src\MenuTheme.cs"
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

rem Assets: the archive is what the published package ships, so reuse it.
if exist "%PET%\素材.nwp" copy /y "%PET%\素材.nwp" "%OUT%\素材.nwp" >nul
if exist "%PET%\原版笑声.wav" copy /y "%PET%\原版笑声.wav" "%OUT%\原版笑声.wav" >nul
if exist "%PET%\使用说明.txt" copy /y "%PET%\使用说明.txt" "%OUT%\使用说明.txt" >nul
if exist "%~dp0LICENSE-ASSETS-PolyForm-Noncommercial-1.0.0.txt" copy /y "%~dp0LICENSE-ASSETS-PolyForm-Noncommercial-1.0.0.txt" "%OUT%\原作者许可_LICENCE.txt" >nul
if exist "%~dp0temp" rmdir /s /q "%~dp0temp"

echo Built: %OUT%\奶蛙桌宠.exe
