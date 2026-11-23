using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TakaVox.UI
{
    /// <summary>
    /// Leveza durante jogos: a janela para de desenhar (medidores, espectro) quando um jogo/app em tela cheia
    /// esta na frente ou quando ela esta escondida, e desenha menos quando esta aberta mas em segundo plano.
    /// Tambem mostra o quanto o proprio TakaVox esta usando do PC.
    /// </summary>
    public sealed partial class MainWindow
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out WinRect r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MonitorInfo mi);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
        [StructLayout(LayoutKind.Sequential)] struct WinRect { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int cbSize; public WinRect rcMonitor, rcWork; public uint dwFlags; }

        bool otherFullscreen;
        DateTime fullscreenCheckAt;
        int renderTick;
        DateTime lastRenderAt = DateTime.Now;
        static readonly int MyPid = Process.GetCurrentProcess().Id;

        /// <summary>Um jogo (ou outro app) em tela cheia esta em primeiro plano?</summary>
        static bool OtherAppFullscreen()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == MyPid) return false; // nossa janela ou a de um plugin
                var cls = new StringBuilder(64);
                GetClassNameW(fg, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false; // area de trabalho
                if (!GetWindowRect(fg, out var r)) return false;
                var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfoW(MonitorFromWindow(fg, 2), ref mi)) return false;
                return r.L <= mi.rcMonitor.L && r.T <= mi.rcMonitor.T && r.R >= mi.rcMonitor.R && r.B >= mi.rcMonitor.B;
            }
            catch { return false; }
        }

        /// <summary>
        /// Decide se este quadro da animacao deve ser desenhado. Falso = nada de medidores/espectro agora
        /// (janela escondida, minimizada, jogo em tela cheia na frente; ou janela em segundo plano entre quadros).
        /// dt = segundos desde o ultimo quadro desenhado (para os medidores cairem no ritmo certo).
        /// </summary>
        bool ShouldRender(out float dt)
        {
            dt = 0;
            if (snapshotMode) { dt = 0.033f; return true; }
            var now = DateTime.Now;
            if ((now - fullscreenCheckAt).TotalMilliseconds > 500)
            {
                fullscreenCheckAt = now;
                otherFullscreen = OtherAppFullscreen();
            }
            if (!IsVisible || WindowState == WindowState.Minimized || otherFullscreen) return false;
            renderTick++;
            if (!IsActive && renderTick % 4 != 0) return false; // aberta mas em segundo plano: ~8 quadros/s
            dt = (float)Math.Min(0.5, (now - lastRenderAt).TotalSeconds);
            lastRenderAt = now;
            return true;
        }

        // ---------------------------------------------------------------- uso do PC pelo TakaVox
        // Medido com chamadas baratas do proprio processo (GetProcessTimes / GetProcessMemoryInfo). A classe Process do
        // .NET le a lista de TODOS os processos do Windows a cada consulta de memoria: caro demais para rodar sempre.
        [StructLayout(LayoutKind.Sequential)]
        struct ProcMem
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
                           QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GetProcessMemoryInfo(IntPtr h, ref ProcMem m, uint cb);
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

        long usageCpuPrev;
        DateTime usagePrevAt, usageLogAt = DateTime.Now;
        string usageText = "";

        static long CpuTime100ns()
        {
            GetProcessTimes(GetCurrentProcess(), out _, out _, out long k, out long u);
            return k + u;
        }

        static void Memory(out long privateMb, out long workingMb)
        {
            var m = new ProcMem { cb = (uint)Marshal.SizeOf<ProcMem>() };
            GetProcessMemoryInfo(GetCurrentProcess(), ref m, m.cb);
            privateMb = (long)(m.PrivateUsage.ToUInt64() >> 20);
            workingMb = (long)(m.WorkingSetSize.ToUInt64() >> 20);
        }

        void UpdateUsage()
        {
            try
            {
                var now = DateTime.Now;
                long cpuT = CpuTime100ns();
                if (usagePrevAt == default(DateTime)) { usageCpuPrev = cpuT; usagePrevAt = now; return; }
                if ((now - usagePrevAt).TotalSeconds < 1.5) return;
                double cpu = (cpuT - usageCpuPrev) / 10000.0 / (now - usagePrevAt).TotalMilliseconds / Environment.ProcessorCount * 100;
                usageCpuPrev = cpuT;
                usagePrevAt = now;
                bool log = (now - usageLogAt).TotalMinutes >= 5;
                if (uiIdle && !log) return; // na bandeja ninguem ve o numero: so o registro de 5 em 5 min
                Memory(out long ramMb, out long wsMb);
                usageText = $"Uso do PC: CPU {cpu:0.0}% · RAM {wsMb} MB";
                if (log)
                {
                    usageLogAt = now;
                    Log.Write($"Uso: CPU {cpu:0.0}% do PC · RAM {wsMb} MB em uso ({ramMb} MB reservada) · jogo em tela cheia: {(otherFullscreen ? "sim" : "não")}" +
                              (uiIdle ? " · na bandeja" : ""));
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- modo bandeja
        // Janela escondida ou minimizada: a interface quase nao acorda (nada de medidores/animacoes; timers lentos) e,
        // depois de uns segundos, devolve ao Windows a memoria que so a tela usava (o jogo fica com mais RAM livre).
        bool uiIdle;
        DispatcherTimer trimTimer;

        void InitIdleMode()
        {
            IsVisibleChanged += (s, e) => UpdateIdleMode();
            StateChanged += (s, e) => UpdateIdleMode();
            trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
            trimTimer.Tick += (s, e) =>
            {
                trimTimer.Stop();
                if (uiIdle) TrimMemory("na bandeja");
            };
            UpdateIdleMode();
        }

        void UpdateIdleMode()
        {
            bool idle = !snapshotMode && (!IsVisible || WindowState == WindowState.Minimized);
            if (idle == uiIdle && fastTimer.IsEnabled != idle) return;
            uiIdle = idle;
            if (idle)
            {
                if (!TestBusy) fastTimer.Stop();
                slowTimer.Interval = TimeSpan.FromSeconds(2);
                trimTimer.Stop();
                trimTimer.Start();
            }
            else
            {
                fastTimer.Start();
                slowTimer.Interval = TimeSpan.FromMilliseconds(700);
                trimTimer.Stop();
            }
            UpdateBackgroundTimers();
            UpdateRest(); // janela aberta: acorda na hora (medidores ao vivo)
        }

        /// <summary>Timers dos plugins e da voz com IA: rapidos so quando alguem esta olhando ou ha trabalho.</summary>
        void UpdateBackgroundTimers()
        {
            if (plugTimer != null)
            {
                var want = TimeSpan.FromMilliseconds(uiIdle && paramWindows.Count == 0 ? 250 : 40);
                if (plugTimer.Interval != want) plugTimer.Interval = want;
            }
            if (aiTimer != null)
            {
                bool aiBusy = P.VcOn && P.VcAiOn || aiVoice.State == "carregando";
                var want = TimeSpan.FromMilliseconds(aiBusy ? (uiIdle ? 500 : 250) : 1000);
                if (aiTimer.Interval != want) aiTimer.Interval = want;
            }
        }

        // ---------------------------------------------------------------- economia automatica (descanso)
        // Nenhum app usando o microfone virtual + janela escondida + sem retorno/teste/sons = ninguem ouve nada:
        // a IA anti-ruido e a voz com IA param (o jogo fica com o processador so para ele). O Windows avisa na hora
        // em que o Discord/jogo/OBS abre o microfone, e o TakaVox volta antes da primeira palavra.
        TakaVox.Audio.ListenWatcher listen;
        DateTime nobodySince = DateTime.MinValue;
        bool restLogged;

        void InitAutoRest()
        {
            if (snapshotMode) return;
            listen = new TakaVox.Audio.ListenWatcher();
            listen.Changed += started =>
            {
                if (started) engine.Resting = false; // acorda ja, na thread do aviso do Windows
                Dispatcher.BeginInvoke(new Action(UpdateRest));
            };
            BindListenDevice();
        }

        void BindListenDevice()
        {
            if (listen == null) return;
            try { listen.Watch(TakaVox.Audio.AudioDevices.FindCableOutput()?.Id); } catch { }
        }

        void UpdateRest()
        {
            if (listen == null || exiting) return;
            int n = listen.CountListening();
            bool busy = n != 0 /* -1 = nao da para saber: trabalha */ || !app.AutoRest || !uiIdle || app.MonitorOn || TestBusy ||
                        IsPlayingAny() || engine.Calib != null;
            if (busy)
            {
                nobodySince = DateTime.MinValue;
                if (engine.Resting) engine.Resting = false;
                if (restLogged)
                {
                    restLogged = false;
                    string who = "";
                    if (n > 0) listen.CountListening(true, out who);
                    Log.Write("Economia: voltou a processar" + (who.Length > 0 ? " (" + who + " abriu o microfone)" : "") + ".");
                }
                return;
            }
            if (nobodySince == DateTime.MinValue) nobodySince = DateTime.Now;
            if (!engine.Resting && (DateTime.Now - nobodySince).TotalSeconds >= 8)
            {
                engine.Resting = true;
                restLogged = true;
                Log.Write("Economia: nenhum app está usando o microfone virtual: o TakaVox descansa (sem IA rodando) até alguém abrir o mic.");
            }
        }

        /// <summary>Devolve ao Windows as paginas de memoria que ninguem esta usando (a tela escondida).</summary>
        void TrimMemory(string why)
        {
            try
            {
                Memory(out _, out long before);
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
                Memory(out _, out long after);
                Log.Write($"Memória: {before} MB -> {after} MB em uso ({why}).");
            }
            catch { }
        }
    }
}
