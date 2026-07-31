@echo off
cd /d "%~dp0"
"C:\Users\Tai_Wei_Yuan\.nuget\packages\microsoft.windowsappsdk\1.6.250108002\tools\net472\XamlCompiler.exe" "obj\x64\Debug\net8.0-windows10.0.19041.0\input.json" "obj\x64\Debug\net8.0-windows10.0.19041.0\output.json" < NUL > "%~dp0xc-stdin.out" 2>&1
echo EXIT=%ERRORLEVEL%
