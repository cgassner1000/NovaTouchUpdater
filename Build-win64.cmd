@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
    echo FEHLER: .NET SDK nicht gefunden. Installiere .NET 8 SDK bzw. die Visual-Studio-Workload .NET-Desktopentwicklung.
    exit /b 1
)
set "VERSION_ARG="
if defined GITHUB_RUN_NUMBER set "VERSION_ARG=-p:Version=1.0.%GITHUB_RUN_NUMBER%"
dotnet publish "NovaTouchUpdater.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false %VERSION_ARG%
if errorlevel 1 (
    echo FEHLER: Build fehlgeschlagen.
    exit /b 1
)
echo.
echo Fertig: bin\Release\net8.0-windows\win-x64\publish\NovaTouchUpdater.exe
exit /b 0
