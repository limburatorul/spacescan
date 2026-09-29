@echo off
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
%FW%\csc.exe /nologo /target:winexe /optimize /platform:anycpu /codepage:65001 /win32manifest:"%~dp0app.manifest" /win32icon:"%~dp0SpaceScan.ico" ^
 /lib:%FW%\WPF /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
 /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll ^
 /resource:"%~dp0MainWindow.xaml",MainWindow.xaml ^
 /out:"%~dp0SpaceScan.exe" "%~dp0SpaceScan.cs" "%~dp0App.cs"
