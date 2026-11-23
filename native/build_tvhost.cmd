@echo off
rem Compila o tvhost.dll: hospedeiro de plugins VST3/VST2 do TakaVox (usa o SDK oficial do VST3, licenca MIT).
rem Uso: build_tvhost.cmd <pasta-de-saida> [test]
rem   test = tambem compila os plugins de teste (tvtest.vst3 e tvtest2.dll) usados pelo tools\PluginTest.cs
setlocal
set "HERE=%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%HERE%..\build\native"
if not exist "%OUT%" mkdir "%OUT%"
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

rem O compilador do Windows nao aceita caminhos com mais de 260 caracteres: trabalha numa copia em pasta curta.
set "W=%TEMP%\tvh"
for %%D in (pluginterfaces base) do (
  robocopy "%HERE%vst3sdk\%%D" "%W%\sdk\%%D" /MIR /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 exit /b 1
)
robocopy "%HERE%vst3sdk\public.sdk\source" "%W%\sdk\public.sdk\source" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 exit /b 1
robocopy "%HERE%tvhost" "%W%\src" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 exit /b 1

set "S=%W%\sdk"
set "P=%S%\public.sdk\source"
set FLAGS=/nologo /utf-8 /std:c++17 /EHsc /O2 /MT /W3 /wd4996 /wd4267 /wd4244 /DRELEASE=1 /DNDEBUG /DUNICODE /D_UNICODE /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /I"%S%"

set IFACE="%S%\pluginterfaces\base\coreiids.cpp" "%S%\pluginterfaces\base\funknown.cpp" "%S%\pluginterfaces\base\ustring.cpp" "%S%\pluginterfaces\base\conststringtable.cpp" "%P%\common\commoniids.cpp" "%P%\vst\vstinitiids.cpp"
set HOSTING="%P%\common\memorystream.cpp" "%P%\common\commonstringconvert.cpp" "%P%\common\threadchecker_win32.cpp" "%P%\vst\utility\stringconvert.cpp" "%P%\vst\hosting\connectionproxy.cpp" "%P%\vst\hosting\eventlist.cpp" "%P%\vst\hosting\hostclasses.cpp" "%P%\vst\hosting\module.cpp" "%P%\vst\hosting\module_win32.cpp" "%P%\vst\hosting\parameterchanges.cpp" "%P%\vst\hosting\pluginterfacesupport.cpp" "%P%\vst\hosting\plugprovider.cpp" "%P%\vst\hosting\processdata.cpp"

if not exist "%W%\obj\host" mkdir "%W%\obj\host"
echo === tvhost.dll ===
cl %FLAGS% /Fo"%W%\obj\host\\" /LD "%W%\src\tvhost.cpp" "%W%\src\tvaudio.cpp" %IFACE% %HOSTING% /Fe"%W%\tvhost.dll" /link /NOLOGO ole32.lib user32.lib gdi32.lib shell32.lib dwmapi.lib mfplat.lib mfreadwrite.lib mfuuid.lib || exit /b 1
copy /y "%W%\tvhost.dll" "%OUT%\tvhost.dll" >nul || exit /b 1

if /i not "%~2"=="test" goto :ok

set PLUGIN="%S%\base\source\baseiids.cpp" "%S%\base\source\fbuffer.cpp" "%S%\base\source\fdebug.cpp" "%S%\base\source\fobject.cpp" "%S%\base\source\fstreamer.cpp" "%S%\base\source\fstring.cpp" "%S%\base\source\timer.cpp" "%S%\base\source\updatehandler.cpp" "%S%\base\thread\source\flock.cpp" "%S%\base\thread\source\fcondition.cpp" "%P%\common\pluginview.cpp" "%P%\main\pluginfactory.cpp" "%P%\main\dllmain.cpp" "%P%\main\moduleinit.cpp" "%P%\vst\vstaudioeffect.cpp" "%P%\vst\vstbus.cpp" "%P%\vst\vstcomponent.cpp" "%P%\vst\vstcomponentbase.cpp" "%P%\vst\vsteditcontroller.cpp" "%P%\vst\vstparameters.cpp"
if not exist "%W%\obj\t3" mkdir "%W%\obj\t3"
echo === tvtest.vst3 (plugin de teste VST3) ===
cl %FLAGS% /Fo"%W%\obj\t3\\" /LD "%W%\src\test\tvtest3.cpp" %IFACE% %PLUGIN% /Fe"%W%\tvtest3.dll" /link /NOLOGO ole32.lib user32.lib comctl32.lib || exit /b 1
copy /y "%W%\tvtest3.dll" "%OUT%\tvtest.vst3" >nul || exit /b 1
if not exist "%W%\obj\t2" mkdir "%W%\obj\t2"
echo === tvtest2.dll (plugin de teste VST2) ===
cl /nologo /utf-8 /O2 /MT /W3 /D_CRT_SECURE_NO_WARNINGS /Fo"%W%\obj\t2\\" /LD "%W%\src\test\tvtest2.cpp" /Fe"%W%\tvtest2.dll" /link /NOLOGO user32.lib comctl32.lib || exit /b 1
copy /y "%W%\tvtest2.dll" "%OUT%\tvtest2.dll" >nul || exit /b 1

:ok
echo OK
