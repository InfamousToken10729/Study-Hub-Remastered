@echo off
setlocal
cd /d "%~dp0"
title Study Hub v3.0 Remastered - One-Click Installer and EXE Builder

echo ============================================================================
echo   STUDY HUB v3.0 REMASTERED - NATIVE C# / WPF ONE-CLICK .EXE BUILDER
echo ============================================================================
echo.

REM 1. Check if .NET 9 SDK is installed; if not, install it automatically via winget
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [SETUP] .NET SDK not found on this PC. Installing Microsoft .NET 9 SDK...
    winget install --id Microsoft.DotNet.SDK.9 --silent --accept-package-agreements --accept-source-agreements
    set "PATH=%PATH%;%ProgramFiles%\dotnet"
)

dotnet --list-sdks | findstr /B "9." >nul 2>&1
if errorlevel 1 (
    echo [SETUP] .NET 9 SDK not detected. Installing Microsoft .NET 9 SDK via winget...
    winget install --id Microsoft.DotNet.SDK.9 --silent --accept-package-agreements --accept-source-agreements
    set "PATH=%PATH%;%ProgramFiles%\dotnet"
)

REM 2. Close any running StudyHub.exe instance so it can be updated cleanly
taskkill /F /IM StudyHub.exe >nul 2>&1

REM 3. Restore packages and compile standalone single-file StudyHub.exe
echo [1/3] Restoring packages and compiling Native C# WPF Application...
dotnet publish "StudyHub.csproj" -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "%~dp0publish_out"

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed! Please check the output above.
    pause
    exit /b 1
)

REM 4. Move compiled StudyHub.exe to root folder and clean temporary build artifacts
echo [2/3] Finalizing StudyHub.exe and cleaning temporary build files...
copy /Y "%~dp0publish_out\StudyHub.exe" "%~dp0StudyHub.exe" >nul

rmdir /S /Q "%~dp0publish_out" >nul 2>&1
rmdir /S /Q "%~dp0bin" >nul 2>&1
rmdir /S /Q "%~dp0obj" >nul 2>&1

echo [3/3] Build complete!
echo.
echo ============================================================================
echo   SUCCESS! StudyHub.exe is ready at:
echo     %~dp0StudyHub.exe
echo ============================================================================
echo.

if /I "%~1"=="--no-launch" exit /b 0

echo Launching StudyHub.exe...
start "" "%~dp0StudyHub.exe"
exit /b 0
