// Diagnostico da economia automatica: quem esta usando o microfone virtual agora e quantos avisos o Windows manda.
// Uso: listencheck.exe <segundos>
using System;
using System.Diagnostics;
using System.Threading;
using TakaVox.Audio;

static class ListenCheck
{
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 5;
        var cab = AudioDevices.FindCableOutput();
        Console.WriteLine("CABLE Output: " + (cab?.Name ?? "não encontrado"));
        var w = new ListenWatcher();
        int events = 0, starts = 0;
        w.Changed += s => { Interlocked.Increment(ref events); if (s) Interlocked.Increment(ref starts); };
        w.Watch(cab?.Id);
        Console.WriteLine("vigiando: " + w.Ok);
        var sw = Stopwatch.StartNew();
        int n = w.CountListening(true, out string who);
        Console.WriteLine($"usando agora: {n} ({who}) — consulta em {sw.Elapsed.TotalMilliseconds:0.00} ms");
        Thread.Sleep(secs * 1000);
        sw.Restart();
        sw.Restart(); n = w.CountListening(); double fast = sw.Elapsed.TotalMilliseconds; sw.Restart(); n = w.CountListening(true, out who);
        Console.WriteLine($"depois de {secs} s: {n} ({who}) — consulta rápida {fast:0.00} ms, com nomes {sw.Elapsed.TotalMilliseconds:0.00} ms; avisos recebidos: {events} ({starts} 'começou')");
        w.Dispose();
        return 0;
    }
}
