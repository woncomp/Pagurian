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

echo Done.
endlocal
exit /b 0

:DeployModule
set "PROJ=%~1"
echo Building module %PROJ%...
dotnet build "%PROJ%\%PROJ%.csproj" -c %CONFIG% -p:Platform=x64
if errorlevel 1 (
    echo Build failed for module %PROJ%.
    exit /b 1
)
set "MOD_OUT=%PROJ%\bin\x64\%CONFIG%\net10.0-windows10.0.22621.0"
if not exist "%MODULES_DIR%" mkdir "%MODULES_DIR%"
copy /y "%MOD_OUT%\%PROJ%.dll" "%MODULES_DIR%\" >nul
if errorlevel 1 (
    echo Failed to copy %PROJ%.dll.
    exit /b 1
)
if exist "%MOD_OUT%\Assets" (
    xcopy /y /q /s "%MOD_OUT%\Assets\*" "%MODULES_DIR%\Assets\" >nul
    if errorlevel 1 (
        echo Failed to copy %PROJ% assets.
        exit /b 1
    )
)
exit /b 0
