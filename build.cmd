@echo off
setlocal

set DEFINE=
if /i "%~1"=="selftest" set DEFINE=/define:SELFTEST

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found
    exit /b 1
)
if not exist lib\0Harmony.dll (
    echo lib\0Harmony.dll missing, run fetch-deps.ps1 first
    exit /b 1
)
if not exist lib\dnlib.dll (
    echo lib\dnlib.dll missing, run fetch-deps.ps1 first
    exit /b 1
)
if not exist bin mkdir bin

"%CSC%" /nologo %DEFINE% /target:library /out:bin\PclHook.dll ^
    /r:lib\0Harmony.dll ^
    /r:lib\dnlib.dll ^
    /r:System.Windows.Forms.dll ^
    /r:System.IO.Compression.dll ^
    /r:System.IO.Compression.FileSystem.dll ^
    src\AssemblyInfo.cs ^
    src\DomainManager.cs ^
    src\Refl.cs ^
    src\Patches.cs ^
    src\HookPlan.cs ^
    src\PatchMethods.cs ^
    src\ModpackInstaller.cs ^
    src\NativeDetour.cs ^
    src\SelfCheck.cs

if errorlevel 1 exit /b 1
echo Build OK: bin\PclHook.dll

"%CSC%" /nologo /target:exe /out:bin\pclhook-check.exe ^
    /r:lib\dnlib.dll ^
    src\HookPlan.cs ^
    check\CheckProgram.cs

if errorlevel 1 exit /b 1
echo Build OK: bin\pclhook-check.exe

copy /y lib\dnlib.dll bin\ >nul || exit /b 1
copy /y lib\0Harmony.dll bin\ >nul || exit /b 1
