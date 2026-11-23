using System;
using System.Threading.Tasks;

namespace TakaVox.Dsp
{
    /// <summary>
    /// Cadeia completa de estudio para voz (mono, 48 kHz, blocos de 10 ms):
    /// Ganho -> Filtro de graves -> Removedor de ventoinha -> IA anti-ruido -> Portao de voz -> Tom (Auto-Tune + modificador de voz, ou a voz com IA)
    /// -> Textura do modificador -> Nivelador -> EQ -> De-esser -> Compressor -> Efeitos do modificador -> Plugins VST -> Volume -> Limitador.
    /// </summary>
    public sealed class VoiceChain : IDisposable
    {
        public const int Block = 480;
        public const float SampleRate = 48000f;
        public const int GateLookahead = 96;   // a deteccao de voz da IA ja enxerga a frente
        public const int LimiterLookahead = 64;

        public static readonly string[] EngineNames = { "DeepFilterNet3 – limpeza máxima", "DeepFilterNet3 LL – tempo real (recomendado)", "RNNoise – leve" };
        public static readonly string[] EngineHints =
        {
            "Remove tudo nas pausas, mas com teclado durante a fala pode abaixar um pouco a voz. Atraso ~33 ms.",
            "Melhor equilíbrio: tira teclado, mouse e ventoinha sem mexer na voz. Atraso ~13 ms. Usa ~20% de 1 núcleo.",
            "Bem leve (1% de CPU), mas deixa passar cliques de teclado e mouse.",
        };

        readonly ChainParams p;
        readonly bool offline;
        int lastVersion = int.MinValue;

        readonly Biquad hp1 = new Biquad(), hp2 = new Biquad();
        readonly Biquad[] eq = { new Biquad(), new Biquad(), new Biquad(), new Biquad(), new Biquad() };
        RnnoiseDenoiser rn;
        DeepFilterDenoiser df;
        volatile DeepFilterDenoiser pendingDf;
        volatile bool creating;
        int activeEngine = -1, failedEngine = -1;
        readonly VoiceGate gate;
        readonly ToneRemover tones;
        readonly float[] toneIn = new float[Block];
        readonly AutoTune tune = new AutoTune();
        readonly VoiceFx vfx = new VoiceFx();
        readonly Leveler agc;
        readonly DeEsser deess;
        readonly Compressor comp;
        readonly Limiter lim;

        float inGainPrev = -1f, outGainPrev = -1f;
        bool hpfOn, eqOn;
        readonly bool[] bandOn = new bool[5];
        float vadSmooth;

        // Medidores (ultimos valores)
        public float Vad { get; private set; }
        public float SnrDb { get; private set; } = float.NaN;
        public float GateGainDb => gate.GainDb;
        public bool GateOpen => gate.IsOpen;
        public float AgcGainDb => agc.GainDb;
        public float DeessGrDb => deess.GrDb;
        public float CompGrDb => comp.GrDb;
        public float LimGrDb => lim.GrDb;
        public AutoTune Tune => tune;
        /// <summary>Cortes que o removedor de ventoinha esta usando agora (vazio = nenhum ruido tonal).</summary>
        public ToneRemover.Notch[] ToneNotches => tones.Active;
        public int ActiveEngine => activeEngine;
        public bool AiAvailable => (activeEngine == 2 && rn != null && rn.Ok) || (activeEngine != 2 && df != null && df.Ok);
        public string EngineError { get; private set; } = "";

        int DenoiserLatency => activeEngine == 2 || df == null ? RnnoiseDenoiser.Latency : df.Latency;

        /// <summary>Plugins VST do usuario (entram depois do compressor, antes do volume final e do limitador).</summary>
        public TakaVox.Plugins.PluginChain Plugins;

        /// <summary>Voz com IA (Beatrice). Null ou fora do ar = motor de tom do TakaVox.</summary>
        public TakaVox.Plugins.AiVoice Ai;
        bool aiActive;
        readonly float[] aiBuf = new float[Block];
        /// <summary>O ultimo bloco saiu pela voz com IA.</summary>
        public bool AiActive => aiActive;

        int ToneLatency => aiActive ? TakaVox.Plugins.AiVoice.Latency : (AutoTune.Needed(p) ? AutoTune.Delay : 0);

        /// <summary>Atraso total da cadeia em amostras (para alinhar o sinal original no A/B).</summary>
        public int LatencySamples => DenoiserLatency + gate.LatencySamples + ToneLatency + (Plugins?.LatencySamples ?? 0) + lim.LatencySamples;

        public VoiceChain(ChainParams parameters, bool offline = false)
        {
            p = parameters;
            this.offline = offline;
            gate = new VoiceGate(SampleRate, GateLookahead);
            tones = new ToneRemover(offline); // ao vivo: analise numa thread propria; offline: na hora
            agc = new Leveler(SampleRate);
            deess = new DeEsser(SampleRate);
            comp = new Compressor(SampleRate);
            lim = new Limiter(SampleRate, LimiterLookahead);
            EnsureEngine(true);
        }

        public string EngineName
        {
            get
            {
                if (activeEngine < 0) return "iniciando…";
                string n = EngineNames[activeEngine];
                if (failedEngine >= 0 && activeEngine == 2) n += " – DeepFilterNet indisponível";
                return n;
            }
        }

        /// <summary>Garante que o motor de IA pedido esta pronto (carrega em segundo plano no ao vivo).</summary>
        void EnsureEngine(bool sync)
        {
            var pend = pendingDf;
            if (pend != null)
            {
                pendingDf = null;
                var old = df;
                df = pend;
                activeEngine = pend.LowLatency ? 1 : 0;
                if (old != null) Task.Run(() => old.Dispose());
            }
            int want = p.NoiseEngine;
            if (want == failedEngine) want = 2;
            if (want == activeEngine) return;
            if (want == 2)
            {
                if (rn == null) rn = new RnnoiseDenoiser();
                activeEngine = 2;
                return;
            }
            bool ll = want == 1;
            if (df != null && df.LowLatency == ll) { activeEngine = want; return; }
            if (sync)
            {
                var d = new DeepFilterDenoiser(ll);
                if (d.Ok) { var old = df; df = d; activeEngine = want; old?.Dispose(); }
                else
                {
                    d.Dispose();
                    failedEngine = want;
                    EngineError = DeepFilterLib.Error;
                    if (rn == null) rn = new RnnoiseDenoiser();
                    activeEngine = 2;
                    Log.Write("DeepFilterNet indisponível (" + EngineError + "): usando RNNoise.");
                }
                return;
            }
            if (creating) return;
            creating = true;
            int w = want;
            Task.Run(() =>
            {
                try
                {
                    var d = new DeepFilterDenoiser(ll);
                    if (d.Ok) pendingDf = d;
                    else { d.Dispose(); failedEngine = w; EngineError = DeepFilterLib.Error; }
                }
                catch (Exception ex) { failedEngine = w; EngineError = ex.Message; }
                finally { creating = false; }
            });
            // enquanto carrega, continua com o motor atual (ou RNNoise se nao houver nenhum)
            if (activeEngine < 0) { if (rn == null) rn = new RnnoiseDenoiser(); activeEngine = 2; }
        }

        void UpdateCoefficients()
        {
            lastVersion = p.Version;
            if (p.HpfOn && !hpfOn) { hp1.Reset(); hp2.Reset(); }
            hpfOn = p.HpfOn;
            // Butterworth de 4a ordem (24 dB/oitava)
            hp1.Set(FilterType.HighPass, SampleRate, p.HpfFreq, 0.54119610, 0);
            hp2.Set(FilterType.HighPass, SampleRate, p.HpfFreq, 1.30656296, 0);
            bool eqWasOn = eqOn;
            eqOn = p.EqOn;
            for (int i = 0; i < 5; i++)
            {
                var b = p.Bands[i];
                bool on = b.On && Math.Abs(b.GainDb) > 0.01f;
                if (on && (!bandOn[i] || !eqWasOn)) eq[i].Reset(); // evita estalo com estado antigo
                bandOn[i] = on;
                eq[i].Set(b.Type, SampleRate, b.Freq, b.Q, b.GainDb);
            }
        }

        /// <summary>Intensidade (0..1) -> limite de atenuacao do DeepFilterNet em dB.</summary>
        public static float AttenFromStrength(float s)
        {
            float[] xs = { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
            float[] ys = { 0f, 10f, 20f, 40f, 70f, 100f };
            s = DspMath.Clamp(s, 0f, 1f);
            for (int i = 1; i < xs.Length; i++)
                if (s <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (s - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return 100f;
        }

        /// <summary>SNR local da IA (dB) -> probabilidade de voz (0..1). Calibrado por modelo
        /// (medido: pausas com teclado ficam abaixo de -7 dB no padrao e de +6 dB no LL).</summary>
        static float SnrToVad(float snr, bool lowLatency) =>
            lowLatency ? DspMath.Clamp((snr - 2f) / 10f, 0f, 1f) : DspMath.Clamp((snr + 2f) / 12f, 0f, 1f);

        /// <summary>Processa exatamente 480 amostras no lugar.</summary>
        public void Process(float[] x) => Run(x, false);

        /// <summary>
        /// Volta do descanso: roda so a parte que tem memoria longa (removedor de ventoinha, IA anti-ruido, portao) num
        /// bloco do audio que ja passou, sem saida. Assim a primeira palavra depois do descanso sai igual as outras.
        /// </summary>
        public void WarmUp(float[] x) => Run(x, true);

        void Run(float[] x, bool warmOnly)
        {
            if (p.Version != lastVersion) UpdateCoefficients();
            EnsureEngine(offline);
            const int n = Block;

            // 1) Ganho de entrada (com rampa para nao estalar)
            float g = DspMath.DbToLin(p.InputGainDb);
            if (inGainPrev < 0) inGainPrev = g;
            float dg = (g - inGainPrev) / n;
            for (int i = 0; i < n; i++) x[i] *= inGainPrev + dg * i;
            inGainPrev = g;

            // 2) Filtro de graves (tira ronco, batida na mesa, "pop")
            if (hpfOn)
                for (int i = 0; i < n; i++) x[i] = hp2.Process(hp1.Process(x[i]));

            // 2b) Removedor de ventoinha/zumbido: cortes estreitos nos "apitos" fixos do ruido, antes da IA
            Array.Copy(x, toneIn, n);
            tones.Apply(x, n, p.ToneOn);

            // 3) IA anti-ruido (sempre roda para manter a latencia constante; intensidade 0 = desligada)
            float vad; bool vadOk;
            if (activeEngine == 2 || df == null)
            {
                if (rn == null) rn = new RnnoiseDenoiser();
                float v = rn.Process(x, p.NoiseOn ? p.NoiseMix : 0f);
                vadOk = rn.Ok;
                vad = vadOk ? v : 1f;
                SnrDb = float.NaN;
            }
            else
            {
                float atten = p.NoiseOn ? AttenFromStrength(p.NoiseMix) : 0f;
                float snr = df.Process(x, atten, p.NoiseOn && p.NoiseExtra ? 0.02f : 0f);
                vadOk = !float.IsNaN(snr);
                SnrDb = snr;
                float raw = vadOk ? SnrToVad(snr, df.LowLatency) : 1f;
                // sobe rapido, desce suave (nao corta o fim das palavras)
                vadSmooth = raw > vadSmooth ? raw : vadSmooth * 0.85f + raw * 0.15f;
                vad = vadSmooth;
            }
            Vad = vadOk ? vad : 1f;
            // so blocos SEM fala ensinam o removedor de ventoinha (a voz nunca vira "ruido")
            tones.Feed(toneIn, n, vadOk && vad < 0.25f);

            // 4) Portao de voz (so abre com VOZ detectada pela IA: teclado/mouse ficam de fora)
            gate.Process(x, n, p, vad, vadOk);
            if (warmOnly) return;

            // 5) Motor de tom: a voz com IA (Beatrice) OU o Auto-Tune + modificador de voz do TakaVox
            //    (tom, timbre, entonacao, vibrato, voz extra; 5b: sopro/sussurro, robo sintetizado, graves/brilho).
            //    Na troca de um para o outro os dois rodam neste bloco e o som cruza em 10 ms (sem estalo).
            var ai = Ai;
            bool useAi = ai != null && p.VcOn && p.VcAiOn && ai.Ready;
            if (useAi == aiActive)
            {
                if (!useAi) ToneEngine(x, n);
                else if (!ai.Process(x, n)) { aiActive = false; ToneEngine(x, n); } // a IA falhou: x voltou como entrou
            }
            else
            {
                Array.Copy(x, aiBuf, n);
                bool aiOk = ai != null && ai.Process(aiBuf, n);
                ToneEngine(x, n);
                if (aiOk)
                    for (int i = 0; i < n; i++)
                    {
                        float w = (i + 0.5f) / n;
                        if (!useAi) w = 1f - w; // saindo da IA: comeca nela e termina no motor normal
                        x[i] = x[i] * (1f - w) + aiBuf[i] * w;
                    }
                aiActive = useAi && aiOk;
                if (aiActive) tune.Clear(); // o motor de tom para: esquece o audio guardado
            }

            // 6) Nivelador automatico (so aprende enquanto voce fala)
            bool speech = gate.IsOpen && (!vadOk || vad > 0.5f);
            agc.Process(x, n, p, speech);

            // 7) Equalizador
            if (eqOn)
                for (int b = 0; b < 5; b++)
                    if (bandOn[b]) eq[b].Process(x, n);

            // 8) De-esser
            deess.Process(x, n, p);

            // 9) Compressor
            comp.Process(x, n, p);

            // 9a) Modificador de voz: distorcao, telefone/radio/megafone, anel, metalico, tremolo, eco, reverb
            vfx.Post(x, n, p);

            // 9b) Plugins VST do usuario (o limitador logo abaixo continua protegendo contra estouro)
            Plugins?.Process(x, n);

            // 10) Volume final
            float og = DspMath.DbToLin(p.OutputGainDb);
            if (outGainPrev < 0) outGainPrev = og;
            float dog = (og - outGainPrev) / n;
            for (int i = 0; i < n; i++) x[i] *= outGainPrev + dog * i;
            outGainPrev = og;

            // 11) Limitador (nunca estoura)
            lim.Process(x, n, p.LimiterCeilingDb, p.LimiterReleaseMs);
        }

        void ToneEngine(float[] x, int n)
        {
            tune.Process(x, n, p);
            vfx.Pre(x, n, p);
        }

        public void Dispose()
        {
            tones.Dispose();
            rn?.Dispose();
            df?.Dispose();
            pendingDf?.Dispose();
        }
    }
}
