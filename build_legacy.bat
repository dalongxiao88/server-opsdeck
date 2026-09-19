@echo off
chcp 65001 >nul
setlocal

rem Purpose: build the legacy WinForms client as an independent Windows single-file package.
rem Scope: only ServerForge.csproj in this directory; no files are read from the new-version folder.
rem Author: shisan8887@gmail
rem Created: 2026-09-15

pushd "%~dp0"
set "OUTPUT_DIR=legacy_publish"

echo ServerForge single-file publish
echo.
dotnet restore ServerForge.csproj
if errorlevel 1 goto :failed

if exist "%OUTPUT_DIR%" rd /s /q "%OUTPUT_DIR%"
dotnet publish ServerForge.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false -o "%OUTPUT_DIR%"
if errorlevel 1 goto :failed

del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
del /q "%OUTPUT_DIR%\*.json" >nul 2>&1
del /q "%OUTPUT_DIR%\*.dll" >nul 2>&1
del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1

echo.
echo ServerForge single-file publish complete: %OUTPUT_DIR%
popd
exit /b 0

:failed
echo.
echo Legacy publish failed.
popd
exit /b 1
