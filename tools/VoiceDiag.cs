// Diagnostico de "voz no copo": grava ao mesmo tempo o mic cru e o que o Discord recebe (CABLE Output) e mostra:
// - formato do mic e se ele esta mandando silencio digital (mudo no proprio mic / acesso bloqueado);
// - "Escutar este dispositivo" ligado em algum mic (a voz chegando duas vezes);
// - ruido de fundo tonal (ventoinha/motor/zumbido eletrico) e o quanto ele fica perto da voz;
// - caminhos duplicados e eco: correlacao cruzada (GCC-PHAT) entre o mic e o cabo;
// - timbre do mic x voz de estudio (a mesma analise da EQ automatica) e o que o TakaVox muda;
// - reflexoes precoces (mesa/parede/monitor perto do mic) pelo cepstro.
// A fala e achada pelo detector de voz da propria IA (DeepFilterNet), que funciona mesmo com barulho alto.
// Uso: voicediag.exe nivel "<mic>"                    (3 s: so confere se o mic esta mandando som)
//      voicediag.exe eco "<mic>" [segundos]           (quanto do som das caixas chega no mic)
//      voicediag.exe <segundos> "<mic>" <pasta>        (diagnostico completo; salva diag_cru.wav e diag_processado.wav)
//      voicediag.exe arquivo <cru.wav> <processado.wav> (refaz a analise de uma gravacao)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TakaVox.Audio;
using TakaVox.Dsp;
using TakaVox.Interop;

static unsafe class VoiceDiag
{
    const int Fs = 48000, B = 480;

    static float[] Capture(string id, int secs, ManualResetEvent go, out string err, out int silentPackets, out int packets, bool loopback = false)
    {
        err = null;
        silentPackets = packets = 0;
        var buf = new float[Fs * (secs + 1)];
        int n = 0;
        try
        {
            var en = AudioDevices.NewEnumerator();
            HResult.Check(en.GetDevice(id, out var dev), "dev");
            var iid = Iids.IAudioClient;
            HResult.Check(dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o), "activate");
            var ac = (IAudioClient)o;
            IntPtr fmt = AudioDevices.MakeFloatFormat(Fs, 1);
            var flagsInit = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            if (loopback) flagsInit |= (AudioClientStreamFlags)0x00020000; // AUDCLNT_STREAMFLAGS_LOOPBACK: o que a saida esta tocando
            HResult.Check(ac.Initialize(AudioClientShareMode.Shared, flagsInit, 2000000, 0, fmt, IntPtr.Zero), "init");
            var iidC = Iids.IAudioCaptureClient;
            HResult.Check(ac.GetService(ref iidC, out var co), "svc");
            var cc = (IAudioCaptureClient)co;
            go.WaitOne();
            HResult.Check(ac.Start(), "start");
            var t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < secs)
            {
                Thread.Sleep(5);
                while (true)
                {
                    HResult.Check(cc.GetNextPacketSize(out uint pkt), "pkt");
                    if (pkt == 0) break;
                    HResult.Check(cc.GetBuffer(out IntPtr data, out uint frames, out var flags, out _, out _), "get");
                    packets++;
                    bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    if (silent) silentPackets++;
                    float* p = (float*)data;
                    for (int i = 0; i < frames && n < buf.Length; i++) buf[n++] = silent ? 0f : p[i];
                    cc.ReleaseBuffer(frames);
                }
            }
            ac.Stop();
        }
        catch (Exception ex) { err = ex.Message; }
        Array.Resize(ref buf, n);
        return buf;
    }

    static string MixFormat(string id)
    {
        try
        {
            var en = AudioDevices.NewEnumerator();
            HResult.Check(en.GetDevice(id, out var dev), "dev");
            var iid = Iids.IAudioClient;
            HResult.Check(dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o), "activate");
            var ac = (IAudioClient)o;
            HResult.Check(ac.GetMixFormat(out IntPtr p), "mix");
            AudioDevices.ReadFormat(p, out int rate, out int ch, out bool isFloat, out int bits);
            Marshal.FreeCoTaskMem(p);
            return $"{rate} Hz, {ch} canal(is), {(isFloat ? "32 bits float" : bits + " bits")}";
        }
        catch (Exception ex) { return "? (" + ex.Message + ")"; }
    }

    // "Escutar este dispositivo" (Propriedades do microfone > Escutar) e "melhorias de audio" do Windows
    static void DeviceFlags(string id, out bool? listen, out string listenTarget, out bool? sysFxOff)
    {
        listen = null; listenTarget = null; sysFxOff = null;
        try
        {
            var en = AudioDevices.NewEnumerator();
            if (en.GetDevice(id, out var dev) != 0) return;
            if (dev.OpenPropertyStore(ComConst.STGM_READ, out var ps) != 0) return;
            var kl = new PropertyKey("24dbb0fc-9311-4b3d-9cf0-18ff155639d4", 1);
            if (ps.GetValue(ref kl, out var v) == 0)
            {
                if (v.vt == 11) listen = v.boolValue != 0;
                NativeMethods.PropVariantClear(ref v);
            }
            var kt = new PropertyKey("24dbb0fc-9311-4b3d-9cf0-18ff155639d4", 0);
            if (ps.GetValue(ref kt, out var t) == 0)
            {
                if (t.vt == 31 && t.pointerValue != IntPtr.Zero) listenTarget = AudioDevices.GetNameById(Marshal.PtrToStringUni(t.pointerValue)) ?? Marshal.PtrToStringUni(t.pointerValue);
                NativeMethods.PropVariantClear(ref t);
            }
            var kf = PKeys.EndpointDisableSysFx;
            if (ps.GetValue(ref kf, out var f) == 0)
            {
                if (f.vt == 19) sysFxOff = f.uintValue != 0; // VT_UI4: 1 = melhorias desligadas
                NativeMethods.PropVariantClear(ref f);
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- FFT complexa
    static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1), wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    /// <summary>
    /// GCC-PHAT medio entre a referencia e o sinal, em trechos escolhidos: picos = caminhos por onde o som chega
    /// (mic x cabo: o principal e o TakaVox; outro pico forte = a voz chegando duas vezes).
    /// </summary>
    static List<(double ms, double rel)> Paths(float[] refSig, float[] sig, Func<int, bool> useChunk, double maxLagMs, out int chunks)
    {
        const int L = 1 << 16, N = L << 1;
        var sr = new double[N];
        var si = new double[N];
        var ar = new double[N]; var ai = new double[N];
        var br = new double[N]; var bi = new double[N];
        chunks = 0;
        int len = Math.Min(refSig.Length, sig.Length);
        for (int s = 0; s + L <= len; s += L / 2)
        {
            if (!useChunk(s)) continue;
            Array.Clear(ar, 0, N); Array.Clear(ai, 0, N); Array.Clear(br, 0, N); Array.Clear(bi, 0, N);
            for (int i = 0; i < L; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (L - 1));
                ar[i] = refSig[s + i] * w;
                br[i] = sig[s + i] * w;
            }
            Fft(ar, ai, false);
            Fft(br, bi, false);
            for (int k = 0; k < N; k++)
            {
                sr[k] += br[k] * ar[k] + bi[k] * ai[k];
                si[k] += bi[k] * ar[k] - br[k] * ai[k];
            }
            chunks++;
        }
        var res = new List<(double, double)>();
        if (chunks == 0) return res;
        for (int k = 0; k < N; k++)
        {
            double f = Math.Min(k, N - k) * (double)Fs / N;
            double m = Math.Sqrt(sr[k] * sr[k] + si[k] * si[k]) + 1e-20;
            double w = f < 100 || f > 8000 ? 0 : 1; // so a faixa da voz
            sr[k] = sr[k] / m * w; si[k] = si[k] / m * w;
        }
        Fft(sr, si, true);
        int maxLag = (int)(maxLagMs * Fs / 1000);
        var c = new double[maxLag + 1];
        double top = 0;
        for (int lag = 0; lag <= maxLag; lag++) { c[lag] = Math.Abs(sr[lag]); top = Math.Max(top, c[lag]); }
        if (top <= 0) return res;
        for (int lag = 1; lag < maxLag; lag++)
            if (c[lag] >= c[lag - 1] && c[lag] > c[lag + 1] && c[lag] >= 0.12 * top)
                res.Add((lag * 1000.0 / Fs, c[lag] / top));
        res.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        var outp = new List<(double, double)>();
        foreach (var p in res)
            if (!outp.Any(q => Math.Abs(q.Item1 - p.Item1) < 1.5)) outp.Add(p);
        return outp;
    }

    // terços de oitava de 63 Hz a 16 kHz
    static readonly double[] Centers = { 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

    static double[] BandLevels(float[] x, Func<int, bool> useFrame, out int frames)
    {
        const int N = 4096;
        var fft = new Fft(N);
        var seg = new float[N];
        var db = new float[N / 2 + 1];
        var acc = new double[N / 2 + 1];
        frames = 0;
        for (int s = 0; s + N <= x.Length; s += N / 2)
        {
            if (!useFrame(s)) continue;
            Array.Copy(x, s, seg, 0, N);
            fft.PowerDb(seg, db);
            for (int k = 0; k <= N / 2; k++) acc[k] += Math.Pow(10, db[k] / 10);
            frames++;
        }
        var bands = new double[Centers.Length];
        for (int b = 0; b < Centers.Length; b++)
        {
            double lo = Centers[b] / Math.Pow(2, 1.0 / 6), hi = Centers[b] * Math.Pow(2, 1.0 / 6), e = 0;
            for (int k = 1; k <= N / 2; k++) { double f = k * (double)Fs / N; if (f >= lo && f < hi) e += acc[k]; }
            bands[b] = 10 * Math.Log10(e / Math.Max(1, frames) + 1e-30);
        }
        return bands;
    }

    // cepstro de potencia medio nos quadros com voz: picos estaveis em 0,3-4 ms = reflexao precoce (filtro-pente)
    static void Cepstrum(float[] x, Func<int, bool> useFrame, string label)
    {
        const int N = 2048;
        var fft = new Fft(N);
        var seg = new float[N];
        var db = new float[N / 2 + 1];
        var cep = new double[N / 2];
        int frames = 0;
        for (int s = 0; s + N <= x.Length; s += N / 2)
        {
            if (!useFrame(s)) continue;
            Array.Copy(x, s, seg, 0, N);
            fft.PowerDb(seg, db);
            for (int q = 1; q < N / 2; q++)
            {
                double c = 0;
                for (int k = 1; k < N / 2; k++) c += db[k] * Math.Cos(Math.PI * k * q / (N / 2));
                cep[q] += c;
            }
            frames++;
            if (frames >= 80) break;
        }
        if (frames < 10) { Console.WriteLine($"   {label}: poucos quadros de voz"); return; }
        int q0 = (int)(0.0003 * Fs), q1 = (int)(0.004 * Fs);
        var vals = new List<double>();
        for (int q = q0; q <= q1; q++) vals.Add(Math.Abs(cep[q] / frames));
        vals.Sort();
        double med = vals[vals.Count / 2];
        var peaks = new List<(double ms, double z)>();
        for (int q = q0 + 1; q < q1; q++)
        {
            double v = Math.Abs(cep[q] / frames);
            if (v > Math.Abs(cep[q - 1] / frames) && v >= Math.Abs(cep[q + 1] / frames) && v > 4 * med)
                peaks.Add((q * 1000.0 / Fs, v / med));
        }
        peaks.Sort((a, b) => b.z.CompareTo(a.z));
        Console.Write($"   {label}: ");
        if (peaks.Count == 0) Console.WriteLine("nenhuma reflexão forte (bom)");
        else
        {
            for (int i = 0; i < Math.Min(3, peaks.Count); i++)
                Console.Write($"{peaks[i].ms:0.00} ms = superfície a ~{peaks[i].ms * 34.3 / 2:0} cm (força {peaks[i].z:0.0}x)   ");
            Console.WriteLine();
        }
    }

    /// <summary>Picos estreitos no ruido de fundo (trechos sem voz): ventoinha/motor ou zumbido eletrico.</summary>
    static void Tonal(float[] x, bool[] speech)
    {
        const int N = 65536;
        var re = new double[N]; var im = new double[N];
        var acc = new double[N / 2 + 1];
        int frames = 0;
        for (int s = 0; s + N <= x.Length; s += N / 2)
        {
            int sb = 0, tot = 0;
            for (int b = s / B; b < (s + N) / B && b < speech.Length; b++) { tot++; if (speech[b]) sb++; }
            if (tot == 0 || sb > tot / 4) continue; // so trechos quase sem fala
            for (int i = 0; i < N; i++) { re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1))); im[i] = 0; }
            Fft(re, im, false);
            for (int k = 0; k <= N / 2; k++) acc[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }
        if (frames == 0) { Console.WriteLine("   sem trechos de silêncio suficientes para medir"); return; }
        double df = (double)Fs / N;
        var db = acc.Select(v => 10 * Math.Log10(v / frames + 1e-30)).ToArray();
        var peaks = new List<(double f, double lvl, double prom)>();
        for (int k = Math.Max(41, (int)(30 / df)); k < (int)(2000 / df); k++)
        {
            if (db[k] < db[k - 1] || db[k] < db[k + 1]) continue;
            var nb = new List<double>();
            for (int j = k - 40; j <= k + 40; j++) if (Math.Abs(j - k) > 3) nb.Add(db[j]);
            nb.Sort();
            double prom = db[k] - nb[nb.Count / 2];
            if (prom > 12) peaks.Add((k * df, db[k], prom));
        }
        var top = peaks.OrderByDescending(p => p.lvl).Take(8).OrderBy(p => p.f).ToList();
        if (top.Count == 0) { Console.WriteLine("   nenhum ruído tonal (bom: o fundo é só chiado comum)"); return; }
        double f0 = top.OrderByDescending(p => p.lvl).First().f;
        // fundamental comum: menor pico que explica os outros como multiplos
        double fund = top.Select(p => p.f).Where(f => top.Count(q => Math.Abs(q.f / f - Math.Round(q.f / f)) * f < 2.5 && Math.Round(q.f / f) >= 1) >= Math.Min(3, top.Count)).DefaultIfEmpty(f0).Min();
        bool grid = Math.Abs(fund / 60 - Math.Round(fund / 60)) * 60 < 0.6;
        Console.WriteLine("   picos: " + string.Join("  ", top.Select(p => $"{p.f:0.0} Hz (+{p.prom:0} dB)")));
        Console.WriteLine($"   série harmônica de ~{fund:0.0} Hz → " + (grid ? "zumbido ELÉTRICO (rede de 60 Hz): cabo/USB/fonte perto do mic" : "VENTOINHA/MOTOR (ventilador, ventoinha do PC, ar-condicionado): não é múltiplo exato de 60 Hz"));
    }

    static void SaveWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int n = x.Length;
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767))));
        }
    }

    static float[] LoadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int n = BitConverter.ToInt32(bytes, 40) / 2;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = BitConverter.ToInt16(bytes, 44 + 2 * i) / 32768f;
        return x;
    }

    static float[] BlockRms(float[] x)
    {
        var r = new float[x.Length / B];
        for (int b = 0; b < r.Length; b++)
        {
            double e = 0;
            for (int i = b * B; i < (b + 1) * B; i++) e += x[i] * x[i];
            r[b] = (float)(10 * Math.Log10(e / B + 1e-20));
        }
        return r;
    }

    static double Pct(float[] v, double p)
    {
        var s = (float[])v.Clone();
        Array.Sort(s);
        return s.Length == 0 ? -200 : s[(int)Math.Min(s.Length - 1, p * s.Length)];
    }

    static double Pct(IEnumerable<float> v, double p) => Pct(v.ToArray(), p);

    /// <summary>
    /// Eco das caixas no mic: grava o que a saida padrao esta tocando (loopback) junto com o mic e mede
    /// quanto do som do PC chega no microfone. Nada e salvo.
    /// </summary>
    static int EchoTest(DeviceInfo mic, int secs)
    {
        string outId = AudioDevices.GetDefaultId(false, ERole.Console);
        string outName = AudioDevices.GetNameById(outId) ?? "?";
        int ff = AudioDevices.GetFormFactor(outId);
        string kind = ff == 3 ? "fone de ouvido" : ff == 1 ? "caixas de som" : ff == 5 || ff == 4 ? "headset" : "tipo " + ff;
        Console.WriteLine($"Saída padrão do Windows: {outName} (o Windows diz: {kind})");
        string commId = AudioDevices.GetDefaultId(false, ERole.Communications);
        if (commId != outId) Console.WriteLine($"Saída de comunicação (Discord pode usar): {AudioDevices.GetNameById(commId)}");
        var go = new ManualResetEvent(false);
        float[] m = null, l = null;
        string e1 = null, e2 = null;
        var t1 = new Thread(() => m = Capture(mic.Id, secs, go, out e1, out _, out _));
        var t2 = new Thread(() => l = Capture(outId, secs, go, out e2, out _, out _, true));
        t1.Start(); t2.Start(); Thread.Sleep(300); go.Set(); t1.Join(); t2.Join();
        if (e1 != null || e2 != null) { Console.WriteLine("erro: " + e1 + " " + e2); return 3; }
        var rl = BlockRms(l);
        var rm = BlockRms(m);
        Console.WriteLine($"Tocando no PC: {l.Length / (double)Fs:0.0} s de áudio, nível mediano {Pct(rl, 0.5):0} dBFS (máx {Pct(rl, 0.97):0})");
        Console.WriteLine($"Mic ao mesmo tempo: nível mediano {Pct(rm, 0.5):0} dBFS (máx {Pct(rm, 0.97):0})");
        if (l.Length < Fs * secs / 2 || Pct(rl, 0.5) < -70)
        {
            Console.WriteLine("RESULTADO: quase nada tocando no PC agora; não dá para medir o eco das caixas.");
            return 0;
        }
        double lthr = Pct(rl, 0.5) - 6;
        var paths = Paths(l, m, s => { int b0 = s / B, b1 = Math.Min(rl.Length, (s + 65536) / B); double e = 0; for (int b = b0; b < b1; b++) e += Math.Pow(10, rl[b] / 10); return b1 > b0 && 10 * Math.Log10(e / (b1 - b0)) > lthr; }, 400, out int chunks);
        if (paths.Count == 0) { Console.WriteLine("RESULTADO: não deu para medir"); return 0; }
        int lag = (int)Math.Round(paths[0].ms * Fs / 1000);
        Console.WriteLine($"Som do PC aparece no mic com {paths[0].ms:0.0} ms de atraso ({chunks} trechos)");
        const int N = 4096;
        var ar = new double[N]; var ai = new double[N]; var br = new double[N]; var bi = new double[N];
        var sxx = new double[N / 2 + 1]; var syy = new double[N / 2 + 1]; var sxr = new double[N / 2 + 1]; var sxi = new double[N / 2 + 1];
        int len = Math.Min(l.Length, m.Length - lag);
        for (int s = 0; s + N <= len; s += N / 2)
        {
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
                ar[i] = l[s + i] * w; ai[i] = 0;
                br[i] = m[s + i + lag] * w; bi[i] = 0;
            }
            Fft(ar, ai, false);
            Fft(br, bi, false);
            for (int k = 0; k <= N / 2; k++)
            {
                sxx[k] += ar[k] * ar[k] + ai[k] * ai[k];
                syy[k] += br[k] * br[k] + bi[k] * bi[k];
                sxr[k] += br[k] * ar[k] + bi[k] * ai[k];
                sxi[k] += bi[k] * ar[k] - br[k] * ai[k];
            }
        }
        foreach (var (f0, f1, name) in new[] { (100.0, 300.0, "graves 100–300 Hz"), (300.0, 1000.0, "médios 300 Hz–1 kHz"), (1000.0, 4000.0, "agudos 1–4 kHz") })
        {
            double num = 0, den = 0;
            for (int k = 1; k <= N / 2; k++)
            {
                double f = k * (double)Fs / N;
                if (f < f0 || f >= f1) continue;
                double c2 = (sxr[k] * sxr[k] + sxi[k] * sxi[k]) / (sxx[k] * syy[k] + 1e-30);
                num += c2 * syy[k]; den += syy[k];
            }
            Console.WriteLine($"   {name}: {100 * num / Math.Max(den, 1e-30):0}% do que o mic capta é o som das caixas");
        }
        return 0;
    }

    /// <summary>Analise completa de uma gravacao (mic cru + cabo).</summary>
    static int Report(float[] raw, float[] proc)
    {
        var rmsRaw = BlockRms(raw);
        var rmsProc = BlockRms(proc);
        int nb = rmsRaw.Length;
        // detector de voz da propria IA (o mesmo do app), bloco a bloco
        var vad = new float[nb];
        using (var vc = new VoiceChain(new ChainParams(), true))
        {
            var blk = new float[B];
            for (int b = 0; b < nb; b++)
            {
                Array.Copy(raw, b * B, blk, 0, B);
                vc.Process(blk);
                vad[b] = vc.Vad;
            }
        }
        var speech = new bool[nb];
        for (int b = 0; b < nb; b++) speech[b] = vad[b] > 0.6f;
        for (int b = 0; b < 100 && b < nb; b++) speech[b] = false; // o primeiro segundo e a IA aquecendo
        double noise = Pct(Enumerable.Range(0, nb).Where(b => !speech[b]).Select(b => rmsRaw[b]), 0.5);
        double voice = Pct(Enumerable.Range(0, nb).Where(b => speech[b]).Select(b => rmsRaw[b]), 0.7);
        int sb = speech.Count(s => s);
        double rawPeak = raw.Length == 0 ? 0 : raw.Max(v => Math.Abs(v)), procPeak = proc.Length == 0 ? 0 : proc.Max(v => Math.Abs(v));
        Console.WriteLine();
        Console.WriteLine("Níveis:");
        Console.WriteLine($"   mic cru: fundo (sem fala) {noise:0} dBFS · voz {(sb > 0 ? voice.ToString("0") + " dBFS" : "—")} · pico {20 * Math.Log10(rawPeak + 1e-12):0.0} dBFS · {sb * B / (double)Fs:0.0} s de fala");
        if (sb > 0) Console.WriteLine($"   voz acima do fundo: {voice - noise:0} dB {(voice - noise < 20 ? "(POUCO: a IA tem que trabalhar pesado e a voz sofre)" : voice - noise < 30 ? "(razoável)" : "(bom)")}");
        Console.WriteLine($"   cabo:    nas pausas {Pct(rmsProc, 0.1):0} dBFS · voz ~{Pct(rmsProc, 0.97):0} dBFS · pico {20 * Math.Log10(procPeak + 1e-12):0.0} dBFS");
        if (rawPeak == 0) { Console.WriteLine("⚠ O mic mandou SILÊNCIO DIGITAL: está mudo no próprio aparelho."); return 4; }
        if (rawPeak > 0.98) Console.WriteLine("⚠ O mic cru chegou no máximo (estalo, sopro ou batida no mic).");

        Console.WriteLine();
        Console.WriteLine("Ruído de fundo tonal:");
        Tonal(raw, speech);

        if (sb < 300)
        {
            Console.WriteLine();
            Console.WriteLine($"⚠ Pouca fala gravada ({sb * B / (double)Fs:0.0} s): fale mais na próxima gravação para medir o timbre e os caminhos da voz.");
            return 5;
        }
        Func<int, bool> rawVoiced = s =>
        {
            int b0 = s / B, b1 = Math.Min(nb, (s + 4096) / B), k = 0;
            for (int b = b0; b < b1; b++) if (speech[b]) k++;
            return b1 > b0 && k >= (b1 - b0) * 0.8;
        };

        Console.WriteLine();
        Console.WriteLine("Caminhos da voz até o cabo (correlação mic × cabo):");
        var paths = Paths(raw, proc, s =>
        {
            int b0 = s / B, b1 = Math.Min(nb, (s + 65536) / B), k = 0;
            for (int b = b0; b < b1; b++) if (speech[b]) k++;
            return b1 > b0 && k >= (b1 - b0) * 0.4;
        }, 400, out int chunks);
        if (paths.Count == 0) Console.WriteLine("   não deu para medir");
        else
        {
            var main = paths[0];
            Console.WriteLine($"   principal: {main.ms:0.0} ms depois do mic (TakaVox + buffers) — {chunks} trechos");
            var others = paths.Skip(1).Where(p => Math.Abs(p.ms - main.ms) > 12 && p.rel >= 0.25).Take(4).ToList();
            if (others.Count == 0) Console.WriteLine("   nenhum outro caminho forte: a voz chega UMA vez só (bom)");
            foreach (var p in others) Console.WriteLine($"   ⚠ outro caminho: {p.ms:0.0} ms (força {p.rel * 100:0}% do principal) — a voz chega duas vezes = som de tubo/copo");
        }

        Console.WriteLine();
        Console.WriteLine("Timbre do mic cru × voz de estúdio (mesma análise da EQ automática):");
        var eq = VoiceEq.Analyze(raw, nb * B, rmsRaw, speech.Select(s => s ? 1f : 0f).ToArray(), nb, 0, (float)noise);
        if (!eq.Ok) Console.WriteLine("   não analisou: " + eq.Error);
        else
        {
            var line1 = new System.Text.StringBuilder("   ");
            for (int c = 0; c < VoiceEq.Centers.Length; c++)
            {
                double f = VoiceEq.Centers[c];
                string fs = f >= 1000 ? (f / 1000).ToString("0.##") + "k" : f.ToString("0");
                line1.Append($"{fs}:{(double.IsNaN(eq.Deviation[c]) ? "?" : eq.Deviation[c].ToString("+0;-0;0"))}  ");
            }
            Console.WriteLine(line1.ToString());
            Console.WriteLine("   EQ automática sugerida: " + VoiceEq.Summary(eq));
            foreach (var note in eq.Notes) Console.WriteLine("      • " + note);
        }

        var bRaw = BandLevels(raw, rawVoiced, out _);
        double pthr = Pct(rmsProc, 0.97) - 20;
        var bProc = BandLevels(proc, s =>
        {
            int b0 = s / B, b1 = Math.Min(rmsProc.Length, (s + 4096) / B);
            double e = 0;
            for (int b = b0; b < b1; b++) e += Math.Pow(10, rmsProc[b] / 10);
            return b1 > b0 && 10 * Math.Log10(e / (b1 - b0)) > pthr;
        }, out _);
        double off = bProc[Array.IndexOf(Centers, 1000.0)] - bRaw[Array.IndexOf(Centers, 1000.0)];
        Console.WriteLine();
        Console.WriteLine("O que o TakaVox faz no timbre da fala (cabo − mic, alinhado em 1 kHz):");
        var line2 = new System.Text.StringBuilder("   ");
        for (int b = 0; b < Centers.Length; b++)
        {
            string fs = Centers[b] >= 1000 ? (Centers[b] / 1000).ToString("0.##") + "k" : Centers[b].ToString("0");
            line2.Append($"{fs}:{(bProc[b] - bRaw[b] - off):+0;-0;0}  ");
        }
        Console.WriteLine(line2.ToString());

        Console.WriteLine();
        Console.WriteLine("Reflexões precoces (mesa/parede/monitor perto do mic dão som de 'copo'):");
        Cepstrum(raw, rawVoiced, "mic cru");
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "arquivo")
            return Report(LoadWav(args[1]), LoadWav(args[2]));

        bool levelOnly = args.Length > 0 && args[0] == "nivel";
        bool echo = args.Length > 0 && args[0] == "eco";
        int secs = levelOnly ? 3 : echo ? 8 : (args.Length > 0 ? int.Parse(args[0]) : 20);
        string micName = args.Length > 1 ? args[1] : "M8";
        string outDir = args.Length > 2 ? args[2] : ".";
        DeviceInfo mic = null;
        foreach (var d in AudioDevices.List(true)) if (d.Name.Contains(micName)) mic = d;
        if (mic == null) { Console.WriteLine("mic não encontrado"); return 2; }
        Console.WriteLine($"Mic: {mic.Name} ({MixFormat(mic.Id)})");

        if (echo) return EchoTest(mic, args.Length > 2 ? int.Parse(args[2]) : 8);

        if (levelOnly)
        {
            var g0 = new ManualResetEvent(true);
            var x = Capture(mic.Id, secs, g0, out string e0, out int sp0, out int pk0);
            if (e0 != null) { Console.WriteLine("erro: " + e0); return 3; }
            var r0 = BlockRms(x);
            double peak = x.Length == 0 ? 0 : x.Max(v => Math.Abs(v));
            bool zeros = peak == 0;
            Console.WriteLine($"Nível: mediana {Pct(r0, 0.5):0.0} dBFS, máximo {Pct(r0, 0.99):0.0} dBFS, pico {20 * Math.Log10(peak + 1e-12):0.0} dBFS, pacotes 'silêncio' do Windows {sp0}/{pk0}");
            Console.WriteLine(zeros ? "RESULTADO: SILÊNCIO DIGITAL (o mic está mudo no próprio aparelho ou o Windows bloqueou o acesso)" : "RESULTADO: o mic está mandando som");
            return zeros ? 1 : 0;
        }

        var cab = AudioDevices.FindCableOutput();
        if (cab == null) { Console.WriteLine("CABLE Output não encontrado"); return 2; }
        Console.WriteLine($"Cabo (o que o Discord recebe): {cab.Name} ({MixFormat(cab.Id)})");
        Console.WriteLine();
        Console.WriteLine("Windows:");
        foreach (var d in AudioDevices.List(true))
        {
            DeviceFlags(d.Id, out bool? listen, out string target, out bool? sysFxOff);
            string fx = sysFxOff == null ? "" : sysFxOff.Value ? "melhorias de áudio desligadas" : "melhorias de áudio LIGADAS";
            string ls = listen == true ? $"⚠ 'ESCUTAR ESTE DISPOSITIVO' LIGADO → toca em: {target ?? "saída padrão"}" : "escutar: desligado";
            Console.WriteLine($"   {d.Name}: {ls}{(fx.Length > 0 ? " · " + fx : "")}");
        }

        Console.WriteLine();
        Console.WriteLine($"Gravando {secs} s: mic cru + cabo…");
        var go = new ManualResetEvent(false);
        float[] raw = null, proc = null;
        string e1 = null, e2 = null;
        var t1 = new Thread(() => raw = Capture(mic.Id, secs, go, out e1, out _, out _));
        var t2 = new Thread(() => proc = Capture(cab.Id, secs, go, out e2, out _, out _));
        t1.Start(); t2.Start(); Thread.Sleep(300); go.Set(); t1.Join(); t2.Join();
        if (e1 != null || e2 != null) { Console.WriteLine("erro: " + e1 + " " + e2); return 3; }
        Directory.CreateDirectory(outDir);
        SaveWav(Path.Combine(outDir, "diag_cru.wav"), raw);
        SaveWav(Path.Combine(outDir, "diag_processado.wav"), proc);
        return Report(raw, proc);
    }
}
