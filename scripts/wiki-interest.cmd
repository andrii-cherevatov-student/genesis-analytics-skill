@echo off
if exist "%~dp0..\.build\wiki-interest.dll" (
  dotnet "%~dp0..\.build\wiki-interest.dll" %*
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wiki-interest.ps1" %*
)
