@echo off
rem Starts the self-contained build (no .NET install needed).
rem Rebuild it with: dotnet publish v2rayN\v2rayN\v2rayN.csproj -c Release -r win-x64 --self-contained true -o publish\Detour
start "" "%~dp0publish\Detour\Detour.exe"
