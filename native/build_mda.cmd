@echo off
rem Compila o pacote gratuito "mda-vst3" (efeitos de Paul Kellett, licenca MIT, que vem nos exemplos do VST 3 SDK)
rem como mda-vst3.vst3. O TakaVox instala esse arquivo na pasta "plugins" dele: quem nao tem nenhum plugin
rem ja consegue usar a pagina Plugins logo de cara.
rem Uso: build_mda.cmd <pasta-de-saida>
setlocal
set "HERE=%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%HERE%..\build\native"
if not exist "%OUT%" mkdir "%OUT%"
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

rem pasta curta (limite de 260 caracteres do compilador)
set "W=%TEMP%\tvh"
for %%D in (pluginterfaces base) do (
  robocopy "%HERE%vst3sdk\%%D" "%W%\sdk\%%D" /MIR /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 exit /b 1
)
robocopy "%HERE%vst3sdk\public.sdk\source" "%W%\sdk\public.sdk\source" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 exit /b 1
robocopy "%HERE%vst3sdk\public.sdk\samples\vst\mda-vst3\source" "%W%\mda" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 exit /b 1
copy /y "%HERE%mda_projectversion.h" "%W%\mda\projectversion.h" >nul || exit /b 1

set "S=%W%\sdk"
set "P=%S%\public.sdk\source"
set FLAGS=/nologo /MP /utf-8 /std:c++17 /EHsc /O2 /MT /W1 /wd4996 /wd4267 /wd4244 /wd4305 /DRELEASE=1 /DNDEBUG /DUNICODE /D_UNICODE /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /D_USE_MATH_DEFINES /I"%S%" /I"%W%\mda"
set IFACE="%S%\pluginterfaces\base\coreiids.cpp" "%S%\pluginterfaces\base\funknown.cpp" "%S%\pluginterfaces\base\ustring.cpp" "%S%\pluginterfaces\base\conststringtable.cpp" "%P%\common\commoniids.cpp" "%P%\vst\vstinitiids.cpp"
set PLUGIN="%S%\base\source\baseiids.cpp" "%S%\base\source\fbuffer.cpp" "%S%\base\source\fdebug.cpp" "%S%\base\source\fobject.cpp" "%S%\base\source\fstreamer.cpp" "%S%\base\source\fstring.cpp" "%S%\base\source\timer.cpp" "%S%\base\source\updatehandler.cpp" "%S%\base\thread\source\flock.cpp" "%S%\base\thread\source\fcondition.cpp" "%P%\common\pluginview.cpp" "%P%\main\dllmain.cpp" "%P%\main\moduleinit.cpp" "%P%\vst\vstaudioeffect.cpp" "%P%\vst\vstbus.cpp" "%P%\vst\vstcomponent.cpp" "%P%\vst\vstcomponentbase.cpp" "%P%\vst\vsteditcontroller.cpp" "%P%\vst\vstparameters.cpp" "%P%\vst\vstnoteexpressiontypes.cpp" "%P%\vst\utility\vst2persistence.cpp"

if not exist "%W%\obj\mda" mkdir "%W%\obj\mda"
echo === mda-vst3.vst3 ===
cl %FLAGS% /Fo"%W%\obj\mda\\" /LD "%W%\mda\*.cpp" %IFACE% %PLUGIN% /Fe"%W%\mda-vst3.dll" /link /NOLOGO ole32.lib user32.lib || exit /b 1
copy /y "%W%\mda-vst3.dll" "%OUT%\mda-vst3.vst3" >nul || exit /b 1
echo OK
