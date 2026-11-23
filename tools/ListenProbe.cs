// Simula um app (Discord/jogo) abrindo o microfone virtual: grava do "CABLE Output" por N segundos e solta.
// Serve para testar a economia automatica do TakaVox (ele tem que acordar na hora).
// Uso: listenprobe.exe <segundos>
using System;
using System.Threading;
using TakaVox.Audio;
using TakaVox.Interop;

static class ListenProbe
{
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 3;
        var cab = AudioDevices.FindCableOutput();
        if (cab == null) { Console.WriteLine("CABLE Output não encontrado"); return 2; }
        var en = AudioDevices.NewEnumerator();
        en.GetDevice(cab.Id, out var dev);
        var iid = Iids.IAudioClient;
        dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o);
        var ac = (IAudioClient)o;
        var fmt = AudioDevices.MakeFloatFormat(48000, 2);
        HResult.Check(ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, 200000, 0, fmt, IntPtr.Zero), "Initialize");
        HResult.Check(ac.Start(), "Start");
        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " abriu o CABLE Output");
        Thread.Sleep(secs * 1000);
        ac.Stop();
        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " fechou");
        return 0;
    }
}
