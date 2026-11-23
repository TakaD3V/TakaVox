// "Jogo simulado" para medir quanto o TakaVox tira de FPS: thread principal + render + N workers (prioridade normal,
// como um jogo), quadros sem limite de FPS. Cada quadro: a principal faz a logica, solta render e workers em paralelo
// e espera todos. Mede FPS medio e os "lows" (1% e 0,1% piores quadros) durante N segundos.
// Uso: gamesim.exe <segundos> <workers> [rotulo]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

static class GameSim
{
    static volatile int sink;

    /// <summary>Trabalho de CPU fixo (contas de ponto flutuante em cache, como fisica/animacao).</summary>
    static void Work(int units)
    {
        double a = 1.000001, b = 0.999999, acc = 0;
        for (int u = 0; u < units; u++)
            for (int i = 0; i < 2000; i++) { acc += a * b; a += 1e-9; b -= 1e-9; }
        if (acc == 42) sink++;
    }

    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 20;
        int nw = args.Length > 1 ? int.Parse(args[1]) : 6;
        string label = args.Length > 2 ? args[2] : "";
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;

        // calibra: 1 unidade ~ 0,1 ms
        var cal = Stopwatch.StartNew();
        Work(200);
        double unitMs = cal.Elapsed.TotalMilliseconds / 200;
        int unitsMain = (int)Math.Round(1.5 / unitMs), unitsJob = (int)Math.Round(2.5 / unitMs);

        var go = new Barrier(nw + 2);
        var done = new Barrier(nw + 2);
        bool stop = false;
        var threads = new List<Thread>();
        for (int k = 0; k < nw + 1; k++) // workers + render
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    go.SignalAndWait();
                    if (Volatile.Read(ref stop)) break;
                    Work(unitsJob);
                    done.SignalAndWait();
                }
            }) { IsBackground = true, Priority = ThreadPriority.Normal };
            t.Start();
            threads.Add(t);
        }
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; // como a thread principal de muitos jogos
        var frames = new List<double>(200000);
        var sw = Stopwatch.StartNew();
        long last = sw.ElapsedTicks;
        // aquece 2 s
        while (sw.Elapsed.TotalSeconds < secs + 2)
        {
            Work(unitsMain);
            go.SignalAndWait();
            done.SignalAndWait();
            long now = sw.ElapsedTicks;
            if (sw.Elapsed.TotalSeconds > 2) frames.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
        }
        Volatile.Write(ref stop, true);
        go.SignalAndWait();
        var s = frames.OrderBy(v => v).ToArray();
        double avgMs = frames.Average();
        double p99 = s[(int)(s.Length * 0.99)], p999 = s[(int)(s.Length * 0.999)];
        Console.WriteLine($"{label,-28} FPS médio {1000 / avgMs,7:0.0} · 1% low {1000 / p99,7:0.0} · 0,1% low {1000 / p999,7:0.0} · pior quadro {s.Last(),5:0.0} ms · {frames.Count} quadros");
        return 0;
    }
}
