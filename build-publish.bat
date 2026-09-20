@echo off
setlocal enabledelayedexpansion

set "CONFIG=Release"
if not "%~1"=="" set "CONFIG=%~1"

set "PUBLISH_DIR=publish"
set "MODULES_DIR=%PUBLISH_DIR%\modules"

rem Clean previous output
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"

rem Publish the host (win-x64 profile -> publish\)
echo Publishing Pagurian (%CONFIG%)...
dotnet publish Pagurian\Pagurian.csproj /p:PublishProfile=win-x64 /p:Configuration=%CONFIG%
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

rem Build and deploy the modules next to the host exe
call :DeployModule Pagurian.Modules.Hello
if errorlevel 1 exit /b 1
call :DeployModule Pagurian.Modules.Metrics
if errorlevel 1 exit /b 1
call :DeployModule Pagurian.Modules.Copilot
if errorlevel 1 exit /b 1

rem Keep release symbols in the build outputs, but do not ship them.
for /r "%PUBLISH_DIR%" %%F in (*.pdb) do del /q "%%F"

echo Done.
endlocal
exit /b 0

:DeployModule
set "PROJ=%~1"
set "HOST_MOD_DIR=Pagurian\bin\x64\%CONFIG%\net10.0-windows10.0.22621.0\modules\%PROJ%"
set "MOD_DIR=%MODULES_DIR%\%PROJ%"
if exist "%HOST_MOD_DIR%" rmdir /s /q "%HOST_MOD_DIR%"
echo Building module %PROJ%...
dotnet build "%PROJ%\%PROJ%.csproj" -c %CONFIG% -p:Platform=x64
if errorlevel 1 (
    echo Build failed for module %PROJ%.
    exit /b 1
)
if not exist "%HOST_MOD_DIR%\%PROJ%.dll" (
    echo Module deployment bundle was not created for %PROJ%.
    exit /b 1
)
if not exist "%MOD_DIR%" mkdir "%MOD_DIR%"
xcopy /y /q /s /e "%HOST_MOD_DIR%\*" "%MOD_DIR%\" >nul
if errorlevel 1 (
    echo Failed to copy %PROJ% deployment bundle.
    exit /b 1
)
exit /b 0
