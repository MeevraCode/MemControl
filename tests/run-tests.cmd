@echo off
rem Builds the process-tools test (with MemControl's own sources) and runs it. Exit code 0 = all passed.
set ROOT=%~dp0..
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /main:ProcessToolsTest /out:"%~dp0ProcessToolsTest.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%ROOT%\MemControl.cs" "%ROOT%\Processes.cs" "%ROOT%\KernelHelp.cs" "%~dp0ProcessToolsTest.cs" || exit /b 1
"%~dp0ProcessToolsTest.exe"
