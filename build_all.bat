@echo off
chcp 65001 >nul
setlocal

rem Purpose: keep the legacy default build entry and forward to the independent single-file script.
rem Scope: build only the legacy WinForms client; do not include the new-version source or user data.
rem Author: shisan8887@gmail
rem Created: 2026-09-15

call "%~dp0build_legacy.bat"
exit /b %errorlevel%
