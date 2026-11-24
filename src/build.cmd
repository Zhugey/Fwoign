@echo off
setlocal
cd /d "%~dp0"

echo [1/4] 检查 Fwoign.exe 是否在运行...
tasklist /fi "imagename eq Fwoign.exe" 2>nul | find /i "Fwoign.exe" >nul
if not errorlevel 1 (
  echo.
  echo 检测到 Fwoign.exe 正在运行，文件被占用无法覆盖。
  echo 请先关闭程序窗口，再重新运行本脚本。
  echo.
  pause
  exit /b 1
)

echo [2/5] 编译 Release 版本...
dotnet build -c Release
if errorlevel 1 goto fail

set ROOT=%~dp0..

echo [3/5] 准备程序目录...
if not exist "%ROOT%\lib" mkdir "%ROOT%\lib"
if not exist "%ROOT%\log" mkdir "%ROOT%\log"
if not exist "%ROOT%\config" mkdir "%ROOT%\config"

echo [4/5] 复制 Fwoign.exe 到程序根目录...
copy /y "bin\Release\net48\Fwoign.exe" "%ROOT%\Fwoign.exe" >nul
if errorlevel 1 goto fail

echo [5/5] 复制外部库到 lib...
copy /y "bin\Release\net48\HandyControl.dll" "%ROOT%\lib\HandyControl.dll" >nul
if errorlevel 1 goto fail

if exist "%ROOT%\Fwoign.exe.config" del /q "%ROOT%\Fwoign.exe.config"

echo.
echo 构建完成：%ROOT%\Fwoign.exe
echo.
goto end

:fail
echo.
echo 构建失败，请查看上面的错误信息。
pause
exit /b 1

:end
endlocal
