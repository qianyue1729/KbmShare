@echo off
rem Build with the .NET Framework compiler bundled with Windows (no SDK needed).
rem Output: single KbmShare.exe, copy to the other machine and run.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [X] csc.exe not found
    exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:exe /platform:x64 /optimize+ /warn:4 /out:KbmShare.exe /r:System.dll KbmShare.cs
if errorlevel 1 (
    echo [X] build failed
    exit /b 1
)
echo [OK] build succeeded: KbmShare.exe
endlocal
