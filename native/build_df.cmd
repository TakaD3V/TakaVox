@echo off
rem Compila a ponte takavox_df.dll (IA DeepFilterNet3 com modelos embutidos).
rem Requer: Rust (rustup, toolchain stable-msvc) e Visual Studio Build Tools (linker MSVC).
setlocal
set "HERE=%~dp0"
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
rem O linker do Windows nao aceita caminhos com mais de 260 caracteres: use uma pasta curta.
if "%CARGO_TARGET_DIR%"=="" set "CARGO_TARGET_DIR=%HERE%takavox_df\target"
cargo build --release --manifest-path "%HERE%takavox_df\Cargo.toml" || exit /b 1
echo OK
