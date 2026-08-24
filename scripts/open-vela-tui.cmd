@echo off
setlocal

rem Opens the Vela TUI.
rem
rem Do not go back to "dotnet run": app.manifest is embedded only in the apphost
rem executable, so under "dotnet run" requireAdministrator never applies and the
rem compaction flow loses its only path to elevation.
rem
rem Arguments are deliberately not forwarded. --worker belongs to the elevated
rem child process that the TUI launches itself, and must never be started by hand.
rem
rem Messages here are ASCII on purpose. cmd.exe reads batch files in 512-byte
rem chunks and corrupts any multi-byte character that straddles a boundary, so
rem non-ASCII text breaks parsing at an offset that shifts with every edit.
rem All Chinese user-facing text belongs in the TUI itself.

for %%I in ("%~dp0..") do set "REPO=%%~fI"
set "EXE=%REPO%\artifacts\build\Vela.Tui\Release\net10.0-windows\Vela.Tui.exe"

if not exist "%EXE%" (
    echo Release executable not found, building it first:
    echo   %EXE%
    call :build || goto :fail
)

if not exist "%EXE%" (
    echo Build finished but the executable is still missing:
    echo   %EXE%
    goto :fail
)

rem S-1-16-12288 is the High Mandatory Level SID, i.e. this process is elevated.
whoami /groups | findstr /c:"S-1-16-12288" >nul 2>&1
if errorlevel 1 goto :elevate

"%EXE%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo Vela exited with code %EXIT_CODE%.
    pause
)
goto :done

:elevate
echo Vela requires administrator rights. A UAC prompt will open it in a new window.
echo To keep it in this window, reopen the terminal as administrator and rerun this script.
rem "&&" is used rather than "if errorlevel" so the branch depends only on the
rem exit code of this call. findstr above already left errorlevel at 1.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -Verb RunAs" && goto :launched
echo Elevated launch did not complete. The UAC prompt may have been cancelled.
set "EXIT_CODE=1"
pause
goto :done

:launched
echo Vela is starting in a new elevated window.
set "EXIT_CODE=0"
goto :done

:build
set "DOTNET="
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET (
    where dotnet >nul 2>&1
    if not errorlevel 1 set "DOTNET=dotnet"
)
if not defined DOTNET (
    echo dotnet was not found. Install the .NET 10 SDK and retry.
    exit /b 1
)
pushd "%REPO%"
"%DOTNET%" build -c Release --nologo
set "BUILD_EXIT=%ERRORLEVEL%"
popd
if not "%BUILD_EXIT%"=="0" (
    echo Build failed with exit code %BUILD_EXIT%.
    exit /b %BUILD_EXIT%
)
exit /b 0

:fail
set "EXIT_CODE=1"
pause

:done
endlocal & exit /b %EXIT_CODE%
