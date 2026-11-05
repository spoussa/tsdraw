@echo off
rem Builds and runs test\GameSwitchTest.cs (opens Notepad and Paint for a few seconds).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0.."
if not exist test-out mkdir test-out
"%CSC%" /nologo /target:exe /out:test-out\GameSwitchTest.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  test\GameSwitchTest.cs src\GameSwitch.cs src\Native.cs
if errorlevel 1 exit /b 1
test-out\GameSwitchTest.exe
