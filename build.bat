@echo off
cd /d "%~dp0"
dotnet build NoiseFacadeGH.csproj -c Release
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
copy /Y "bin\Release\net48\NoiseFacadeGH.dll" "%APPDATA%\Grasshopper\Libraries\NoiseFacadeGH.gha"
echo.
echo Installed to %APPDATA%\Grasshopper\Libraries\NoiseFacadeGH.gha
echo Reload Grasshopper (unload + reload plugin, or restart Rhino).
