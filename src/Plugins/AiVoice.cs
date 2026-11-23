using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using TakaVox.Dsp;

namespace TakaVox.Plugins
{
    /// <summary>
    /// Voz com IA ao vivo: o Beatrice 2 (VST3) na cadeia de audio, no lugar do motor de tom do TakaVox.
    /// - A interface chama <see cref="Update"/> (a cada mudanca e algumas vezes por segundo): carrega o plugin e o modelo
    ///   em segundo plano, troca de modelo, aplica tom/timbre/entonacao e casa o tom medio da sua voz com o da voz alvo.
    /// - A thread de audio chama <see cref="Process"/>, que so troca blocos com a thread da IA (outro nucleo): a IA tem
    ///   10 ms de folga para cada bloco, entao um bloco mais lento de vez em quando nao vira estalo.
    /// - Se a IA travar, soltar NaN ou ficar muda com voce falando, ela sai sozinha e a sua voz normal volta:
    ///   o microfone nunca fica mudo por causa da IA.
    /// </summary>
    public sealed class AiVoice
    {
        const int Block = 480, Slots = 8;
        /// <summary>Atraso da IA em amostras: ~37 ms do Beatrice + 10 ms de folga da thread dela.</summary>
        public const int Latency = 1776 + Block;
        /// <summary>A IA sai ~7 dB mais baixa que a entrada: compensa na saida do plugin.</summary>
        public const double OutGainDb = 6;

        [System.Runtime.InteropServices.DllImport("avrt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
        [System.Runtime.InteropServices.DllImport("avrt.dll")] static extern bool AvSetMmThreadPriority(IntPtr h, int prio);

        // ================================================================ thread de audio <-> thread da IA
        IntPtr live;                 // instancia em uso (Zero = nenhuma)
        int gen;                     // geracao: muda a cada vez que a IA entra no audio (descarta blocos velhos)
        long dspSeq, workSeq;        // impar = thread de audio / da IA dentro da parte que usa a instancia
        volatile int failCode;       // 0 ok; 1 travou; 2 ficou muda; 3 soltou lixo (NaN)
        readonly float[][] inQ = new float[Slots][], outQ = new float[Slots][];
        readonly int[] inGen = new int[Slots], outGen = new int[Slots];
        int inW, inR, outW, outR;    // inW/outR: thread de audio; inR/outW: thread da IA
        int dspGen = -1, pushed;
        readonly AutoResetEvent work = new AutoResetEvent(false), done = new AutoResetEvent(false);
        Thread worker;
        volatile bool stop;
        int lastProcessTick;
        /// <summary>Blocos em que a IA atrasou mais que a folga (saiu silencio) e blocos pulados para recuperar a folga.</summary>
        public long Late, Skipped;
        /// <summary>Vezes que a thread de audio esperou a IA terminar o bloco (e o tempo total esperado).</summary>
        public long Waits, WaitTicks;

        // so da thread da IA
        readonly float[] backup = new float[Block];
        readonly PitchMeter meter = new PitchMeter();
        int silentSpeech, nanBlocks, workGen = -1;
        long cpuTicks, cpuBlocks;
        public double MaxMs;

        public AiVoice()
        {
            for (int i = 0; i < Slots; i++) { inQ[i] = new float[Block]; outQ[i] = new float[Block]; }
        }

        /// <summary>Pronta para processar (carregada, ligada e sem falha).</summary>
        public bool Ready => Volatile.Read(ref live) != IntPtr.Zero && failCode == 0;

        /// <summary>
        /// Thread de audio: entrega o bloco para a IA e devolve a voz nova (um bloco atras).
        /// Falso = nao processou (x volta como entrou e quem chamou usa o motor normal).
        /// </summary>
        public bool Process(float[] x, int n)
        {
            Interlocked.Increment(ref dspSeq); // entrando (antes de ler 'live': a interface depende dessa ordem)
            try
            {
                if (n != Block || Volatile.Read(ref live) == IntPtr.Zero || failCode != 0) return false;
                lastProcessTick = Environment.TickCount;
                int g = Volatile.Read(ref gen);
                if (g != dspGen) { dspGen = g; pushed = 0; }

                // 1) entrada para a IA
                if (inW - Volatile.Read(ref inR) < Slots)
                {
                    int s = inW % Slots;
                    Array.Copy(x, inQ[s], Block);
                    inGen[s] = g;
                    Volatile.Write(ref inW, inW + 1);
                    pushed++;
                    work.Set();
                }

                // 2) saida da IA: o bloco anterior (10 ms de folga). Espera ate 4 ms se ela atrasou um pouco.
                if (pushed < 2) { Array.Clear(x, 0, n); return true; } // primeiro bloco desta vez: a folga
                long t0 = Stopwatch.GetTimestamp();
                long deadline = t0 + Stopwatch.Frequency * 4 / 1000;
                bool waited = false;
                while (true)
                {
                    int w = Volatile.Read(ref outW);
                    while (outR < w && outGen[outR % Slots] != g) outR++; // restos de quando a IA estava desligada
                    if (w - outR >= 1)
                    {
                        if (w - outR >= 2) { Skipped += w - outR - 1; outR = w - 1; } // atrasou antes: volta para 1 bloco de folga
                        Array.Copy(outQ[outR % Slots], x, Block);
                        outR++;
                        if (waited) { Waits++; WaitTicks += Stopwatch.GetTimestamp() - t0; }
                        return true;
                    }
                    long left = deadline - Stopwatch.GetTimestamp();
                    if (left <= 0) { Late++; Waits++; WaitTicks += Stopwatch.GetTimestamp() - t0; Array.Clear(x, 0, n); return true; }
                    waited = true;
                    done.WaitOne((int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
                }
            }
            finally { Interlocked.Increment(ref dspSeq); }
        }

        void StartWorker()
        {
            if (worker != null) return;
            stop = false;
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "TakaVox voz com IA", Priority = ThreadPriority.Highest };
            worker.Start();
        }

        void WorkerLoop()
        {
            uint task = 0;
            IntPtr avrt = IntPtr.Zero;
            try { avrt = AvSetMmThreadCharacteristicsW("Pro Audio", ref task); if (avrt != IntPtr.Zero) AvSetMmThreadPriority(avrt, 1); } catch { }
            TakaVox.Audio.PowerHints.DisableThreadThrottling();
            TakaVox.Audio.PowerHints.PreferQuietCore(1);
            while (!stop)
            {
                work.WaitOne(200);
                while (!stop && Volatile.Read(ref inW) - inR > 0)
                {
                    int s = inR % Slots;
                    int o = outW % Slots;
                    RunBlock(inQ[s], outQ[o]);
                    outGen[o] = inGen[s];
                    Volatile.Write(ref inR, inR + 1);
                    if (outW - Volatile.Read(ref outR) < Slots) Volatile.Write(ref outW, outW + 1); // cheio = a thread de audio parou de ler
                    done.Set();
                }
            }
        }

        /// <summary>Thread da IA: um bloco pelo Beatrice, com as protecoes.</summary>
        unsafe void RunBlock(float[] src, float[] dst)
        {
            Interlocked.Increment(ref workSeq);
            try
            {
                IntPtr h = Volatile.Read(ref live);
                if (h == IntPtr.Zero || failCode != 0) { Array.Copy(src, dst, Block); return; }
                int g = Volatile.Read(ref gen);
                if (g != workGen) { workGen = g; silentSpeech = 0; nanBlocks = 0; }
                double ein = 0;
                for (int i = 0; i < Block; i++) ein += src[i] * src[i];
                meter.Feed(src, Block);
                Array.Copy(src, backup, Block);
                Array.Copy(src, dst, Block);
                long t0 = Stopwatch.GetTimestamp();
                int r;
                fixed (float* p = dst) r = Tvh.tvh_process(h, p, Block);
                long dt = Stopwatch.GetTimestamp() - t0;
                cpuTicks += dt;
                cpuBlocks++;
                double ms = dt * 1000.0 / Stopwatch.Frequency;
                if (ms > MaxMs) MaxMs = ms;
                if (r != 0)
                {
                    Array.Copy(backup, dst, Block);
                    if (r == -2) failCode = 1;
                    return;
                }
                double eout = 0;
                for (int i = 0; i < Block; i++)
                {
                    float v = dst[i];
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        Array.Copy(backup, dst, Block);
                        if (++nanBlocks > 20) failCode = 3;
                        return;
                    }
                    eout += v * v;
                }
                // voce falando (> -45 dBFS) e a IA soltando silencio absoluto por 1,5 s: modelo nao carregou ou a IA parou
                if (ein > Block * 3e-5 && eout < Block * 1e-12) { if (++silentSpeech > 150) failCode = 2; }
                else if (eout >= Block * 1e-12) silentSpeech = 0;
            }
            finally { Interlocked.Increment(ref workSeq); }
        }

        // ================================================================ interface
        IntPtr handle;                         // instancia carregada (pode estar fora do audio)
        volatile IntPtr loadedHandle;          // resultado da carga em segundo plano
        volatile string loadError;
        volatile bool loading;
        string loadingToml, modelToml;
        bool failShown;
        DateTime offSince = DateTime.MaxValue;
        readonly Dictionary<int, double> sent = new Dictionary<int, double>();
        long lastCpuTicks, lastCpuBlocks;

        public BeatriceModel Model { get; private set; }
        public BeatriceVoice Voice { get; private set; }
        /// <summary>"desligada", "carregando", "pronta", "pausada", "parou", "sem modelo", "sem plugin", "erro".</summary>
        public string State { get; private set; } = "desligada";
        public string Error { get; private set; }
        public long LoadMs { get; private set; }
        /// <summary>Tom medio de quem fala (nota MIDI) usado para casar com a voz alvo. A interface guarda nos ajustes.</summary>
        public float SourceMidi { get; set; } = 48f;
        /// <summary>O tom medio mudou desde a ultima vez que a interface salvou.</summary>
        public bool SourceChanged;
        /// <summary>Tom de saida aplicado agora (st).</summary>
        public double PitchShift { get; private set; }
        /// <summary>A IA parou sozinha (motivo). Chamado na thread da interface.</summary>
        public event Action<string> Failed;

        /// <summary>Quadros de voz que o medidor de tom ja ouviu nesta sessao.</summary>
        public int MeterFrames => meter.Frames;
        public float MeterMedian => meter.Median;

        static string FailText(int code)
        {
            switch (code)
            {
                case 1: return "a IA travou";
                case 2: return "a IA ficou muda (o modelo de voz não carregou?)";
                case 3: return "a IA soltou um som inválido";
            }
            return "";
        }

        /// <summary>Interface: liga/desliga, carrega/troca o modelo e aplica os ajustes. Barato quando nada mudou.</summary>
        public void Update(ChainParams p, IList<BeatriceModel> models, string pluginPath)
        {
            // resultado da carga em segundo plano
            var lh = loadedHandle;
            if (lh != IntPtr.Zero)
            {
                loadedHandle = IntPtr.Zero;
                handle = lh;
                modelToml = loadingToml;
                sent.Clear();
            }
            var le = loadError;
            if (le != null) { loadError = null; Error = le; State = "erro"; Log.Write("Voz com IA: " + le); }

            int fc = failCode;
            if (fc != 0 && !failShown)
            {
                failShown = true;
                Error = FailText(fc);
                Unpublish();
                Log.Write("Voz com IA parou: " + Error + ". Voltou para a voz normal.");
                Failed?.Invoke(Error);
            }

            bool want = p.VcOn && p.VcAiOn;
            if (!want)
            {
                // so tira do audio depois que a cadeia ja cruzou para o motor normal (sem estalo)
                if (Volatile.Read(ref live) != IntPtr.Zero && unchecked(Environment.TickCount - lastProcessTick) > 150) Unpublish();
                if (handle != IntPtr.Zero)
                {
                    if (offSince == DateTime.MaxValue) offSince = DateTime.Now;
                    else if ((DateTime.Now - offSince).TotalSeconds > 90 && Volatile.Read(ref live) == IntPtr.Zero) FreeNow(); // libera a memoria
                }
                if (State != "erro") State = handle != IntPtr.Zero ? "pausada" : "desligada";
                return;
            }
            offSince = DateTime.MaxValue;
            if (failCode != 0) { State = "parou"; return; }

            var model = BeatriceModel.Find(models, p.VcAiModel);
            if (model == null) { Model = null; Voice = null; State = "sem modelo"; Error = "nenhum modelo de voz instalado"; Unpublish(); return; }
            Model = model;
            Voice = model.Voices[Math.Max(0, Math.Min(p.VcAiVoice, model.Voices.Count - 1))];
            if (string.IsNullOrEmpty(pluginPath)) { State = "sem plugin"; Error = "o Beatrice não está instalado"; return; }

            if (handle == IntPtr.Zero)
            {
                if (!loading && State != "erro") StartLoad(pluginPath, model.Toml);
                if (loading) State = "carregando";
                return;
            }
            if (!string.Equals(modelToml, model.Toml, StringComparison.OrdinalIgnoreCase)) SwitchModel(model.Toml);

            // tom medio de quem fala: o medidor substitui o valor salvo quando ja ouviu ~2,5 s de voz e difere em 3/4 de semitom
            float med = meter.Median;
            if (meter.Frames >= 60 && med > 0 && Math.Abs(med - SourceMidi) >= 0.75f) { SourceMidi = med; SourceChanged = true; }

            ApplyParams(p, Voice);
            Publish();
            State = "pronta";
            Error = null;
        }

        void StartLoad(string vst3, string toml)
        {
            loading = true;
            loadingToml = toml;
            Error = null;
            var t = new Thread(() =>
            {
                try
                {
                    if (!Tvh.Init(out var e)) { loadError = e; return; }
                    var st = Beatrice.StateForModel(toml);
                    var err = new StringBuilder(512);
                    var sw = Stopwatch.StartNew();
                    IntPtr h = Tvh.tvh_load(vst3, 0, Tvh.SampleRate, Tvh.MaxBlock, st, st.Length, err, err.Capacity);
                    if (h == IntPtr.Zero) { loadError = "o Beatrice não abriu (" + err + ")"; return; }
                    LoadMs = sw.ElapsedMilliseconds;
                    Log.Write($"Voz com IA: Beatrice {Beatrice.PluginVersion(vst3)} pronto em {LoadMs} ms.");
                    loadedHandle = h;
                }
                catch (Exception ex) { loadError = ex.Message; }
                finally { loading = false; }
            }) { IsBackground = true, Name = "TakaVox IA (carregando)" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        /// <summary>Troca o modelo da instancia (thread da interface; o audio fica mudo ~0,1 s enquanto o plugin le os arquivos).</summary>
        void SwitchModel(string toml)
        {
            byte[] cur = GetState(handle);
            byte[] st = (cur != null ? Beatrice.WithModel(cur, toml) : null) ?? Beatrice.StateForModel(toml);
            var sw = Stopwatch.StartNew();
            Tvh.tvh_set_state(handle, st, st.Length);
            modelToml = toml;
            sent.Clear();
            Log.Write($"Voz com IA: modelo trocado em {sw.ElapsedMilliseconds} ms.");
        }

        static byte[] GetState(IntPtr h)
        {
            int n = Tvh.tvh_get_state(h, null, 0);
            if (n <= 0) return null;
            var b = new byte[n];
            return Tvh.tvh_get_state(h, b, n) == n ? b : null;
        }

        void ApplyParams(ChainParams p, BeatriceVoice v)
        {
            double shift = v.AveragePitch - SourceMidi + p.VcAiPitch;
            shift = Math.Round(Math.Max(-24, Math.Min(24, shift)) * 8) / 8; // passos de 1/8 st (os do plugin)
            PitchShift = shift;
            Set(Beatrice.PVoice, v.Index);
            Set(Beatrice.PPitch, shift);
            Set(Beatrice.PFormant, p.VcAiFormant);
            Set(Beatrice.PIntonation, Math.Max(0, Math.Min(2, p.VcAiInton / 100.0)));
            Set(Beatrice.POutGain, OutGainDb);
            Set(Beatrice.PPitchCorrection, p.TuneOn ? Math.Max(0, Math.Min(1, p.TuneAmount / 100.0)) : 0); // Auto-Tune: a IA afina
            Set(Beatrice.PAvgSrcPitch, Math.Round(SourceMidi * 8) / 8); // so para a janela do plugin mostrar certo
        }

        void Set(int id, double plain)
        {
            if (sent.TryGetValue(id, out var old) && Math.Abs(old - plain) < 1e-9) return;
            sent[id] = plain;
            Tvh.tvh_param_set_id(handle, id, Tvh.tvh_param_to_norm(handle, id, plain));
        }

        /// <summary>Poe a instancia no audio (limpando o som velho guardado se ela estava pausada).</summary>
        unsafe void Publish()
        {
            if (Volatile.Read(ref live) == handle) return;
            StartWorker();
            // ninguem esta usando esta instancia agora: da para processar silencio aqui mesmo (aplica os ajustes
            // e tira os ~40 ms de voz antiga que ficaram dentro do plugin)
            var z = new float[Block];
            fixed (float* zp = z)
                for (int i = 0; i < 8; i++) { Array.Clear(z, 0, z.Length); Tvh.tvh_process(handle, zp, Block); }
            Interlocked.Increment(ref gen); // blocos de antes nao saem mais
            Volatile.Write(ref live, handle);
        }

        /// <summary>Tira a instancia do audio e espera as duas threads largarem ela.</summary>
        void Unpublish()
        {
            if (Volatile.Read(ref live) == IntPtr.Zero) return;
            Volatile.Write(ref live, IntPtr.Zero);
            Interlocked.MemoryBarrier();
            WaitOut(ref dspSeq);
            WaitOut(ref workSeq);
        }

        static void WaitOut(ref long seq)
        {
            long s = Interlocked.Read(ref seq);
            if ((s & 1) == 0) return;
            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref seq) == s && sw.ElapsedMilliseconds < 2000) Thread.Sleep(1);
        }

        void FreeNow()
        {
            Unpublish();
            if (handle == IntPtr.Zero) return;
            IntPtr h = handle;
            handle = IntPtr.Zero;
            modelToml = null;
            sent.Clear();
            try { Tvh.tvh_free(h); } catch { }
            Log.Write("Voz com IA: liberada da memória (desligada há mais de 1 minuto).");
        }

        /// <summary>Depois de uma falha: descarta a instancia e carrega de novo.</summary>
        public void Retry()
        {
            FreeNow();
            failCode = 0;
            failShown = false;
            Error = null;
            State = "desligada";
        }

        /// <summary>Uso medio de CPU da IA desde a ultima leitura (ms por bloco de 10 ms; 2,5 ms = 25% de 1 nucleo).</summary>
        public double ReadMsPerBlock()
        {
            long t = cpuTicks, b = cpuBlocks;
            long dt = t - lastCpuTicks, db = b - lastCpuBlocks;
            lastCpuTicks = t; lastCpuBlocks = b;
            return db > 0 ? dt * 1000.0 / Stopwatch.Frequency / db : 0;
        }

        /// <summary>Saida do app.</summary>
        public void Shutdown()
        {
            Unpublish();
            stop = true;
            work.Set();
            try { worker?.Join(1000); } catch { }
            worker = null;
            if (handle != IntPtr.Zero) { try { Tvh.tvh_free(handle); } catch { } handle = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Tom medio de quem fala: YIN a 12 kHz a cada 40 ms (~0,2% de 1 nucleo), mediana com memoria de ~1 min de fala.
    /// </summary>
    sealed class PitchMeter
    {
        const int Dec = 4, W = 480, MaxLag = 240, MinLag = 16, Len = W + MaxLag; // 12 kHz: 50..750 Hz
        readonly float[] ring = new float[1024];
        readonly float[] frame = new float[Len];
        readonly double[] cm = new double[MaxLag + 1];
        readonly Biquad lp1 = new Biquad(), lp2 = new Biquad();
        int pos, count, phase, blocks;
        const double Lo = 30, Step = 0.25;
        const int Bins = 240;                    // MIDI 30..90 em quartos de semitom
        readonly double[] hist = new double[Bins];
        double total;
        public volatile float Median;            // 0 = ainda nao sabe
        public volatile int Frames;              // quadros com voz contados

        public PitchMeter()
        {
            lp1.Set(FilterType.LowPass, 48000, 2600, 0.5412, 0);
            lp2.Set(FilterType.LowPass, 48000, 2600, 1.3066, 0);
        }

        public void Feed(float[] x, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float v = lp2.Process(lp1.Process(x[i]));
                if (Math.Abs(v) < 1e-20f) v = 0f;
                if (++phase < Dec) continue;
                phase = 0;
                ring[pos & 1023] = v;
                pos++;
                if (count < 1024) count++;
            }
            if (++blocks < 4 || count < Len) return;
            blocks = 0;
            Analyze();
        }

        void Analyze()
        {
            int start = pos - Len;
            double e = 0;
            for (int i = 0; i < Len; i++) frame[i] = ring[(start + i) & 1023];
            for (int i = 0; i < W; i++) e += frame[i] * frame[i];
            if (e / W < 3e-5) return; // abaixo de -45 dBFS: pausa
            double run = 0;
            for (int tau = 1; tau <= MaxLag; tau++)
            {
                double s = 0;
                for (int i = 0; i < W; i++) { double d = frame[i] - frame[i + tau]; s += d * d; }
                run += s;
                cm[tau] = run > 0 ? s * tau / run : 1;
            }
            int best = -1;
            for (int tau = MinLag; tau <= MaxLag; tau++)
                if (cm[tau] < 0.12)
                {
                    while (tau < MaxLag && cm[tau + 1] < cm[tau]) tau++;
                    best = tau;
                    break;
                }
            if (best < 0) return;
            double midi = 69 + 12 * Math.Log(12000.0 / best / 440.0, 2);
            int b = (int)Math.Round((midi - Lo) / Step);
            if (b < 0 || b >= Bins) return;
            const double decay = 0.9993; // os quadros antigos perdem peso devagar (~1 min de fala)
            for (int i = 0; i < Bins; i++) hist[i] *= decay;
            total = total * decay + 1;
            hist[b] += 1;
            int f = Frames + 1;
            Frames = f;
            if ((f & 7) == 0 || f < 64)
            {
                double half = total / 2, acc = 0;
                for (int i = 0; i < Bins; i++)
                {
                    acc += hist[i];
                    if (acc >= half) { Median = (float)(Lo + i * Step); break; }
                }
            }
        }
    }
}
