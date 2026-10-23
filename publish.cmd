@echo off
rem Rebuilds dist\ExamSession.exe as a single self-contained executable.
cd /d "%~dp0"
dotnet publish ExamSession.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o dist
pause
