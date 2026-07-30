@echo off
set "PATH=C:\Program Files\dotnet;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "ProgramData=C:\ProgramData"
set "ProgramFiles=C:\Program Files"
set "ProgramFiles(x86)=C:\Program Files (x86)"
dotnet %*
