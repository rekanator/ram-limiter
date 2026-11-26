@echo off
rem Makes the ready-made download: dist\RAM-Limiter-<version>-Windows.zip at the project's top,
rem holding the app, the extension, the license and the install steps.
setlocal
cd /d "%~dp0\.."
for /f %%v in ('powershell -NoProfile -Command "(Get-Content extension\manifest.json -Raw | ConvertFrom-Json).version"') do set version=%%v
if not defined version exit /b 1
call windows\build.cmd || exit /b 1
set out=dist\windows\RAM Limiter
if exist dist\windows rmdir /s /q dist\windows
mkdir "%out%" || exit /b 1
copy /y windows\dist\RAMLimiter.exe "%out%\" >nul || exit /b 1
copy /y LICENSE "%out%\" >nul || exit /b 1
copy /y windows\INSTALL.txt "%out%\How to install.txt" >nul || exit /b 1
xcopy /e /i /q extension "%out%\extension" >nul || exit /b 1
set zip=dist\RAM-Limiter-%version%-Windows.zip
if exist "%zip%" del "%zip%"
"%WINDIR%\System32\tar.exe" -a -c -f "%zip%" -C dist\windows "RAM Limiter" || exit /b 1
echo Made %zip%
