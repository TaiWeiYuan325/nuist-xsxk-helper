@echo off
rem dotnet build wrapper for MSYS shells
set "PATH=C:\Program Files\dotnet;%ProgramData%;C:\Program Files;%PATH%"
set "PROGRAMDATA=%ProgramData%"
set "PROGRAMFILES=C:\Program Files"
"C:\Program Files\dotnet\dotnet.exe" %*
