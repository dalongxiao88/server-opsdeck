@echo off
chcp 65001 >nul
setlocal

echo 小白服务器管理器 - 编译
echo.
dotnet restore RDPManager.csproj
if errorlevel 1 goto :failed

if exist "发布包" rd /s /q "发布包"
dotnet publish RDPManager.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false -o "发布包"
if errorlevel 1 goto :failed

copy /Y "发布包\小白服务器管理器.exe" "小白服务器管理器.exe" >nul

echo.
echo 编译完成，输出目录：发布包
exit /b 0

:failed
echo.
echo 编译失败，请检查上面的错误信息。
exit /b 1
