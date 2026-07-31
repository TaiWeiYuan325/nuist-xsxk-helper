@echo off
start "" "%~dp0bin\x64\Debug\net8.0-windows10.0.19041.0\XsxkWinUI.exe"
timeout /t 3 /nobreak >nul
tasklist /FI "IMAGENAME eq XsxkWinUI.exe" /NH
timeout /t 3 /nobreak >nul
tasklist /FI "IMAGENAME eq XsxkWinUI.exe" /NH
