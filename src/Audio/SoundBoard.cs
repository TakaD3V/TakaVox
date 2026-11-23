using System;
using System.Threading;

namespace TakaVox.Audio
{
    /// <summary>Um som pronto para tocar: mono, 48 kHz, 16 bits (metade da memoria de float, 96 dB de faixa).</summary>
    public sealed class SoundClip
    {
        public readonly short[] Data;
        public int Length => Data.Length;
        public SoundClip(short[] data) { Data = data; }
    }

    /// <summary>Pedido de tocar um som (montado pela interface; a thread de audio so le).</summary>
    public sealed class SoundPlay
    {
        public int Key;              // numero do som (modos reiniciar/parar e o que a interface mostra tocando)
        public SoundClip Clip;
        public float Gain = 1f;      // linear: volume do som + normalizacao
        public int Start, End;       // trecho em amostras
        public int FadeIn, FadeOut;  // amostras
        public bool Loop;
        public int Mode;             // 0 reiniciar, 1 liga/desliga (apertar de novo para), 2 sobrepor
        public bool PreviewOnly;     // so no fone de quem esta usando (previa do editor)
    }

    /// <summary>
    /// Mixer do sound pad, na thread de audio: ate 32 sons ao mesmo tempo, com fade, loop, trecho, modos de disparo
    /// e "abaixar minha voz enquanto toca". A interface so manda pedidos por uma fila sem trava; nada aqui aloca memoria.
    /// </summary>
    public sealed class SoundBoard
    {
        public const int MaxVoices = 32;
        const float StopFade = 1f / (0.015f * 48000f); // 15 ms

        sealed class Voice
        {
            public bool On, Stopping, Preview;
            public int Key, Pos, Start, End, FadeIn, FadeOut;
            public short[] D;
            public float Gain, StopEnv;
            public bool Loop;
            public long Born;
        }

        enum CmdType { Play = 1, Stop = 2, StopAll = 3 }
        struct Cmd { public CmdType Type; public SoundPlay Play; public int Key; }

        readonly Voice[] voices = new Voice[MaxVoices];
        readonly Cmd[] queue = new Cmd[256];
        int qW, qR;
        long born;
        float duck = 1f;

        // ---------------------------------------------------------------- ajustes (a interface escreve, o audio le)
        /// <summary>Volume dos sons para quem te escuta (linear) e para o seu fone (linear).</summary>
        public volatile float MicGain = 1f, PhoneGain = 0.5f;
        public volatile bool ToMic = true, ToPhone = true;
        /// <summary>Os sons saem mesmo com o microfone no mudo.</summary>
        public volatile bool WhileMuted = true;
        /// <summary>So um som por vez: tocar um para os outros.</summary>
        public volatile bool Exclusive;
        /// <summary>Quanto a sua voz abaixa enquanto um som toca (dB, 0 = nada).</summary>
        public volatile float DuckDb;

        // ---------------------------------------------------------------- estado para a interface
        /// <summary>Som tocando em cada vaga (0 = livre) e onde ele esta (0..1).</summary>
        public readonly int[] SlotKey = new int[MaxVoices];
        public readonly float[] SlotPos = new float[MaxVoices];
        /// <summary>Quantos sons estao tocando agora (para o mic, sem contar previas).</summary>
        public volatile int Active;
        /// <summary>Pico de saida dos sons neste bloco (medidor).</summary>
        public volatile float Peak;

        public SoundBoard()
        {
            for (int i = 0; i < MaxVoices; i++) voices[i] = new Voice();
        }

        // ---------------------------------------------------------------- interface (1 produtor)
        bool Push(Cmd c)
        {
            int w = qW;
            if (w - Volatile.Read(ref qR) >= queue.Length) return false; // fila cheia (o audio parou?)
            queue[w & (queue.Length - 1)] = c;
            Volatile.Write(ref qW, w + 1);
            return true;
        }

        public bool Play(SoundPlay p) => p?.Clip != null && p.End > p.Start && Push(new Cmd { Type = CmdType.Play, Play = p, Key = p.Key });
        public void Stop(int key) => Push(new Cmd { Type = CmdType.Stop, Key = key });
        public void StopAll() => Push(new Cmd { Type = CmdType.StopAll });

        /// <summary>O som esta tocando agora? (e quanto ja tocou, 0..1)</summary>
        public bool IsPlaying(int key, out float pos)
        {
            pos = 0;
            bool any = false;
            for (int i = 0; i < MaxVoices; i++)
                if (Volatile.Read(ref SlotKey[i]) == key) { any = true; pos = Math.Max(pos, SlotPos[i]); }
            return any;
        }

        // ---------------------------------------------------------------- thread de audio
        void Apply(ref Cmd c)
        {
            switch (c.Type)
            {
                case CmdType.StopAll:
                    foreach (var v in voices) if (v.On) v.Stopping = true;
                    break;
                case CmdType.Stop:
                    foreach (var v in voices) if (v.On && v.Key == c.Key) v.Stopping = true;
                    break;
                case CmdType.Play:
                {
                    var p = c.Play;
                    bool playing = false;
                    foreach (var v in voices) if (v.On && !v.Stopping && v.Key == p.Key && v.Preview == p.PreviewOnly) playing = true;
                    if (p.Mode == 1 && playing)
                    {
                        foreach (var v in voices) if (v.On && v.Key == p.Key) v.Stopping = true;
                        break;
                    }
                    if (p.Mode == 0 || (Exclusive && !p.PreviewOnly))
                        foreach (var v in voices)
                            if (v.On && v.Preview == p.PreviewOnly && (v.Key == p.Key || (Exclusive && !p.PreviewOnly))) v.Stopping = true;
                    Voice slot = null;
                    foreach (var v in voices) if (!v.On) { slot = v; break; }
                    if (slot == null)
                    {
                        // sem vaga: o mais velho da lugar (corte de 15 ms nao da para evitar aqui)
                        slot = voices[0];
                        foreach (var v in voices) if (v.Born < slot.Born) slot = v;
                    }
                    slot.On = true; slot.Stopping = false; slot.StopEnv = 1f;
                    slot.Preview = p.PreviewOnly;
                    slot.Key = p.Key; slot.D = p.Clip.Data;
                    slot.Start = Math.Max(0, Math.Min(p.Start, slot.D.Length));
                    slot.End = Math.Max(slot.Start, Math.Min(p.End, slot.D.Length));
                    slot.Pos = slot.Start;
                    slot.FadeIn = Math.Max(0, p.FadeIn); slot.FadeOut = Math.Max(0, p.FadeOut);
                    slot.Gain = p.Gain; slot.Loop = p.Loop;
                    slot.Born = ++born;
                    break;
                }
            }
        }

        /// <summary>
        /// Thread de audio: mistura os sons em mic[] e phone[] (zera e soma). Retorna se algum som esta tocando.
        /// </summary>
        public bool Mix(float[] mic, float[] phone, int n)
        {
            int w = Volatile.Read(ref qW);
            while (qR != w)
            {
                Apply(ref queue[qR & (queue.Length - 1)]);
                queue[qR & (queue.Length - 1)].Play = null;
                Volatile.Write(ref qR, qR + 1);
            }
            Array.Clear(mic, 0, n);
            Array.Clear(phone, 0, n);
            int active = 0;
            bool any = false;
            const float s16 = 1f / 32768f;
            // volumes mestre: a previa do editor sempre toca no fone (mesmo com "ouvir no fone" desligado)
            float mg = ToMic ? MicGain : 0f, pg = ToPhone ? PhoneGain : 0f, prevG = Math.Max(PhoneGain, 0.25f);
            for (int k = 0; k < MaxVoices; k++)
            {
                var v = voices[k];
                if (!v.On) { if (SlotKey[k] != 0) SlotKey[k] = 0; continue; }
                any = true;
                if (!v.Preview) active++;
                float toMic = v.Preview ? 0f : mg, toPhone = v.Preview ? prevG : pg;
                short[] d = v.D;
                for (int i = 0; i < n; i++)
                {
                    if (v.Pos >= v.End)
                    {
                        if (v.Loop && v.End > v.Start) v.Pos = v.Start;
                        else { v.On = false; break; }
                    }
                    float env = 1f;
                    int rel = v.Pos - v.Start;
                    if (rel < v.FadeIn) env = (rel + 1) / (float)(v.FadeIn + 1);
                    if (!v.Loop)
                    {
                        int left = v.End - v.Pos;
                        if (left <= v.FadeOut) { float e2 = left / (float)(v.FadeOut + 1); if (e2 < env) env = e2; }
                    }
                    if (v.Stopping)
                    {
                        v.StopEnv -= StopFade;
                        if (v.StopEnv <= 0f) { v.On = false; break; }
                        env *= v.StopEnv;
                    }
                    float s = d[v.Pos] * s16 * v.Gain * env;
                    mic[i] += s * toMic;
                    phone[i] += s * toPhone;
                    v.Pos++;
                }
                if (v.On)
                {
                    SlotKey[k] = v.Preview ? -v.Key : v.Key;
                    SlotPos[k] = v.End > v.Start ? (v.Pos - v.Start) / (float)(v.End - v.Start) : 0f;
                }
                else SlotKey[k] = 0;
            }
            Active = active;
            float peak = 0f;
            if (any)
                for (int i = 0; i < n; i++)
                {
                    float a = Math.Abs(mic[i]);
                    if (a > peak) peak = a;
                }
            Peak = peak;
            return any;
        }

        float duckTarget = 1f;

        /// <summary>Thread de audio: no inicio do bloco, depois do Mix (decide para onde a voz vai).</summary>
        public void BeginDuck(bool soundsOn)
        {
            float db = DuckDb;
            duckTarget = soundsOn && Active > 0 && ToMic && db < -0.1f ? (float)Math.Pow(10, db / 20.0) : 1f;
        }

        /// <summary>
        /// Thread de audio: ganho da sua voz nesta amostra (abaixa em ~20 ms quando um som comeca e volta em ~300 ms).
        /// Chamar uma vez por amostra, na ordem.
        /// </summary>
        public float NextDuck()
        {
            float target = duckTarget;
            if (duck == target) return duck;
            const float attack = 1f / (0.020f * 48000f), release = 1f / (0.300f * 48000f);
            duck += (target - duck) * (target < duck ? attack : release);
            if (Math.Abs(target - duck) < 1e-4f) duck = target;
            return duck;
        }
    }
}
