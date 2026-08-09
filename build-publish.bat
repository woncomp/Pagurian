@echo off
setlocal enabledelayedexpansion

set "CONFIG=Release"
set "ARCH=x64"

if not "%~1"=="" set "CONFIG=%~1"
if not "%~2"=="" set "ARCH=%~2"

if /I "%ARCH%"=="x64" (
    call :Publish win-x64
    if errorlevel 1 exit /b 1
) else if /I "%ARCH%"=="ARM64" (
    call :Publish win-arm64
    if errorlevel 1 exit /b 1
) else if /I "%ARCH%"=="all" (
    call :Publish win-x64
    if errorlevel 1 exit /b 1
    call :Publish win-arm64
    if errorlevel 1 exit /b 1
) else (
    echo Unknown architecture: %ARCH%
    echo Usage: build-publish.bat [Release^|Debug] [x64^|ARM64^|all]
    exit /b 1
)

echo Done.
endlocal
exit /b 0

:Publish
set "PROFILE=%~1"
echo Publishing with profile %PROFILE%...
dotnet publish Pagurian\Pagurian.csproj /p:PublishProfile=%PROFILE%
if errorlevel 1 (
    echo Publish failed for profile %PROFILE%.
    exit /b 1
)
exit /b 0
