@echo off
rem Builds the Crescendo APO (the DLL that gets loaded into audiodg.exe).
rem
rem Two flags here are not optional:
rem   /MT          -- static CRT; audiodg cannot be relied on to have the
rem                   VC++ redistributable loaded.
rem   /MANIFEST:NO -- an embedded manifest makes the APO fail to load once the
rem                   DisableProtectedAudioDG test key is removed.

setlocal
set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" (
    echo [!] Visual Studio Build Tools 2022 x64 not found.
    exit /b 1
)
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1

set SRC=%~dp0..\src\Crescendo.Apo
set OBJ=%~dp0..\artifacts\obj
set OUT=%~dp0..\artifacts

if not exist "%OBJ%" mkdir "%OBJ%"
if not exist "%OUT%" mkdir "%OUT%"

pushd "%OBJ%"

rem Import stub for AERT_Allocate/AERT_Free -- declared in the SDK headers but
rem only shipped as an import library with the WDK.
lib /nologo /DEF:"%~dp0audioeng-aert.def" /MACHINE:X64 /OUT:audioeng-aert.lib >nul
if errorlevel 1 (
    popd
    echo [!] Could not generate the audioeng import stub.
    exit /b 1
)

cl /nologo /c /std:c++17 /O2 /Oi /fp:fast /EHsc /MT /W3 /GR ^
   /DUNICODE /D_UNICODE /DNDEBUG /DWIN32 /D_WINDOWS /D_WINDLL ^
   /I"%SRC%" ^
   "%SRC%\CrescendoApo.cpp" "%SRC%\Dll.cpp" "%SRC%\Guids.cpp"
if errorlevel 1 (
    popd
    echo [!] Compilation failed.
    exit /b 1
)

link /nologo /DLL /MANIFEST:NO /OPT:REF /OPT:ICF ^
     /OUT:"%OUT%\CrescendoApo.dll" ^
     /DEF:"%SRC%\Crescendo.def" ^
     CrescendoApo.obj Dll.obj Guids.obj ^
     AudioBaseProcessingObjectV140.lib audioeng-aert.lib ^
     ksuser.lib propsys.lib uuid.lib ole32.lib oleaut32.lib advapi32.lib user32.lib kernel32.lib
if errorlevel 1 (
    popd
    echo [!] Link failed.
    exit /b 1
)

popd
echo [+] Built %OUT%\CrescendoApo.dll
exit /b 0
