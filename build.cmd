@echo off
rem Builds MemControl.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize /target:winexe /out:"%~dp0MemControl.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll "%~dp0MemControl.cs" "%~dp0Processes.cs" "%~dp0KernelHelp.cs" "%~dp0Smart.cs" "%~dp0AdminTask.cs"
