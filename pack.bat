@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-release.ps1"
echo.
echo 打包完成，输出在 release 目录。
pause
