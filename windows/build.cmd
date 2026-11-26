@echo off
rem Builds windows\dist\RAMLimiter.exe with the C# compiler that comes with Windows.
setlocal
cd /d "%~dp0"
set csc=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist dist mkdir dist
set icon=
if exist app.ico set icon=/win32icon:app.ico
"%csc%" /nologo /target:winexe /platform:x64 /optimize+ /debug- /warn:4 /utf8output /codepage:65001 ^
  /win32manifest:app.manifest %icon% /out:dist\RAMLimiter.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  App.cs Browsers.cs Server.cs Icons.cs
