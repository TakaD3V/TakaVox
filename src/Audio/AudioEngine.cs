using System;
using System.Runtime.InteropServices;
using System.Threading;
using TakaVox.Dsp;
using TakaVox.Interop;

namespace TakaVox.Audio
{
    /// <summary>Medidores atualizados pela thread de audio e lidos pela interface.</summary>
    public sealed class EngineMeters
    {
        float inPeak, outPeak;
        public volatile float InRmsDb = -120f, OutRmsDb = -120f;
        public volatile float Vad, GateDb, AgcDb, DeessDb, CompDb, LimDb;
        public volatile bool GateOpen;
        public volatile float NoiseInDb = -120f, NoiseOutDb = -120f;
        public volatile float SpeechInDb = -120f, SpeechOutDb = -120f;
        public volatile int Clips;
        public long Blocks;

        public void PushPeaks(float i, float o)
        {
            if (i > inPeak) inPeak = i;
            if (o > outPeak) outPeak = o;
        }
        public float TakeInPeak() => Interlocked.Exchange(ref inPeak, 0f);
        public float TakeOutPeak() => Interlocked.Exchange(ref outPeak, 0f);
    }

    /// <summary>Coleta dados durante a calibracao automatica (sem alocar na thread de audio).</summary>
    public sealed class CalibrationData
    {
        public const int Max = 3000;
        public const int BlockLen = VoiceChain.Block;
        public readonly float[] RmsDb = new float[Max], PeakDb = new float[Max], Vad = new float[Max];
        /// <summary>Audio cru da calibracao, bloco a bloco (a EQ automatica mede o timbre do mic com ele).</summary>
        public readonly float[] Audio = new float[Max * BlockLen];
        int count;
        public int Count => Volatile.Read(ref count);
        public int AudioLength => Count * BlockLen;
        public void Add(float rmsDb, float peakDb, float vad, float[] block, int n)
        {
            int c = count;
            if (c >= Max) return;
            RmsDb[c] = rmsDb; PeakDb[c] = peakDb; Vad[c] = vad;
            if (block != null && n == BlockLen) Array.Copy(block, 0, Audio, c * BlockLen, BlockLen);
            Volatile.Write(ref count, c + 1);
        }
    }

    /// <summary>Recebe avisos do Windows quando dispositivos entram/saem ou o padrao muda.</summary>
    [ComVisible(true)]
    public sealed class DeviceNotifier : IMMNotificationClient
    {
        public event Action Changed;
        public event Action<bool, ERole, string> DefaultChanged;
        int IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { Fire(); return 0; }
        int IMMNotificationClient.OnDeviceAdded(string deviceId) { Fire(); return 0; }
        int IMMNotificationClient.OnDeviceRemoved(string deviceId) { Fire(); return 0; }
        int IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string id)
        {
            try { DefaultChanged?.Invoke(flow == EDataFlow.Capture, role, id); } catch { }
            return 0;
        }
        int IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
        void Fire() { try { Changed?.Invoke(); } catch { } }
    }

    /// <summary>
    /// Motor de audio: captura o microfone real, passa pela cadeia de estudio e entrega no
    /// microfone virtual (CABLE Input) e, opcionalmente, no fone para voce se ouvir.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public readonly ChainParams Params;
        public readonly EngineMeters Meters = new EngineMeters();
        public readonly SampleTap TapIn = new SampleTap(), TapOut = new SampleTap();
        public readonly Recorder Rec = new Recorder(48000 * 10);
        public volatile CalibrationData Calib;
        /// <summary>Sound pad: sons para quem te escuta (no microfone virtual) e/ou no seu fone.</summary>
        public readonly SoundBoard Sounds = new SoundBoard();
        readonly float[] sndMic = new float[VoiceChain.Block], sndPhone = new float[VoiceChain.Block], monBuf = new float[VoiceChain.Block];
        // protege a soma voz + sons (1 ms de antecipacao; sem som tocando nao mexe em nada)
        readonly Limiter outLimiter = new Limiter(48000f, 48);
        volatile bool soundMonitor;

        /// <summary>
        /// Descanso (economia automatica): nenhum app esta usando o microfone virtual e ninguem esta olhando a janela,
        /// se ouvindo ou testando. A cadeia pesada (IA anti-ruido, voz com IA) para e o cabo recebe silencio. Quem
        /// liga/desliga e a interface; um app abrindo o microfone acorda na hora (aviso do Windows).
        /// </summary>
        public volatile bool Resting;
        bool resting;
        readonly float[] warmBuf = new float[VoiceChain.Block];
        /// <summary>Blocos processados em descanso (para os testes e o registro).</summary>
        public long RestBlocks;

        readonly VoiceChain chain;
        readonly ElasticBuffer mainFifo = new ElasticBuffer { TargetFill = 720 };
        readonly ElasticBuffer monFifo = new ElasticBuffer { TargetFill = 600 };   // retorno: o minimo de atraso possivel
        RenderOutput mainOut, monOut, playOut;
        ArraySource playSrc;
        readonly object outLock = new object();

        Thread capThread;
        readonly IntPtr capEvt;
        volatile bool capStop, capRestart;
        volatile string inputId, inputName;
        public volatile string CaptureStatus = "parado";
        public int CaptureGlitches;   // pacotes do mic marcados com descontinuidade pelo Windows
        public double MaxProcessMs;   // maior tempo de processamento de um bloco de 10 ms
        public int SlowBlocks;        // blocos que levaram mais de 8 ms
        public volatile bool CaptureRunning;
        public volatile int CaptureChannels;
        public volatile string CaptureFormat = "";

        // estado da thread de audio
        readonly float[] block = new float[VoiceChain.Block];
        readonly float[] work = new float[VoiceChain.Block];
        const int RawRing = 32768; // ~680 ms: cabe a latencia da cadeia + plugins VST com lookahead grande
        readonly float[] rawDelay = new float[RawRing];
        long rawPos;
        int blockFill;

        // fila entre a captura e o processamento (a IA roda numa thread propria: um pico de CPU
        // nunca faz o microfone "pular", e picos de ate ~30 ms sao absorvidos sem corte)
        const int CapRing = 65536, CapMask = CapRing - 1;
        readonly float[] capRing = new float[CapRing];
        long capW, capWPub, capR;
        readonly IntPtr dspEvt;
        Thread dspThread;
        volatile bool dspStop;
        public int DspResyncs;
        float bypassMix, muteGain = 1f;
        uint ditherState = 0x9E3779B9;
        readonly double[] chPow = new double[8];
        int autoChannel = -1, autoCounter;

        readonly IMMDeviceEnumerator notifyEnum;
        readonly DeviceNotifier notifier = new DeviceNotifier();

        public event Action DevicesChanged;
        public event Action<bool, ERole, string> DefaultDeviceChanged;
        /// <summary>Disparado (na thread de audio) quando o microfone foi achado com outro ID (outra porta USB).</summary>
        public event Action<string, string> InputRelocated;

        public string InputId => inputId;
        public string InputName => inputName;
        public string OutputId { get; private set; }
        public string MonitorId { get; private set; }
        public bool MonitorOn { get; private set; }
        public float MonitorVolume { get; private set; } = 0.8f;
        public bool AiAvailable => chain.AiAvailable;
        public string EngineName => chain.EngineName;
        public int ActiveEngine => chain.ActiveEngine;
        public float SnrDb => chain.SnrDb;
        public AutoTune Tune => chain.Tune;
        public ToneRemover.Notch[] ToneNotches => chain.ToneNotches;
        public int ChainLatencySamples => chain.LatencySamples;
        /// <summary>Plugins VST processados na thread de audio (null = nenhum).</summary>
        public TakaVox.Plugins.PluginChain Plugins { get => chain.Plugins; set => chain.Plugins = value; }
        /// <summary>Voz com IA (Beatrice) no lugar do motor de tom (null = nenhuma).</summary>
        public TakaVox.Plugins.AiVoice Ai { get => chain.Ai; set => chain.Ai = value; }
        /// <summary>O audio esta saindo pela voz com IA agora.</summary>
        public bool AiActive => chain.AiActive;
        public RenderOutput MainOutput => mainOut;
        public RenderOutput MonitorOutput => monOut;
        public ElasticBuffer MainFifo => mainFifo;
        public ElasticBuffer MonitorFifo => monFifo;

        public AudioEngine(ChainParams p)
        {
            Params = p;
            PowerHints.DisableProcessThrottling();
            chain = new VoiceChain(p);
            capEvt = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
            dspEvt = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
            dspThread = new Thread(DspLoop) { IsBackground = true, Name = "TakaVox processamento", Priority = ThreadPriority.Highest };
            dspThread.Start();
            try
            {
                notifyEnum = AudioDevices.NewEnumerator();
                notifier.Changed += () => DevicesChanged?.Invoke();
                notifier.DefaultChanged += (cap, role, id) => DefaultDeviceChanged?.Invoke(cap, role, id);
                notifyEnum.RegisterEndpointNotificationCallback(notifier);
            }
            catch (Exception ex) { Log.Write("Notificações de dispositivo indisponíveis: " + ex.Message); }
        }

        // ------------------------------------------------------------------ controle

        public void SetInput(string id, string name)
        {
            inputId = id; inputName = name;
            if (capThread == null)
            {
                capThread = new Thread(CaptureLoop) { IsBackground = true, Name = "TakaVox captura", Priority = ThreadPriority.Highest };
                capThread.Start();
            }
            else
            {
                capRestart = true;
                NativeMethods.SetEvent(capEvt);
            }
        }

        public void SetOutput(string id)
        {
            lock (outLock)
            {
                if (mainOut != null && mainOut.DeviceId == id) return;
                mainOut?.Dispose();
                mainOut = null;
                OutputId = id;
                mainFifo.Reset();
                if (!string.IsNullOrEmpty(id)) mainOut = new RenderOutput(id, mainFifo, "microfone virtual");
            }
        }

        public void SetMonitor(string id, bool on, float volume)
        {
            lock (outLock)
            {
                MonitorVolume = volume;
                MonitorId = id;
                MonitorOn = on && !string.IsNullOrEmpty(id);
                UpdateMonitorOutput();
            }
        }

        /// <summary>Sons do sound pad no seu fone (abre a saida do retorno mesmo sem "Ouvir minha voz").</summary>
        public void SetSoundMonitor(bool on)
        {
            lock (outLock)
            {
                soundMonitor = on;
                UpdateMonitorOutput();
            }
        }

        public bool SoundMonitor => soundMonitor;

        /// <summary>Abre/fecha a saida do fone: ela existe se voce se ouve OU se os sons tocam no fone. Chamar com outLock.</summary>
        void UpdateMonitorOutput()
        {
            bool want = (MonitorOn || soundMonitor) && !string.IsNullOrEmpty(MonitorId);
            if (!want || (monOut != null && monOut.DeviceId != MonitorId))
            {
                monOut?.Dispose();
                monOut = null;
            }
            if (want && monOut == null)
            {
                monFifo.Reset();
                monOut = new RenderOutput(MonitorId, monFifo, "retorno (fone)", 1.5);
            }
            if (monOut != null) monOut.Volume = playOut != null ? 0f : 1f; // o volume da sua voz no fone vai na mistura
        }

        // ------------------------------------------------------------------ captura

        void CaptureLoop()
        {
            uint task = 0;
            IntPtr avrt = IntPtr.Zero;
            try { avrt = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref task); } catch { }
            PowerHints.PreferQuietCore(1);
            while (!capStop)
            {
                capRestart = false;
                try { CaptureOnce(); }
                catch (Exception ex)
                {
                    CaptureRunning = false;
                    CaptureStatus = RenderOutput.DescribeError(ex);
                    Log.Write("Captura: " + ex.Message);
                }
                if (capStop) break;
                if (capRestart) continue;
                for (int i = 0; i < 15 && !capStop && !capRestart; i++) Thread.Sleep(100);
            }
            if (avrt != IntPtr.Zero) try { NativeMethods.AvRevertMmThreadCharacteristics(avrt); } catch { }
        }

        string ResolveInput()
        {
            string id = inputId;
            if (AudioDevices.IsActive(id)) return id;
            // o Windows cria outro ID quando o mic muda de porta USB: procura pelo nome
            var found = AudioDevices.FindByName(true, inputName);
            if (found != null && !AudioDevices.IsVirtualName(found.Name))
            {
                inputId = found.Id; inputName = found.Name;
                Log.Change($"Microfone reencontrado em outra porta: {found.Name}");
                try { InputRelocated?.Invoke(found.Id, found.Name); } catch { }
                return found.Id;
            }
            return null;
        }

        void CaptureOnce()
        {
            string id = ResolveInput();
            if (id == null)
            {
                CaptureRunning = false;
                CaptureStatus = string.IsNullOrEmpty(inputName) ? "nenhum microfone escolhido" : "microfone desconectado – aguardando";
                return;
            }
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioClient ac = null; IAudioCaptureClient cc = null;
            IntPtr fmt = IntPtr.Zero;
            bool started = false;
            try
            {
                en = AudioDevices.NewEnumerator();
                HResult.Check(en.GetDevice(id, out dev), "Abrir microfone");
                var iid2 = Iids.IAudioClient2;
                object o;
                if (dev.Activate(ref iid2, ComConst.CLSCTX_ALL, IntPtr.Zero, out o) == 0)
                {
                    var props = new AudioClientProperties
                    {
                        cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                        bIsOffload = 0,
                        eCategory = AudioStreamCategory.Other,
                        Options = AudioClientStreamOptions.None
                    };
                    try { ((IAudioClient2)o).SetClientProperties(ref props); } catch { }
                    ac = (IAudioClient)o;
                }
                else
                {
                    var iid = Iids.IAudioClient;
                    HResult.Check(dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out o), "Ativar microfone");
                    ac = (IAudioClient)o;
                }

                int ch = 2;
                if (ac.GetMixFormat(out IntPtr mix) == 0 && mix != IntPtr.Zero)
                {
                    AudioDevices.ReadFormat(mix, out int mr, out int mc, out bool mf, out int mb);
                    ch = Math.Max(1, Math.Min(8, mc));
                    CaptureFormat = $"{mr / 1000.0:0.#} kHz, {mc} canal(is)";
                    Marshal.FreeCoTaskMem(mix);
                }
                CaptureChannels = ch;
                fmt = AudioDevices.MakeFloatFormat(48000, ch);
                var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
                HResult.Check(ac.Initialize(AudioClientShareMode.Shared, flags, 200000, 0, fmt, IntPtr.Zero), "Inicializar microfone");
                HResult.Check(ac.SetEventHandle(capEvt), "Evento do microfone");
                var iidC = Iids.IAudioCaptureClient;
                HResult.Check(ac.GetService(ref iidC, out var co), "Cliente de captura");
                cc = (IAudioCaptureClient)co;
                HResult.Check(ac.Start(), "Iniciar microfone");
                started = true;
                CaptureRunning = true;
                CaptureStatus = "ok";
                autoChannel = -1; autoCounter = 0;
                Array.Clear(chPow, 0, chPow.Length);
                int idle = 0;

                while (!capStop && !capRestart)
                {
                    uint w = NativeMethods.WaitForSingleObject(capEvt, 100);
                    if (capStop || capRestart) break;
                    bool got = false;
                    while (true)
                    {
                        HResult.Check(cc.GetNextPacketSize(out uint pkt), "Pacote do microfone");
                        if (pkt == 0) break;
                        int hr = cc.GetBuffer(out IntPtr data, out uint frames, out var bflags, out _, out _);
                        if (hr == HResult.AUDCLNT_S_BUFFER_EMPTY) break;
                        HResult.Check(hr, "Ler microfone");
                        if ((bflags & AudioClientBufferFlags.DataDiscontinuity) != 0 && got) CaptureGlitches++;
                        Feed(data, (int)frames, ch, (bflags & AudioClientBufferFlags.Silent) != 0);
                        HResult.Check(cc.ReleaseBuffer(frames), "Liberar buffer do microfone");
                        got = true;
                    }
                    if (got) idle = 0;
                    else if (++idle > 20) throw new Exception("o microfone parou de enviar áudio");
                }
            }
            finally
            {
                CaptureRunning = false;
                if (started) try { ac.Stop(); } catch { }
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (cc != null) Marshal.ReleaseComObject(cc);
                if (ac != null) Marshal.ReleaseComObject(ac);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        unsafe void Feed(IntPtr data, int frames, int ch, bool silent)
        {
            float* src = (float*)data;
            int mode = Params.ChannelMode;
            for (int f = 0; f < frames; f++)
            {
                float s;
                if (silent) s = 0f;
                else if (ch == 1) s = src[f];
                else
                {
                    float* fr = src + f * ch;
                    if (mode == 1) s = fr[0];
                    else if (mode == 2) s = fr[1];
                    else if (mode == 0 && autoChannel >= 0) s = fr[autoChannel];
                    else s = 0.5f * (fr[0] + fr[1]);
                    if (mode == 0)
                        for (int c = 0; c < 2; c++) chPow[c] += fr[c] * fr[c];
                }
                if (float.IsNaN(s) || float.IsInfinity(s)) s = 0f;
                capRing[capW & CapMask] = s;
                capW++;
                if (++blockFill == VoiceChain.Block)
                {
                    blockFill = 0;
                    if (ch >= 2 && mode == 0) UpdateAutoChannel();
                }
            }
            Volatile.Write(ref capWPub, capW);
            NativeMethods.SetEvent(dspEvt);
        }

        /// <summary>Thread de processamento: pega blocos de 10 ms da fila da captura e roda a cadeia (IA etc.).</summary>
        void DspLoop()
        {
            uint task = 0;
            IntPtr avrt = IntPtr.Zero;
            try { avrt = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref task); if (avrt != IntPtr.Zero) NativeMethods.AvSetMmThreadPriority(avrt, 1); } catch { }
            PowerHints.DisableThreadThrottling();
            PowerHints.PreferQuietCore(0);
            while (!dspStop)
            {
                NativeMethods.WaitForSingleObject(dspEvt, 100);
                if (dspStop) break;
                long w = Volatile.Read(ref capWPub);
                if (w - capR > CapRing - 4096) { capR = w - 2 * VoiceChain.Block; DspResyncs++; }
                while (w - capR >= VoiceChain.Block && !dspStop)
                {
                    for (int i = 0; i < VoiceChain.Block; i++) block[i] = capRing[(capR + i) & CapMask];
                    capR += VoiceChain.Block;
                    try { ProcessBlock(); }
                    catch (Exception ex) { Log.Write("Processamento: " + ex.Message); }
                }
            }
            if (avrt != IntPtr.Zero) try { NativeMethods.AvRevertMmThreadCharacteristics(avrt); } catch { }
        }

        void UpdateAutoChannel()
        {
            if (++autoCounter < 50) return; // decide a cada 0,5 s
            autoCounter = 0;
            double l = 10 * Math.Log10(chPow[0] + 1e-12), r = 10 * Math.Log10(chPow[1] + 1e-12);
            chPow[0] = chPow[1] = 0;
            if (Math.Max(l, r) < -95) return; // silencio total: nao decide
            if (l - r > 15) autoChannel = 0;
            else if (r - l > 15) autoChannel = 1;
            else if (Math.Abs(l - r) < 9) autoChannel = -1;
        }

        void ProcessBlock()
        {
            const int n = VoiceChain.Block;
            if (Resting && Calib == null)
            {
                // descanso: ninguem ouvindo; nada de IA, o microfone virtual recebe silencio
                resting = true;
                Array.Clear(work, 0, n);
                mainFifo.Write(work, n);
                TapOut.Write(work, n);
                Meters.Blocks++;
                RestBlocks++;
                return;
            }
            if (resting)
            {
                resting = false;
                WarmUp();
            }
            // medidas do sinal original
            float rawPeak = 0; double rawSum = 0;
            for (int i = 0; i < n; i++) { float a = Math.Abs(block[i]); if (a > rawPeak) rawPeak = a; rawSum += block[i] * block[i]; }
            float rawRmsDb = DspMath.PowToDb(rawSum / n);
            if (rawPeak >= 0.98f) Meters.Clips++;

            Rec.Feed(block, n);
            TapIn.Write(block, n);

            // processa (medindo o tempo: um bloco nunca pode demorar perto de 10 ms)
            Array.Copy(block, work, n);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            chain.Process(work);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > MaxProcessMs) MaxProcessMs = ms;
            if (ms > 8) SlowBlocks++;

            // sound pad: os sons tocando agora (para quem te escuta e para o seu fone)
            bool sounds = Sounds.Mix(sndMic, sndPhone, n);
            Sounds.BeginDuck(sounds);
            float soundMute = Sounds.WhileMuted ? 1f : 0f;
            bool monVoice = MonitorOn, monActive = monVoice || soundMonitor;
            float monVol = MonitorVolume;

            // A/B (efeitos desligados) com crossfade, sinal original alinhado no tempo
            float target = Params.Bypass ? 1f : 0f;
            float muteTarget = Params.Mute ? 0f : 1f;
            float ceilDb = Math.Min(-0.3f, Params.LimiterCeilingDb);
            float safetyCeil = DspMath.DbToLin(ceilDb);
            int lat = Math.Min(RawRing - 1, chain.LatencySamples);
            for (int i = 0; i < n; i++)
            {
                rawDelay[rawPos & (RawRing - 1)] = block[i];
                float d = rawDelay[(rawPos - lat) & (RawRing - 1)];
                rawPos++;
                if (bypassMix != target) bypassMix += Math.Sign(target - bypassMix) * Math.Min(Math.Abs(target - bypassMix), 1f / 960f);
                if (bypassMix > 0) work[i] = work[i] * (1 - bypassMix) + d * bypassMix;
                // mudo com rampa de 10 ms (sem estalo); a voz abaixa enquanto um som toca, se o usuario pediu
                if (muteGain != muteTarget) muteGain += Math.Sign(muteTarget - muteGain) * Math.Min(Math.Abs(muteTarget - muteGain), 1f / 480f);
                float voice = work[i] * muteGain * Sounds.NextDuck();
                if (monActive) monBuf[i] = (monVoice ? voice * monVol : 0f) + sndPhone[i];
                work[i] = sounds ? voice + sndMic[i] * (soundMute + (1f - soundMute) * muteGain) : voice;
            }

            // a soma voz + sons nunca estoura (sem som tocando, o limitador nao mexe: a voz ja chega limitada)
            outLimiter.Process(work, n, ceilDb, 60f);

            for (int i = 0; i < n; i++)
            {
                // "comfort noise" inaudivel (-96 dBFS, TPDF): a IA deixa silencio digital absoluto entre as falas
                // e alguns apps (ex.: Discord) acham que o microfone "sumiu" quando so recebem zeros
                if (!Params.Mute)
                {
                    ditherState ^= ditherState << 13; ditherState ^= ditherState >> 17; ditherState ^= ditherState << 5;
                    int r1 = (int)(ditherState & 0xFFFF);
                    ditherState ^= ditherState << 13; ditherState ^= ditherState >> 17; ditherState ^= ditherState << 5;
                    int r2 = (int)(ditherState & 0xFFFF);
                    work[i] += (r1 - r2) * (1.6e-5f / 65536f); // inteiros com sinal: amplitude max 1,6e-5 (-96 dBFS)
                }
                // trava final de seguranca: nada sai acima do teto do limitador, aconteca o que acontecer antes
                float v = work[i];
                if (float.IsNaN(v)) v = 0f;
                work[i] = v > safetyCeil ? safetyCeil : (v < -safetyCeil ? -safetyCeil : v);
            }

            mainFifo.Write(work, n);
            if (monActive)
            {
                for (int i = 0; i < n; i++)
                {
                    float v = monBuf[i];
                    monBuf[i] = float.IsNaN(v) ? 0f : (v > 0.99f ? 0.99f : (v < -0.99f ? -0.99f : v));
                }
                monFifo.Write(monBuf, n);
            }
            TapOut.Write(work, n);

            float outPeak = 0; double outSum = 0;
            for (int i = 0; i < n; i++) { float a = Math.Abs(work[i]); if (a > outPeak) outPeak = a; outSum += work[i] * work[i]; }
            float outRmsDb = DspMath.PowToDb(outSum / n);

            var m = Meters;
            m.PushPeaks(rawPeak, outPeak);
            m.InRmsDb = rawRmsDb; m.OutRmsDb = outRmsDb;
            float vad = chain.Vad;
            m.Vad = vad; m.GateDb = chain.GateGainDb; m.GateOpen = chain.GateOpen;
            m.AgcDb = chain.AgcGainDb; m.DeessDb = chain.DeessGrDb; m.CompDb = chain.CompGrDb; m.LimDb = chain.LimGrDb;
            if (vad < 0.15f && !chain.GateOpen)
            {
                m.NoiseInDb = Smooth(m.NoiseInDb, rawRmsDb);
                m.NoiseOutDb = Smooth(m.NoiseOutDb, outRmsDb);
            }
            else if (vad > 0.7f)
            {
                m.SpeechInDb = Smooth(m.SpeechInDb, rawRmsDb);
                m.SpeechOutDb = Smooth(m.SpeechOutDb, outRmsDb);
            }
            m.Blocks++;

            Calib?.Add(rawRmsDb, DspMath.LinToDb(rawPeak), vad, block, n);
        }

        /// <summary>Saindo do descanso: aquece a IA anti-ruido com os ultimos 60 ms de audio do microfone (sem saida).</summary>
        void WarmUp()
        {
            const int B = VoiceChain.Block, Blocks = 6;
            for (int k = Blocks; k >= 1; k--)
            {
                long start = capR - (long)B * (k + 1);
                if (start < 0) continue;
                for (int i = 0; i < B; i++) warmBuf[i] = capRing[(start + i) & CapMask];
                chain.WarmUp(warmBuf);
            }
        }

        static float Smooth(float prevDb, float newDb)
        {
            if (prevDb <= -119f) return newDb;
            double p = Math.Pow(10, prevDb / 10), q = Math.Pow(10, newDb / 10);
            return DspMath.PowToDb(p + (q - p) * 0.04);
        }

        // ------------------------------------------------------------------ teste A/B

        /// <summary>Processa uma gravacao com os ajustes atuais (cadeia separada, nao interfere no ao vivo).</summary>
        public float[] ProcessOffline(float[] input, int len)
        {
            var p = Params.Clone();
            p.Bypass = false;
            using (var c = new VoiceChain(p, true))
            {
                int lat = c.LatencySamples;
                int blocks = (len + lat + VoiceChain.Block - 1) / VoiceChain.Block;
                var buf = new float[blocks * VoiceChain.Block];
                Array.Copy(input, buf, Math.Min(len, input.Length));
                var blk = new float[VoiceChain.Block];
                for (int b = 0; b < blocks; b++)
                {
                    Array.Copy(buf, b * VoiceChain.Block, blk, 0, VoiceChain.Block);
                    c.Process(blk);
                    Array.Copy(blk, 0, buf, b * VoiceChain.Block, VoiceChain.Block);
                }
                var res = new float[len];
                Array.Copy(buf, lat, res, 0, len);
                return res;
            }
        }

        public bool IsPlaying { get { var s = playSrc; return s != null && !s.Finished; } }
        public double PlaybackProgress { get { var s = playSrc; return s == null ? 0 : s.Progress; } }

        public void Play(float[] data, int len)
        {
            StopPlayback();
            string dev = MonitorId;
            if (string.IsNullOrEmpty(dev) || !AudioDevices.IsActive(dev)) dev = AudioDevices.GetDefaultId(false, ERole.Console);
            if (dev == null) return;
            lock (outLock)
            {
                playSrc = new ArraySource(data, len);
                playOut = new RenderOutput(dev, playSrc, "teste");
                if (monOut != null) monOut.Volume = 0f;
            }
        }

        public void StopPlayback()
        {
            lock (outLock)
            {
                playOut?.Dispose();
                playOut = null;
                playSrc = null;
                if (monOut != null) monOut.Volume = 1f;
            }
        }

        public void Dispose()
        {
            capStop = true;
            NativeMethods.SetEvent(capEvt);
            capThread?.Join(3000);
            dspStop = true;
            NativeMethods.SetEvent(dspEvt);
            dspThread?.Join(3000);
            StopPlayback();
            lock (outLock)
            {
                mainOut?.Dispose(); mainOut = null;
                monOut?.Dispose(); monOut = null;
            }
            try { notifyEnum?.UnregisterEndpointNotificationCallback(notifier); } catch { }
            chain.Dispose();
            NativeMethods.CloseHandle(capEvt);
        }
    }
}
