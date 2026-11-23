//! takavox_df: DLL com API C simples para o TakaVox usar a IA DeepFilterNet3 em tempo real.
//! - Os dois modelos (padrao e baixa latencia) ficam embutidos: o app nao precisa baixar nada.
//! - Todas as funcoes capturam panics do Rust (nunca derrubam o app): em erro retornam NaN/null.
use std::panic::{catch_unwind, AssertUnwindSafe};

use df::tract::{DfParams, DfTract, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};

static MODEL_STD: &[u8] = include_bytes!("../../DeepFilterNet-0.5.6/models/DeepFilterNet3_onnx.tar.gz");
static MODEL_LL: &[u8] = include_bytes!("../../DeepFilterNet-0.5.6/models/DeepFilterNet3_ll_onnx.tar.gz");

pub struct TvDf {
    df: DfTract,
    hop: usize,
}

/// Cria o estado da IA. `low_latency` != 0 usa o modelo de baixa latencia.
/// Retorna null se falhar.
#[no_mangle]
pub extern "C" fn tvdf_create(low_latency: i32, atten_lim_db: f32) -> *mut TvDf {
    let r = catch_unwind(|| -> Option<*mut TvDf> {
        let bytes: &'static [u8] = if low_latency != 0 { MODEL_LL } else { MODEL_STD };
        let params = DfParams::from_bytes(bytes).ok()?;
        let rt = RuntimeParams::default_with_ch(1).with_atten_lim(atten_lim_db);
        let df = DfTract::new(params, &rt).ok()?;
        let hop = df.hop_size;
        Some(Box::into_raw(Box::new(TvDf { df, hop })))
    });
    match r {
        Ok(Some(p)) => p,
        _ => std::ptr::null_mut(),
    }
}

/// Tamanho do quadro em amostras (480 = 10 ms a 48 kHz).
#[no_mangle]
pub extern "C" fn tvdf_frame_length(st: *mut TvDf) -> i32 {
    if st.is_null() {
        return 0;
    }
    unsafe { (*st).hop as i32 }
}

/// Taxa de amostragem do modelo (48000).
#[no_mangle]
pub extern "C" fn tvdf_sample_rate(st: *mut TvDf) -> i32 {
    if st.is_null() {
        return 0;
    }
    unsafe { (*st).df.sr as i32 }
}

/// Atraso algoritmico em amostras (sobreposicao da STFT + antecipacao do modelo).
#[no_mangle]
pub extern "C" fn tvdf_latency(st: *mut TvDf) -> i32 {
    if st.is_null() {
        return 0;
    }
    let d = unsafe { &(*st).df };
    (d.fft_size - d.hop_size + d.lookahead * d.hop_size) as i32
}

/// Processa um quadro (frame_length amostras). Retorna o SNR local estimado em dB
/// (alto = voz limpa, baixo/negativo = so ruido) ou NaN em caso de erro.
#[no_mangle]
pub extern "C" fn tvdf_process(st: *mut TvDf, input: *const f32, output: *mut f32) -> f32 {
    if st.is_null() || input.is_null() || output.is_null() {
        return f32::NAN;
    }
    let r = catch_unwind(AssertUnwindSafe(|| unsafe {
        let s = &mut *st;
        let i = ArrayView2::from_shape_ptr((1, s.hop), input);
        let o = ArrayViewMut2::from_shape_ptr((1, s.hop), output);
        s.df.process(i, o)
    }));
    match r {
        Ok(Ok(lsnr)) => lsnr,
        _ => f32::NAN,
    }
}

/// Limite de atenuacao em dB (100 = remove o ruido por completo; 20-30 = mais natural).
#[no_mangle]
pub extern "C" fn tvdf_set_atten_lim(st: *mut TvDf, db: f32) {
    if st.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe { (*st).df.set_atten_lim(db) }));
}

/// Pos-filtro (0 desliga; 0.01-0.05 = limpeza extra mais agressiva).
#[no_mangle]
pub extern "C" fn tvdf_set_post_filter(st: *mut TvDf, beta: f32) {
    if st.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe { (*st).df.set_pf_beta(beta) }));
}

/// Limiares de processamento (dB de SNR local): abaixo de `min_db` o quadro e tratado como
/// so ruido; acima de `max_erb`/`max_df` os estagios sao pulados (voz ja limpa).
#[no_mangle]
pub extern "C" fn tvdf_set_thresholds(st: *mut TvDf, min_db: f32, max_erb: f32, max_df: f32) {
    if st.is_null() {
        return;
    }
    unsafe {
        let d = &mut (*st).df;
        d.min_db_thresh = min_db;
        d.max_db_erb_thresh = max_erb;
        d.max_db_df_thresh = max_df;
    }
}

#[no_mangle]
pub extern "C" fn tvdf_free(st: *mut TvDf) {
    if st.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe { drop(Box::from_raw(st)) }));
}
