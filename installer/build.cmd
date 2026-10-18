@echo off
rem Rebuilds every payload and then the installer -> ..\dist\TopBarExtrasSetup.exe
rem Needs: TopBar installed (its exe and Newtonsoft.Json.dll are compile
rem references), .NET Framework 4.8 reference assemblies, PyInstaller, Inno Setup 6.
setlocal
cd /d "%~dp0.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe

echo [1/4] extension widgets + engine
call extension\build-dll.cmd || goto :fail

echo [2/4] display watcher
"%CSC%" /nologo /target:winexe /optimize+ /out:watch\TopBarDisplayWatch.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  watch\TopBarDisplayWatch.cs || goto :fail

echo [3/4] settings app
python -m PyInstaller --noconfirm --clean --onefile --windowed ^
  --name "TopBar Settings" ^
  --icon "%CD%\assets\topbar_settings.ico" ^
  --add-data "%CD%\assets\topbar_settings.ico;." ^
  --add-data "%CD%\assets\fonts;fonts" ^
  --add-data "%CD%\assets\lan-setup.ps1;." ^
  --distpath "%CD%\dist" --workpath "%CD%\build" --specpath "%CD%\build" ^
  topbar_settings.py || goto :fail

echo [4/4] installer
"%ISCC%" /Q installer\TopBarExtras.iss || goto :fail

echo.
echo Built dist\TopBarExtrasSetup.exe
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
