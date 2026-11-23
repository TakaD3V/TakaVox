// Escuta o "CABLE Output" (o que o Discord/jogos recebem) e mede nivel, continuidade e cortes.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TakaVox.Audio;
using TakaVox.Interop;

static unsafe class CableCheck
{
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 8;
        var cab = AudioDevices.FindCableOutput();
        if (cab == null) { Console.WriteLine("CABLE Output nao encontrado"); return 2; }
        Console.WriteLine("Ouvindo: " + cab.Name);
        var en = AudioDevices.NewEnumerator();
        HResult.Check(en.GetDevice(cab.Id, out var dev), "dev");
        var iid = Iids.IAudioClient;
        HResult.Check(dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o), "activate");
        var ac = (IAudioClient)o;
        IntPtr fmt = AudioDevices.MakeFloatFormat(48000, 1);
        HResult.Check(ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, 2000000, 0, fmt, IntPtr.Zero), "init");
        var iidC = Iids.IAudioCaptureClient;
        HResult.Check(ac.GetService(ref iidC, out var co), "svc");
        var cc = (IAudioCaptureClient)co;
        HResult.Check(ac.Start(), "start");

        var buf = new float[48000 * (secs + 1)];
        int n = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < secs)
        {
            System.Threading.Thread.Sleep(5);
            while (true)
            {
                HResult.Check(cc.GetNextPacketSize(out uint pkt), "pkt");
                if (pkt == 0) break;
                HResult.Check(cc.GetBuffer(out IntPtr data, out uint frames, out var flags, out _, out _), "get");
                float* p = (float*)data;
                for (int i = 0; i < frames && n < buf.Length; i++) buf[n++] = (flags & AudioClientBufferFlags.Silent) != 0 ? 0f : p[i];
                cc.ReleaseBuffer(frames);
            }
        }
        ac.Stop();

        // analise em janelas de 100 ms
        int win = 4800, active = 0, total = 0;
        double maxRms = -200, peak = 0, sumActive = 0;
        for (int s = 0; s + win <= n; s += win)
        {
            double e = 0;
            for (int i = s; i < s + win; i++) { e += buf[i] * buf[i]; peak = Math.Max(peak, Math.Abs(buf[i])); }
            double db = 10 * Math.Log10(e / win + 1e-20);
            total++;
            if (db > -45) { active++; sumActive += Math.Pow(10, db / 10); }
            maxRms = Math.Max(maxRms, db);
        }
        // cortes: trechos de zero absoluto (>= 2 ms) no meio de audio com sinal
        int dropouts = 0, zeroRun = 0;
        for (int i = 1; i < n; i++)
        {
            if (buf[i] == 0f) zeroRun++;
            else
            {
                if (zeroRun >= 96 && zeroRun < 48000 && i - zeroRun > 4800) dropouts++;
                zeroRun = 0;
            }
        }
        Console.WriteLine($"amostras: {n} ({n / 48000.0:0.0} s)");
        Console.WriteLine($"janelas com voz/som (> -45 dBFS): {active}/{total}");
        Console.WriteLine($"nivel medio quando ha som: {(active > 0 ? 10 * Math.Log10(sumActive / active) : -200):0.0} dBFS RMS | max janela: {maxRms:0.0} dBFS | pico: {20 * Math.Log10(peak + 1e-12):0.00} dBFS");
        Console.WriteLine($"cortes (silencio digital no meio do audio): {dropouts}");
        Console.WriteLine(peak <= 0.892 ? "limitador OK (nunca passou de -1 dBFS)" : "ATENCAO: pico acima de -1 dBFS");
        Marshal.FreeCoTaskMem(fmt);
        return 0;
    }
}
