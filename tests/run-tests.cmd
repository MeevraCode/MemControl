@echo off
rem Builds the tests (with MemControl's own sources) and runs them. Exit code 0 = all passed.
setlocal
set ROOT=%~dp0..
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll
set SRC="%ROOT%\MemControl.cs" "%ROOT%\Processes.cs" "%ROOT%\KernelHelp.cs" "%ROOT%\Smart.cs" "%ROOT%\AdminTask.cs" "%ROOT%\Health.cs" "%ROOT%\Leaks.cs"
%CSC% /main:ProcessToolsTest /out:"%~dp0ProcessToolsTest.exe" %SRC% "%~dp0ProcessToolsTest.cs" || exit /b 1
%CSC% /main:SmartTest /out:"%~dp0SmartTest.exe" %SRC% "%~dp0SmartTest.cs" || exit /b 1
"%~dp0ProcessToolsTest.exe" || exit /b 1
"%~dp0SmartTest.exe"
