@echo off
rem Builds TSDraw.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist dist mkdir dist
set /p VER=<VERSION
> src\Version.cs echo namespace TSDraw { static class BuildInfo { public const string Version = "%VER%"; } }
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:dist\TSDraw.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /win32icon:assets\tsdraw.ico /win32manifest:assets\app.manifest ^
  /resource:src\mod.js,TSDraw.mod.js /resource:assets\tsdraw.ico,TSDraw.icon ^
  src\AssemblyInfo.cs src\App.cs src\Cdp.cs src\Native.cs src\Overlay.cs src\Relay.cs src\Updater.cs src\Util.cs src\Version.cs
if errorlevel 1 exit /b 1
copy /y relay\tsdraw-relay.py dist\ >nul
copy /y README.md dist\ >nul
powershell -NoProfile -Command "Compress-Archive -Force -Path dist\TSDraw.exe,dist\tsdraw-relay.py,dist\README.md -DestinationPath dist\TSDraw.zip"
echo Built TSDraw %VER%: dist\TSDraw.exe and dist\TSDraw.zip
