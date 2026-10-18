@echo off
rem Builds extension\bin\TopBarExtras.dll (all extension widgets + the engine).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8
set TB=%APPDATA%\TopBar
if not exist bin mkdir bin
"%CSC%" /nologo /target:library /optimize+ /out:bin\TopBarExtras.dll ^
  /r:"%TB%\TopBar.exe" /r:"%TB%\Newtonsoft.Json.dll" ^
  /r:"%REF%\PresentationCore.dll" /r:"%REF%\PresentationFramework.dll" ^
  /r:"%REF%\WindowsBase.dll" /r:"%REF%\System.Xaml.dll" /r:"%REF%\UIAutomationProvider.dll" ^
  Common.cs GpuSampler.cs Processes.cs CPULoadTunable.cs RAMUsage.cs GPUUsage.cs Lan.cs RemoteStat.cs ^
  Engine.cs ClockPanel.cs TopPanel.cs
