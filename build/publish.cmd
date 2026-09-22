@echo off
rem Produces a ready-to-run Crescendo in dist\:
rem   1. regenerates the icon,
rem   2. builds the native engine,
rem   3. runs the DSP and COM test suites (a failing test stops the release),
rem   4. publishes the app as a single framework-dependent executable.
rem
rem Framework-dependent keeps the download around a few MB; it needs the
rem .NET 8 Desktop Runtime, which the app tells the user about if it is missing.

setlocal
set ROOT=%~dp0..
set DIST=%ROOT%\dist

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-icon.ps1" >nul
if errorlevel 1 ( echo [!] Icon generation failed. & exit /b 1 )

call "%~dp0build-apo.cmd"
if errorlevel 1 exit /b 1

call "%~dp0run-tests.cmd"
if errorlevel 1 ( echo [!] Tests failed; not publishing. & exit /b 1 )

if exist "%DIST%" rmdir /s /q "%DIST%"

dotnet publish "%ROOT%\src\Crescendo.App\Crescendo.App.csproj" ^
    -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none -o "%DIST%" --nologo -v q
if errorlevel 1 ( echo [!] Publish failed. & exit /b 1 )

copy /y "%ROOT%\artifacts\CrescendoApo.dll" "%DIST%\CrescendoApo.dll" >nul

rem Sign both binaries with Ferhad's certificate so FerhadGuard treats the app
rem and its startup task as trusted. An unsigned APO still loads (the policy key
rem allows it); the signature is for Guard, not for Windows.
set SIGNER=%LOCALAPPDATA%\FerhadImza\imzala.ps1
if exist "%SIGNER%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SIGNER%" "%DIST%" >nul
    if errorlevel 1 ( echo [!] Signing failed. & exit /b 1 )
    echo [+] Signed with Ferhad's certificate
) else (
    echo [i] Signing script not found; binaries left unsigned.
)

echo.
echo [+] Release ready in %DIST%
dir /b "%DIST%"
exit /b 0
