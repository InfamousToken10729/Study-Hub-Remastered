@echo off
setlocal EnableDelayedExpansion
title Study Hub v3.0 - Dual Platform Builder (.EXE + .APK)
cd /d "%~dp0"

set "NO_LAUNCH=0"
if /i "%~1"=="--no-launch" set "NO_LAUNCH=1"

echo ============================================================================
echo   STUDY HUB v3.0 REMASTERED - DUAL-TARGET BUILDER (WINDOWS .EXE + ANDROID .APK)
echo ============================================================================
echo.

REM --- 1. Resolve .NET 9 SDK with MAUI Android Workload ---
set "DOTNET_CMD=%LOCALAPPDATA%\StudyHubDotNet\dotnet.exe"
if not exist "%DOTNET_CMD%" (
    where dotnet >nul 2>&1
    if not errorlevel 1 (
        set "DOTNET_CMD=dotnet"
    ) else (
        echo [INFO] Bootstrapping user-local .NET SDK with MAUI Android workload...
        robocopy "C:\Program Files\dotnet" "%LOCALAPPDATA%\StudyHubDotNet" /E /NFL /NDL /NJH /NJS /NC /NS /NP >nul
        if exist "%LOCALAPPDATA%\StudyHubDotNet\metadata\workloads\9.0.300\installertype\msi" (
            del /f /q "%LOCALAPPDATA%\StudyHubDotNet\metadata\workloads\9.0.300\installertype\msi"
        )
        set "DOTNET_CMD=%LOCALAPPDATA%\StudyHubDotNet\dotnet.exe"
        "%DOTNET_CMD%" workload install maui-android
    )
)

set "ANDROID_SDK=%LOCALAPPDATA%\Android\Sdk"
set "JAVA_SDK=%LOCALAPPDATA%\Android\jdk"

if not exist "%ANDROID_SDK%\platforms\android-35" (
    echo [INFO] Installing Android SDK Platform 35 and OpenJDK...
    "%DOTNET_CMD%" build "%~dp0StudyHub.csproj" -f net9.0-android -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=true -p:AndroidSdkDirectory="%ANDROID_SDK%" -p:JavaSdkDirectory="%JAVA_SDK%"
)

REM --- 2. Terminate any running StudyHub.exe instance ---
taskkill /F /IM StudyHub.exe >nul 2>&1

REM --- 3. Clean previous temporary build directories ---
if exist "%~dp0publish_win" rd /s /q "%~dp0publish_win"
if exist "%~dp0publish_android" rd /s /q "%~dp0publish_android"
if exist "%~dp0bin" rd /s /q "%~dp0bin"
if exist "%~dp0obj" rd /s /q "%~dp0obj"

echo [1/2] Building Windows Desktop Single-File Executable (StudyHub.exe)...
"%DOTNET_CMD%" publish "%~dp0StudyHub.csproj" -f net9.0-windows -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o "%~dp0publish_win"
if errorlevel 1 (
    echo.
    echo [ERROR] Windows .EXE build failed!
    if "%NO_LAUNCH%"=="0" pause
    exit /b 1
)

if exist "%~dp0publish_win\StudyHub.exe" (
    copy /Y "%~dp0publish_win\StudyHub.exe" "%~dp0StudyHub.exe" >nul
    echo       [OK] StudyHub.exe created successfully!
) else (
    echo [ERROR] publish_win\StudyHub.exe was not found!
    if "%NO_LAUNCH%"=="0" pause
    exit /b 1
)

echo.
echo [2/2] Building Native .NET MAUI Android Package (StudyHub.apk)...
"%DOTNET_CMD%" publish "%~dp0StudyHub.csproj" -f net9.0-android -c Release -p:AndroidPackageFormat=apk -p:AndroidSdkDirectory="%ANDROID_SDK%" -p:JavaSdkDirectory="%JAVA_SDK%" -o "%~dp0publish_android"
if errorlevel 1 (
    echo.
    echo [ERROR] Android .APK build failed!
    if "%NO_LAUNCH%"=="0" pause
    exit /b 1
)

set "APK_FOUND="
for %%F in ("%~dp0publish_android\*-Signed.apk") do set "APK_FOUND=%%~fF"
if not defined APK_FOUND (
    for %%F in ("%~dp0publish_android\*.apk") do set "APK_FOUND=%%~fF"
)

if defined APK_FOUND (
    copy /Y "!APK_FOUND!" "%~dp0StudyHub.apk" >nul
    echo       [OK] StudyHub.apk created successfully!
) else (
    echo [ERROR] No .apk file found in publish_android!
    if "%NO_LAUNCH%"=="0" pause
    exit /b 1
)

echo.
echo [CLEANUP] Removing intermediate build folders...
if exist "%~dp0publish_win" rd /s /q "%~dp0publish_win"
if exist "%~dp0publish_android" rd /s /q "%~dp0publish_android"
if exist "%~dp0bin" rd /s /q "%~dp0bin"
if exist "%~dp0obj" rd /s /q "%~dp0obj"

echo.
echo ============================================================================
echo   BUILD COMPLETE!
echo   - Windows Desktop: %~dp0StudyHub.exe
echo   - Android Mobile : %~dp0StudyHub.apk
echo ============================================================================

if "%NO_LAUNCH%"=="0" (
    echo Launching StudyHub.exe for testing...
    start "" "%~dp0StudyHub.exe"
    pause
)
exit /b 0
