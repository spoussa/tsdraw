@echo off
rem Publish a new version to GitHub (see release.ps1). Example: release.cmd minor -Notes "New pens"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
