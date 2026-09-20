@echo off
chcp 65001 >nul
setlocal

rem Build the opt-in NativeAOT distribution package.
rem The result is a folder because WebView2, WinForms and IronRDP need native DLLs.

pushd "%~dp0"
set "OUTPUT_DIR=protected_publish"

echo ServerForge protected NativeAOT publish
echo.
dotnet restore ServerForge.csproj -r win-x64 /p:ServerForgeProtectedBuild=true
if errorlevel 1 goto :failed

if exist "%OUTPUT_DIR%" rd /s /q "%OUTPUT_DIR%"
dotnet publish ServerForge.csproj -c Release --no-restore /p:ServerForgeProtectedBuild=true -o "%OUTPUT_DIR%"
if errorlevel 1 goto :failed

del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1

echo.
echo Protected NativeAOT publish complete: %OUTPUT_DIR%
popd
exit /b 0

:failed
echo.
echo Protected NativeAOT publish failed.
popd
exit /b 1
