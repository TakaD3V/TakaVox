@echo off
rem Compila o RNNoise 0.2 (Xiph.Org, licenca BSD) em duas DLLs x64: AVX2 (rapida) e SSE2 (compatibilidade).
rem Uso: build_rnnoise.cmd <pasta-do-fonte-rnnoise> <pasta-de-saida>
setlocal
set "SRC=%~1"
set "OUT=%~2"
if "%SRC%"=="" ( echo uso: build_rnnoise.cmd SRC OUT & exit /b 2 )
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

set FILES="%SRC%\src\denoise.c" "%SRC%\src\rnn.c" "%SRC%\src\pitch.c" "%SRC%\src\kiss_fft.c" "%SRC%\src\celt_lpc.c" "%SRC%\src\nnet.c" "%SRC%\src\nnet_default.c" "%SRC%\src\parse_lpcnet_weights.c" "%SRC%\src\rnnoise_data.c" "%SRC%\src\rnnoise_tables.c"
set DEFS=/DWIN32 /DRNNOISE_BUILD /DDLL_EXPORT /D_USE_MATH_DEFINES /D_CRT_SECURE_NO_WARNINGS /D__SSE__ /D__SSE2__
set COMMON=/nologo /O2 /Oi /GS- /std:c11 /W1 /MT %DEFS% /I"%SRC%\include" /I"%SRC%\src"

if not exist "%OUT%\obj_avx2" mkdir "%OUT%\obj_avx2"
if not exist "%OUT%\obj_sse2" mkdir "%OUT%\obj_sse2"

echo === AVX2 ===
cl %COMMON% /arch:AVX2 /D__FMA__ /D__SSSE3__ /D__SSE4_1__ /Fo"%OUT%\obj_avx2\\" /LD %FILES% /Fe"%OUT%\rnnoise_avx2.dll" /link /NOLOGO || exit /b 1

echo === SSE2 ===
cl %COMMON% /Fo"%OUT%\obj_sse2\\" /LD %FILES% /Fe"%OUT%\rnnoise_sse2.dll" /link /NOLOGO || exit /b 1

echo OK
