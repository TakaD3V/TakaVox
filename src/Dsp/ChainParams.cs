using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TakaVox.Dsp
{
    public sealed class EqBand
    {
        public string Name;
        public FilterType Type;
        public bool On = true;
        public float Freq;
        public float GainDb;
        public float Q;

        public EqBand(string name, FilterType type, float freq, float gain, float q)
        {
            Name = name; Type = type; Freq = freq; GainDb = gain; Q = q;
        }
    }

    /// <summary>
    /// Todos os parametros da cadeia de voz. A interface altera os campos e chama Touch();
    /// a thread de audio percebe a mudanca de Version e recalcula os coeficientes.
    /// </summary>
    public sealed class ChainParams
    {
        int version;
        public int Version => Volatile.Read(ref version);
        public void Touch() => Interlocked.Increment(ref version);

        // Entrada
        public float InputGainDb = 0f;       // seguro antes da calibracao (o nivelador sobe a voz se precisar)
        public int ChannelMode = 0;          // 0 auto, 1 esquerdo, 2 direito, 3 media

        // Filtro de graves (passa-altas)
        public bool HpfOn = true;
        public float HpfFreq = 85f;

        // Removedor de ventoinha/zumbido (cortes estreitos nos "apitos" fixos do ruido, antes da IA)
        public bool ToneOn = true;

        // Reducao de ruido por IA
        public bool NoiseOn = true;
        public float NoiseMix = 1.0f;        // intensidade 0..1 (DeepFilterNet: limite de atenuacao; RNNoise: mistura)
        public int NoiseEngine = 1;          // 0 = DeepFilterNet3 (limpeza maxima), 1 = DeepFilterNet3 LL (tempo real, padrao), 2 = RNNoise (leve)
        public bool NoiseExtra = false;      // pos-filtro: limpeza extra (mais agressiva)

        // Auto-Tune
        public bool TuneOn = false;
        public int TuneKey = 0;              // 0 = C ... 11 = B
        public int TuneScale = 0;            // ver AutoTune.ScaleNames
        public float TuneSpeed = 0f;         // 0 = robo instantaneo ... 100 = natural
        public float TuneAmount = 100f;      // % da correcao

        // Modificador de voz (tom e timbre independentes + efeitos). Ver VoicePresets.
        public bool VcOn = false;
        public float VcPitch = 0f;           // tom em semitons (-12..+12)
        public float VcFormant = 0f;         // timbre/formantes em % (-35..+60): + = garganta menor (mais fino), - = maior (mais grosso)
        public float VcIntonation = 100f;    // % da entonacao natural: 0 = monotono (robo), 100 = natural, 200 = bem expressiva
        public float VcVibDepth = 0f;        // vibrato em cents (0..150)
        public float VcVibRate = 5.5f;       // Hz (2..12)
        public float VcExtraMix = 0f;        // voz extra (segunda voz em outra altura): volume % (0 = desligada)
        public float VcExtraPitch = -12f;    // tom da voz extra em semitons, em relacao a sua voz (-24..+24)
        public float VcExtraFormant = 0f;    // timbre da voz extra em %
        public float VcBreath = 0f;          // sopro/sussurro % (0..50 = voz soprosa, 100 = so sussurro)
        public float VcSynth = 0f;           // robo sintetizado (vocoder) %
        public float VcSynthHz = 110f;       // nota do robo sintetizado (50..400 Hz)
        public float VcLowDb = 0f;           // graves da voz modificada (-12..+12 dB)
        public float VcHighDb = 0f;          // brilho da voz modificada (-12..+12 dB)
        public int VcFilter = 0;             // ver VoiceFx.FilterNames
        public float VcDrive = 0f;           // distorcao %
        public float VcRingMix = 0f;         // modulador em anel %
        public float VcRingHz = 60f;         // Hz (20..1000)
        public float VcMetal = 0f;           // metalico %
        public float VcMetalHz = 120f;       // Hz (50..500)
        public float VcTremDepth = 0f;       // tremolo %
        public float VcTremRate = 6f;        // Hz (1..20)
        public float VcEchoMix = 0f;         // eco %
        public float VcEchoMs = 280f;        // ms (40..1000)
        public float VcEchoFb = 35f;         // repeticoes % (0..85)
        public float VcReverbMix = 0f;       // reverb %
        public float VcReverbSize = 50f;     // tamanho do ambiente %

        // Voz com IA (Beatrice 2): troca a sua voz pela voz de uma personagem (ver Plugins/AiVoice). Fica no lugar do
        // motor de tom; os efeitos (filtros, eco, reverb) continuam valendo por cima.
        public bool VcAiOn = false;          // usar a voz com IA
        public float VcAiModel = 0f;         // modelo de voz (codigo do nome da pasta, BeatriceModel.Code; 0 = o primeiro)
        public int VcAiVoice = 0;            // voz dentro do modelo
        public float VcAiPitch = 0f;         // ajuste do tom (st) em relacao ao tom medio da voz alvo (-12..+12)
        public float VcAiFormant = 0f;       // timbre em st (-2..+2, passos de 0,5)
        public float VcAiInton = 100f;       // entonacao % (0 = monotono, 100 = natural, 200 = bem expressiva)

        // Mudo (nao salvo): Discord/jogos recebem silencio
        public volatile bool Mute;

        // Portao de voz (gate/expansor com VAD)
        public bool GateOn = true;
        public float GateThresholdDb = -55f;
        public float GateRangeDb = -30f;
        public float GateHoldMs = 220f;
        public float GateReleaseMs = 120f;
        public bool GateUseVad = true;
        public float GateVadThreshold = 0.45f;

        // Nivelador automatico (AGC lento)
        public bool AgcOn = true;
        public float AgcTargetDb = -20f;
        public float AgcMaxBoostDb = 10f;
        public float AgcMaxCutDb = 10f;
        public float AgcSpeed = 4f;          // dB por segundo

        // Equalizador
        public bool EqOn = true;
        public readonly EqBand[] Bands =
        {
            new EqBand("Grave / Corpo",   FilterType.LowShelf,  130f,  2.0f, 0.71f),
            new EqBand("Embolado",        FilterType.Peak,      320f, -3.0f, 1.10f),
            new EqBand("Nasal",           FilterType.Peak,     1000f, -1.0f, 1.40f),
            new EqBand("Presença",        FilterType.Peak,     3600f,  3.5f, 0.90f),
            new EqBand("Brilho / Ar",     FilterType.HighShelf,10000f, 3.0f, 0.71f),
        };

        // De-esser
        public bool DeessOn = true;
        public float DeessFreq = 6000f;
        public float DeessThresholdDb = -12f;
        public float DeessMaxDb = 6f;

        // Compressor
        public bool CompOn = true;
        public float CompThresholdDb = -26f;
        public float CompRatio = 3f;
        public float CompAttackMs = 8f;
        public float CompReleaseMs = 160f;
        public float CompKneeDb = 6f;
        public float CompMakeupDb = 4f;

        // Saida
        public float OutputGainDb = 2f;
        public float LimiterCeilingDb = -1f;
        public float LimiterReleaseMs = 80f;

        // Global
        public bool Bypass;

        // ------------------------------------------------------------------
        // Tabela de campos (para salvar/carregar e presets)
        // ------------------------------------------------------------------
        sealed class Field
        {
            public string Key; public Func<ChainParams, float> Get; public Action<ChainParams, float> Set;
            public Field(string k, Func<ChainParams, float> g, Action<ChainParams, float> s) { Key = k; Get = g; Set = s; }
        }

        static float B(bool b) => b ? 1f : 0f;
        static bool F(float f) => f >= 0.5f;

        static readonly List<Field> Fields = BuildFields();

        static List<Field> BuildFields()
        {
            var l = new List<Field>
            {
                new Field("InputGainDb", p => p.InputGainDb, (p, v) => p.InputGainDb = v),
                new Field("ChannelMode", p => p.ChannelMode, (p, v) => p.ChannelMode = (int)Math.Round(v)),
                new Field("HpfOn", p => B(p.HpfOn), (p, v) => p.HpfOn = F(v)),
                new Field("HpfFreq", p => p.HpfFreq, (p, v) => p.HpfFreq = v),
                new Field("ToneOn", p => B(p.ToneOn), (p, v) => p.ToneOn = F(v)),
                new Field("NoiseOn", p => B(p.NoiseOn), (p, v) => p.NoiseOn = F(v)),
                new Field("NoiseMix", p => p.NoiseMix, (p, v) => p.NoiseMix = v),
                new Field("NoiseEngine", p => p.NoiseEngine, (p, v) => p.NoiseEngine = (int)Math.Round(v)),
                new Field("NoiseExtra", p => B(p.NoiseExtra), (p, v) => p.NoiseExtra = F(v)),
                new Field("TuneOn", p => B(p.TuneOn), (p, v) => p.TuneOn = F(v)),
                new Field("TuneKey", p => p.TuneKey, (p, v) => p.TuneKey = (int)Math.Round(v)),
                new Field("TuneScale", p => p.TuneScale, (p, v) => p.TuneScale = (int)Math.Round(v)),
                new Field("TuneSpeed", p => p.TuneSpeed, (p, v) => p.TuneSpeed = v),
                new Field("TuneAmount", p => p.TuneAmount, (p, v) => p.TuneAmount = v),
                new Field("VcOn", p => B(p.VcOn), (p, v) => p.VcOn = F(v)),
                new Field("VcPitch", p => p.VcPitch, (p, v) => p.VcPitch = v),
                new Field("VcFormant", p => p.VcFormant, (p, v) => p.VcFormant = v),
                new Field("VcIntonation", p => p.VcIntonation, (p, v) => p.VcIntonation = v),
                new Field("VcVibDepth", p => p.VcVibDepth, (p, v) => p.VcVibDepth = v),
                new Field("VcVibRate", p => p.VcVibRate, (p, v) => p.VcVibRate = v),
                new Field("VcExtraMix", p => p.VcExtraMix, (p, v) => p.VcExtraMix = v),
                new Field("VcExtraPitch", p => p.VcExtraPitch, (p, v) => p.VcExtraPitch = v),
                new Field("VcExtraFormant", p => p.VcExtraFormant, (p, v) => p.VcExtraFormant = v),
                new Field("VcBreath", p => p.VcBreath, (p, v) => p.VcBreath = v),
                new Field("VcSynth", p => p.VcSynth, (p, v) => p.VcSynth = v),
                new Field("VcSynthHz", p => p.VcSynthHz, (p, v) => p.VcSynthHz = v),
                new Field("VcLowDb", p => p.VcLowDb, (p, v) => p.VcLowDb = v),
                new Field("VcHighDb", p => p.VcHighDb, (p, v) => p.VcHighDb = v),
                new Field("VcFilter", p => p.VcFilter, (p, v) => p.VcFilter = (int)Math.Round(v)),
                new Field("VcDrive", p => p.VcDrive, (p, v) => p.VcDrive = v),
                new Field("VcRingMix", p => p.VcRingMix, (p, v) => p.VcRingMix = v),
                new Field("VcRingHz", p => p.VcRingHz, (p, v) => p.VcRingHz = v),
                new Field("VcMetal", p => p.VcMetal, (p, v) => p.VcMetal = v),
                new Field("VcMetalHz", p => p.VcMetalHz, (p, v) => p.VcMetalHz = v),
                new Field("VcTremDepth", p => p.VcTremDepth, (p, v) => p.VcTremDepth = v),
                new Field("VcTremRate", p => p.VcTremRate, (p, v) => p.VcTremRate = v),
                new Field("VcEchoMix", p => p.VcEchoMix, (p, v) => p.VcEchoMix = v),
                new Field("VcEchoMs", p => p.VcEchoMs, (p, v) => p.VcEchoMs = v),
                new Field("VcEchoFb", p => p.VcEchoFb, (p, v) => p.VcEchoFb = v),
                new Field("VcReverbMix", p => p.VcReverbMix, (p, v) => p.VcReverbMix = v),
                new Field("VcReverbSize", p => p.VcReverbSize, (p, v) => p.VcReverbSize = v),
                new Field("VcAiOn", p => B(p.VcAiOn), (p, v) => p.VcAiOn = F(v)),
                new Field("VcAiModel", p => p.VcAiModel, (p, v) => p.VcAiModel = v),
                new Field("VcAiVoice", p => p.VcAiVoice, (p, v) => p.VcAiVoice = (int)Math.Round(v)),
                new Field("VcAiPitch", p => p.VcAiPitch, (p, v) => p.VcAiPitch = v),
                new Field("VcAiFormant", p => p.VcAiFormant, (p, v) => p.VcAiFormant = v),
                new Field("VcAiInton", p => p.VcAiInton, (p, v) => p.VcAiInton = v),
                new Field("GateOn", p => B(p.GateOn), (p, v) => p.GateOn = F(v)),
                new Field("GateThresholdDb", p => p.GateThresholdDb, (p, v) => p.GateThresholdDb = v),
                new Field("GateRangeDb", p => p.GateRangeDb, (p, v) => p.GateRangeDb = v),
                new Field("GateHoldMs", p => p.GateHoldMs, (p, v) => p.GateHoldMs = v),
                new Field("GateReleaseMs", p => p.GateReleaseMs, (p, v) => p.GateReleaseMs = v),
                new Field("GateUseVad", p => B(p.GateUseVad), (p, v) => p.GateUseVad = F(v)),
                new Field("GateVadThreshold", p => p.GateVadThreshold, (p, v) => p.GateVadThreshold = v),
                new Field("AgcOn", p => B(p.AgcOn), (p, v) => p.AgcOn = F(v)),
                new Field("AgcTargetDb", p => p.AgcTargetDb, (p, v) => p.AgcTargetDb = v),
                new Field("AgcMaxBoostDb", p => p.AgcMaxBoostDb, (p, v) => p.AgcMaxBoostDb = v),
                new Field("AgcMaxCutDb", p => p.AgcMaxCutDb, (p, v) => p.AgcMaxCutDb = v),
                new Field("AgcSpeed", p => p.AgcSpeed, (p, v) => p.AgcSpeed = v),
                new Field("EqOn", p => B(p.EqOn), (p, v) => p.EqOn = F(v)),
                new Field("DeessOn", p => B(p.DeessOn), (p, v) => p.DeessOn = F(v)),
                new Field("DeessFreq", p => p.DeessFreq, (p, v) => p.DeessFreq = v),
                new Field("DeessThresholdDb", p => p.DeessThresholdDb, (p, v) => p.DeessThresholdDb = v),
                new Field("DeessMaxDb", p => p.DeessMaxDb, (p, v) => p.DeessMaxDb = v),
                new Field("CompOn", p => B(p.CompOn), (p, v) => p.CompOn = F(v)),
                new Field("CompThresholdDb", p => p.CompThresholdDb, (p, v) => p.CompThresholdDb = v),
                new Field("CompRatio", p => p.CompRatio, (p, v) => p.CompRatio = v),
                new Field("CompAttackMs", p => p.CompAttackMs, (p, v) => p.CompAttackMs = v),
                new Field("CompReleaseMs", p => p.CompReleaseMs, (p, v) => p.CompReleaseMs = v),
                new Field("CompKneeDb", p => p.CompKneeDb, (p, v) => p.CompKneeDb = v),
                new Field("CompMakeupDb", p => p.CompMakeupDb, (p, v) => p.CompMakeupDb = v),
                new Field("OutputGainDb", p => p.OutputGainDb, (p, v) => p.OutputGainDb = v),
                new Field("LimiterCeilingDb", p => p.LimiterCeilingDb, (p, v) => p.LimiterCeilingDb = v),
                new Field("LimiterReleaseMs", p => p.LimiterReleaseMs, (p, v) => p.LimiterReleaseMs = v),
            };
            for (int i = 0; i < 5; i++)
            {
                int b = i;
                l.Add(new Field($"Eq{b + 1}On", p => B(p.Bands[b].On), (p, v) => p.Bands[b].On = F(v)));
                l.Add(new Field($"Eq{b + 1}Freq", p => p.Bands[b].Freq, (p, v) => p.Bands[b].Freq = v));
                l.Add(new Field($"Eq{b + 1}Gain", p => p.Bands[b].GainDb, (p, v) => p.Bands[b].GainDb = v));
                l.Add(new Field($"Eq{b + 1}Q", p => p.Bands[b].Q, (p, v) => p.Bands[b].Q = v));
            }
            return l;
        }

        /// <summary>Exporta os parametros como pares chave/valor (cultura invariavel).</summary>
        public Dictionary<string, string> ToDictionary()
        {
            var d = new Dictionary<string, string>();
            foreach (var f in Fields) d[f.Key] = f.Get(this).ToString("R", CultureInfo.InvariantCulture);
            return d;
        }

        /// <summary>Carrega valores conhecidos; chaves ausentes mantem o valor atual.</summary>
        public void FromDictionary(IDictionary<string, string> d)
        {
            foreach (var f in Fields)
            {
                if (d.TryGetValue(f.Key, out var s) &&
                    float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
                    !float.IsNaN(v) && !float.IsInfinity(v))
                    f.Set(this, v);
            }
            Sanitize();
            Touch();
        }

        public void CopyFrom(ChainParams other)
        {
            foreach (var f in Fields) f.Set(this, f.Get(other));
            Sanitize();
            Touch();
        }

        static bool IsVoiceKey(string k) => k.StartsWith("Vc", StringComparison.Ordinal);

        /// <summary>Copia so o modificador de voz (VcOn e todos os ajustes Vc*).</summary>
        public void CopyVoiceFrom(ChainParams other)
        {
            foreach (var f in Fields) if (IsVoiceKey(f.Key)) f.Set(this, f.Get(other));
        }

        /// <summary>Os ajustes do modificador (sem contar o liga/desliga) sao iguais aos de outro?</summary>
        public bool SameVoice(ChainParams other)
        {
            foreach (var f in Fields)
                if (IsVoiceKey(f.Key) && f.Key != "VcOn" && Math.Abs(f.Get(this) - f.Get(other)) > 0.01f) return false;
            return true;
        }

        /// <summary>Ajustes do modificador como texto (presets de voz do usuario).</summary>
        public Dictionary<string, string> VoiceToDictionary()
        {
            var d = new Dictionary<string, string>();
            foreach (var f in Fields) if (IsVoiceKey(f.Key) && f.Key != "VcOn") d[f.Key] = f.Get(this).ToString("R", CultureInfo.InvariantCulture);
            return d;
        }

        public ChainParams Clone()
        {
            var c = new ChainParams();
            c.CopyFrom(this);
            c.Bypass = Bypass;
            return c;
        }

        /// <summary>Garante que todos os valores estao em faixas seguras.</summary>
        public void Sanitize()
        {
            InputGainDb = DspMath.Clamp(InputGainDb, -12f, 40f);
            if (ChannelMode < 0 || ChannelMode > 3) ChannelMode = 0;
            HpfFreq = DspMath.Clamp(HpfFreq, 20f, 300f);
            NoiseMix = DspMath.Clamp(NoiseMix, 0f, 1f);
            if (NoiseEngine < 0 || NoiseEngine > 2) NoiseEngine = 0;
            if (TuneKey < 0 || TuneKey > 11) TuneKey = 0;
            if (TuneScale < 0 || TuneScale >= AutoTune.ScaleNames.Length) TuneScale = 0;
            TuneSpeed = DspMath.Clamp(TuneSpeed, 0f, 100f);
            TuneAmount = DspMath.Clamp(TuneAmount, 0f, 100f);
            VcPitch = DspMath.Clamp(VcPitch, -12f, 12f);
            VcFormant = DspMath.Clamp(VcFormant, -35f, 60f);
            VcIntonation = DspMath.Clamp(VcIntonation, 0f, 200f);
            VcVibDepth = DspMath.Clamp(VcVibDepth, 0f, 150f);
            VcVibRate = DspMath.Clamp(VcVibRate, 2f, 12f);
            VcExtraMix = DspMath.Clamp(VcExtraMix, 0f, 100f);
            VcExtraPitch = DspMath.Clamp(VcExtraPitch, -24f, 24f);
            VcExtraFormant = DspMath.Clamp(VcExtraFormant, -35f, 60f);
            VcBreath = DspMath.Clamp(VcBreath, 0f, 100f);
            VcSynth = DspMath.Clamp(VcSynth, 0f, 100f);
            VcSynthHz = DspMath.Clamp(VcSynthHz, 50f, 400f);
            VcLowDb = DspMath.Clamp(VcLowDb, -12f, 12f);
            VcHighDb = DspMath.Clamp(VcHighDb, -12f, 12f);
            if (VcFilter < 0 || VcFilter >= VoiceFx.FilterNames.Length) VcFilter = 0;
            VcDrive = DspMath.Clamp(VcDrive, 0f, 100f);
            VcRingMix = DspMath.Clamp(VcRingMix, 0f, 100f);
            VcRingHz = DspMath.Clamp(VcRingHz, 20f, 1000f);
            VcMetal = DspMath.Clamp(VcMetal, 0f, 100f);
            VcMetalHz = DspMath.Clamp(VcMetalHz, 50f, 500f);
            VcTremDepth = DspMath.Clamp(VcTremDepth, 0f, 100f);
            VcTremRate = DspMath.Clamp(VcTremRate, 1f, 20f);
            VcEchoMix = DspMath.Clamp(VcEchoMix, 0f, 100f);
            VcEchoMs = DspMath.Clamp(VcEchoMs, 40f, 1000f);
            VcEchoFb = DspMath.Clamp(VcEchoFb, 0f, 85f);
            VcReverbMix = DspMath.Clamp(VcReverbMix, 0f, 100f);
            VcReverbSize = DspMath.Clamp(VcReverbSize, 0f, 100f);
            VcAiModel = (float)Math.Round(DspMath.Clamp(VcAiModel, 0f, 65535f));
            if (VcAiVoice < 0 || VcAiVoice > 255) VcAiVoice = 0;
            VcAiPitch = DspMath.Clamp(VcAiPitch, -12f, 12f);
            VcAiFormant = (float)(Math.Round(DspMath.Clamp(VcAiFormant, -2f, 2f) * 2) / 2);
            VcAiInton = DspMath.Clamp(VcAiInton, 0f, 200f);
            GateThresholdDb = DspMath.Clamp(GateThresholdDb, -80f, -10f);
            GateRangeDb = DspMath.Clamp(GateRangeDb, -80f, 0f);
            GateHoldMs = DspMath.Clamp(GateHoldMs, 0f, 1000f);
            GateReleaseMs = DspMath.Clamp(GateReleaseMs, 10f, 1000f);
            GateVadThreshold = DspMath.Clamp(GateVadThreshold, 0.05f, 0.95f);
            AgcTargetDb = DspMath.Clamp(AgcTargetDb, -36f, -8f);
            AgcMaxBoostDb = DspMath.Clamp(AgcMaxBoostDb, 0f, 24f);
            AgcMaxCutDb = DspMath.Clamp(AgcMaxCutDb, 0f, 24f);
            AgcSpeed = DspMath.Clamp(AgcSpeed, 0.5f, 20f);
            foreach (var b in Bands)
            {
                b.Freq = DspMath.Clamp(b.Freq, 20f, 20000f);
                b.GainDb = DspMath.Clamp(b.GainDb, -18f, 18f);
                b.Q = DspMath.Clamp(b.Q, 0.2f, 8f);
            }
            DeessFreq = DspMath.Clamp(DeessFreq, 3000f, 12000f);
            DeessThresholdDb = DspMath.Clamp(DeessThresholdDb, -30f, 0f);
            DeessMaxDb = DspMath.Clamp(DeessMaxDb, 0f, 18f);
            CompThresholdDb = DspMath.Clamp(CompThresholdDb, -60f, 0f);
            CompRatio = DspMath.Clamp(CompRatio, 1f, 20f);
            CompAttackMs = DspMath.Clamp(CompAttackMs, 0.5f, 100f);
            CompReleaseMs = DspMath.Clamp(CompReleaseMs, 20f, 1000f);
            CompKneeDb = DspMath.Clamp(CompKneeDb, 0f, 20f);
            CompMakeupDb = DspMath.Clamp(CompMakeupDb, -12f, 24f);
            OutputGainDb = DspMath.Clamp(OutputGainDb, -24f, 24f);
            LimiterCeilingDb = DspMath.Clamp(LimiterCeilingDb, -12f, 0f);
            LimiterReleaseMs = DspMath.Clamp(LimiterReleaseMs, 10f, 500f);
        }

        /// <summary>Resposta total do EQ em dB (para o grafico). Usa filtros proprios, nao toca na thread de audio.</summary>
        public double EqResponseDb(double freq, Biquad scratch)
        {
            if (!EqOn) return 0;
            double sum = 0;
            foreach (var b in Bands)
            {
                if (!b.On) continue;
                scratch.Set(b.Type, 48000, b.Freq, b.Q, b.GainDb);
                sum += scratch.MagnitudeDb(freq, 48000);
            }
            return sum;
        }
    }
}
