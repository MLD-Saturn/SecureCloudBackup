@echo off
setlocal EnableDelayedExpansion
REM ============================================
REM Azure Backup Tool - Build Installed Executable
REM ============================================
REM This builds an installed version that stores its
REM database in %LocalAppData%\SecureCloudBackup folder.
REM ============================================

echo Building INSTALLED executable for Windows x64...
echo.

REM Check if the exe is running
tasklist /FI "IMAGENAME eq SecureCloudBackup.exe" 2>NUL | find /I /N "SecureCloudBackup.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo ERROR: SecureCloudBackup.exe is currently running.
    echo Please close the application and try again.
    echo.
    pause
    exit /b 1
)

REM Clean previous builds (with retry for locked files)
if exist "publish\installed" (
    echo Cleaning previous build...
    rmdir /s /q "publish\installed" 2>NUL
    if exist "publish\installed" (
        echo.
        echo ERROR: Cannot delete publish folder - files may be in use.
        echo Please close any applications using files in that folder.
        echo.
        pause
        exit /b 1
    )
)

REM Build and publish
dotnet publish src\SecureCloudBackup -c Release -r win-x64 --self-contained true -o publish\installed

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build failed!
    pause
    exit /b 1
)

REM NOTE: No portable.marker file = installed mode

echo.
echo ============================================
echo INSTALLED Build successful!
echo ============================================
echo.
echo Output location: publish\installed\
echo.
echo Installation:
echo   Copy SecureCloudBackup.exe to any location (e.g., C:\Program Files\SecureCloudBackup)
echo.
echo Data storage:
echo   Database and settings will be stored in:
echo   %%LocalAppData%%\SecureCloudBackup\backup.db
echo.
echo Window title will show: "Azure Backup - Encrypted Cloud Backup"
echo.

REM Show file size
for %%A in (publish\installed\SecureCloudBackup.exe) do (
    set /a size=%%~zA / 1048576
    echo Executable size: approximately !size! MB
)

echo.
pause
endlocal
