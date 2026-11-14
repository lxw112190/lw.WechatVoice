@echo off
setlocal
set "SILK_VS_ROOT="
for /f "usebackq tokens=*" %%V in (`call "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -version "[17.0,18.0)" -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "SILK_VS_ROOT=%%V"
if not defined SILK_VS_ROOT exit /b 1
call "%SILK_VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
pushd "%~dp0..\.deps\silk-sdk"
if errorlevel 1 exit /b 1
if not exist "..\..\build\silk-obj" mkdir "..\..\build\silk-obj"
cl /nologo /O2 /EHsc /LD /Iinterface /Fo"../../build/silk-obj/" src\*.c silk_exports.c /Fe"../../build/silk.dll" >nul
set "SILK_BUILD_EXIT=%errorlevel%"
if not "%SILK_BUILD_EXIT%"=="0" goto finish
copy /y "..\..\build\silk.dll" "silk.dll" >nul
set "SILK_BUILD_EXIT=%errorlevel%"
:finish
popd
exit /b %SILK_BUILD_EXIT%
