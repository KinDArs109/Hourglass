@echo off
setlocal

rem  build.cmd            -> portable single .exe, runs on any Windows 10/11 x64
rem  build.cmd framework  -> small .exe, needs the .NET 8 Desktop Runtime installed

cd /d "%~dp0"

if /i "%~1"=="framework" goto framework

echo Building portable build (self-contained)...
dotnet publish src\Hourglass\Hourglass.csproj ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o dist\portable
if errorlevel 1 goto failed
echo.
echo Ready: dist\portable\Hourglass.exe
goto done

:framework
echo Building framework-dependent build...
dotnet publish src\Hourglass\Hourglass.csproj ^
  -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=none ^
  -o dist\framework
if errorlevel 1 goto failed
echo.
echo Ready: dist\framework\Hourglass.exe (requires .NET 8 Desktop Runtime)
goto done

:failed
echo.
echo Build failed.
exit /b 1

:done
endlocal
