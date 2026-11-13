@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /out:ClaudePixel.exe ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ClaudePixel.cs
