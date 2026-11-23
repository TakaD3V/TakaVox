using System;
using System.Runtime.InteropServices;

namespace TakaVox.Audio
{
    /// <summary>
    /// Pede ao Windows 11 para NAO colocar o app em "modo eficiencia" (clock baixo em segundo plano):
    /// a IA precisa terminar cada bloco de 10 ms a tempo, mesmo com o app minimizado na bandeja.
    /// </summary>
    internal static class PowerHints
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PowerState { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerState s, int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetThreadInformation(IntPtr h, int cls, ref PowerState s, int size);

        public static void DisableProcessThrottling()
        {
            // ProcessPowerThrottling = 4; EXECUTION_SPEED (1) | IGNORE_TIMER_RESOLUTION (4); StateMask 0 = nao limitar
            var s = new PowerState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
            try { SetProcessInformation(GetCurrentProcess(), 4, ref s, Marshal.SizeOf<PowerState>()); } catch { }
        }

        [DllImport("kernel32.dll")] static extern uint SetThreadIdealProcessor(IntPtr thread, uint processor);

        /// <summary>
        /// Sugere ao Windows rodar esta thread de audio no ULTIMO nucleo (slot 0 e 1 = os dois processadores logicos dele).
        /// Os jogos ocupam primeiro os nucleos "preferidos" (os primeiros): assim o audio quase nunca disputa nucleo com o
        /// jogo. E so uma preferencia: se o ultimo nucleo estiver ocupado, o Windows usa outro livre.
        /// </summary>
        public static void PreferQuietCore(int slot)
        {
            int n = Environment.ProcessorCount;
            if (n < 4) return;
            try { SetThreadIdealProcessor(GetCurrentThread(), (uint)(n - 1 - (slot & 1))); } catch { }
        }

        public static void DisableThreadThrottling()
        {
            // ThreadPowerThrottling = 3; EXECUTION_SPEED (1)
            var s = new PowerState { Version = 1, ControlMask = 0x1, StateMask = 0 };
            try { SetThreadInformation(GetCurrentThread(), 3, ref s, Marshal.SizeOf<PowerState>()); } catch { }
        }
    }
}
