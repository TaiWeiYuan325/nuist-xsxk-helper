@echo off
rem XamlCompiler.exe needs a real console stdin (piped stdin makes it exit 1 silently).
rem Use start /wait /min to give it a new minimized console; retry on flaky failure.
setlocal
cd /d "%~dp0"
set N=0
:retry
start "" /wait /min "C:\Users\Tai_Wei_Yuan\.nuget\packages\microsoft.windowsappsdk\1.6.250108002\tools\net472\XamlCompiler.exe" "%~1" "%~2"
if %ERRORLEVEL%==0 exit /b 0
set /a N+=1
if %N% LSS 4 goto retry
exit /b 1
