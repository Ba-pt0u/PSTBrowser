@echo off
rem Builds the native SQLite DLL used by PST Browser on Windows, with the Microsoft C compiler (Visual Studio Build Tools).
rem The C runtime is linked statically (/MT): the DLL only depends on KERNEL32.
setlocal
cd /d "%~dp0"
where cl >nul 2>nul
if errorlevel 1 (
  for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
)
if defined VSPATH call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
where cl >nul 2>nul
if errorlevel 1 (
  echo Compilateur C introuvable : installez "Build Tools for Visual Studio" ^(charge de travail C++^).
  exit /b 1
)
if not exist bin\win-x64 mkdir bin\win-x64
cl /nologo /O2 /MT /LD /W1 ^
  /DSQLITE_ENABLE_FTS5 /DSQLITE_THREADSAFE=1 /DSQLITE_DEFAULT_MEMSTATUS=0 /DSQLITE_DQS=0 /DSQLITE_OMIT_DEPRECATED ^
  /DSQLITE_OMIT_LOAD_EXTENSION /DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 /DSQLITE_LIKE_DOESNT_MATCH_BLOBS /DSQLITE_USE_URI=0 ^
  /DSQLITE_ENABLE_MATH_FUNCTIONS "/DSQLITE_API=__declspec(dllexport)" ^
  sqlite\sqlite3.c /Fo:bin\win-x64\sqlite3.obj /Fe:bin\win-x64\e_sqlite3.dll /link /NOLOGO
if errorlevel 1 exit /b 1
del bin\win-x64\sqlite3.obj bin\win-x64\e_sqlite3.lib bin\win-x64\e_sqlite3.exp 2>nul
echo bin\win-x64\e_sqlite3.dll
