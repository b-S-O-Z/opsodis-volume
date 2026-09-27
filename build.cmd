@echo off
rem Builds OpsodisVolume.exe with the C# compiler bundled in .NET Framework 4.x (no SDK needed).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"

"%CSC%" /nologo /optimize+ /target:winexe /platform:anycpu ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:"%~dp0bin\OpsodisVolume.exe" ^
  "%~dp0src\CoreAudio.cs" "%~dp0src\VolumeSync.cs" "%~dp0src\Program.cs"
if errorlevel 1 exit /b 1

"%CSC%" /nologo /platform:anycpu /out:"%~dp0bin\Probe.exe" "%~dp0src\CoreAudio.cs" "%~dp0tools\Probe.cs"
if errorlevel 1 exit /b 1

echo Built: %~dp0bin\OpsodisVolume.exe
