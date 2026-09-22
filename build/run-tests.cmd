@echo off
rem Builds and runs the DSP test suite.

setlocal
set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" (
    echo [!] Visual Studio Build Tools 2022 x64 not found.
    exit /b 1
)
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1

set OBJ=%~dp0..\artifacts\tests
if not exist "%OBJ%" mkdir "%OBJ%"

pushd "%OBJ%"

rem Same import stub the APO build uses, for AERT_Allocate/AERT_Free.
lib /nologo /DEF:"%~dp0audioeng-aert.def" /MACHINE:X64 /OUT:audioeng-aert.lib >nul
if errorlevel 1 (
    popd
    echo [!] Could not generate the audioeng import stub.
    exit /b 1
)

rem /fp:fast matches the APO build, so the tests measure the same arithmetic
rem the engine actually performs.
cl /nologo /std:c++17 /O2 /fp:fast /EHsc /MT /W3 ^
   /DUNICODE /D_UNICODE /DNDEBUG ^
   "%~dp0..\tests\dsp_tests.cpp" ^
   /Fe:dsp_tests.exe ^
   /link audioeng-aert.lib
if errorlevel 1 (
    popd
    echo [!] Test build failed.
    exit /b 1
)

cl /nologo /std:c++17 /O2 /EHsc /MT /W3 /DUNICODE /D_UNICODE /DNDEBUG ^
   "%~dp0..\tests\com_tests.cpp" /Fe:com_tests.exe /link ole32.lib
if errorlevel 1 (
    popd
    echo [!] COM test build failed.
    exit /b 1
)

echo.
call "%OBJ%\dsp_tests.exe"
set RESULT=%errorlevel%

echo.
call "%OBJ%\com_tests.exe" "%~dp0..\artifacts\CrescendoApo.dll"
if errorlevel 1 set RESULT=1
popd

if %RESULT% neq 0 (
    echo.
    echo [!] DSP tests failed.
    exit /b 1
)

echo.
echo [+] All DSP tests passed.
exit /b 0
