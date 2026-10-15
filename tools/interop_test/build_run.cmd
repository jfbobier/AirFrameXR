@echo off
set VCV="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist %VCV% for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set VCV="%%i\VC\Auxiliary\Build\vcvars64.bat"
call %VCV% >nul
cd /d %USERPROFILE%\interop
cl /nologo /EHsc /MD /O1 /std:c++17 interop_test.cpp /Fe:interop_test.exe /link d3d12.lib d3d11.lib dxgi.lib
if errorlevel 1 exit /b 1
echo ---- release run
interop_test.exe
echo ---- debug-layer run
interop_test.exe debug
