using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TakaVox.Interop;

namespace TakaVox.Audio
{
    // ---------------------------------------------------------------- sessoes de audio do Windows (quem esta gravando de um dispositivo)
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, ref Guid context);
        [PreserveSig] int OnGroupingParamChanged(ref Guid grouping, ref Guid context);
        [PreserveSig] int OnStateChanged(int state);
        [PreserveSig] int OnSessionDisconnected(int reason);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IAudioSessionControl2 session);
    }

    /// <summary>
    /// Vigia quem esta usando o microfone virtual (CABLE Output): Discord, jogos, OBS... O Windows avisa na hora em
    /// que um app abre ou fecha o microfone, e o TakaVox pode descansar (sem IA rodando) enquanto ninguem escuta.
    /// </summary>
    public sealed class ListenWatcher : IDisposable
    {
        const int Active = 1;

        [ComVisible(true)]
        sealed class Events : IAudioSessionEvents
        {
            readonly ListenWatcher owner;
            public Events(ListenWatcher o) { owner = o; }
            public int OnDisplayNameChanged(string name, ref Guid context) => 0;
            public int OnIconPathChanged(string path, ref Guid context) => 0;
            public int OnSimpleVolumeChanged(float volume, bool mute, ref Guid context) => 0;
            public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, ref Guid context) => 0;
            public int OnGroupingParamChanged(ref Guid grouping, ref Guid context) => 0;
            public int OnStateChanged(int state) { owner.Fire(state == Active); return 0; }
            public int OnSessionDisconnected(int reason) { owner.Fire(false); return 0; }
        }

        [ComVisible(true)]
        sealed class Notifier : IAudioSessionNotification
        {
            readonly ListenWatcher owner;
            public Notifier(ListenWatcher o) { owner = o; }
            public int OnSessionCreated(IAudioSessionControl2 session)
            {
                // um app novo abriu o microfone: acorda ja (registrar os avisos dele fica para fora deste callback)
                owner.newSession = true;
                owner.Fire(true);
                return 0;
            }
        }

        IAudioSessionManager2 manager;
        Notifier notifier;
        readonly List<KeyValuePair<IAudioSessionControl2, Events>> watched = new List<KeyValuePair<IAudioSessionControl2, Events>>();
        volatile bool newSession;
        string deviceId;
        static readonly int MyPid = Process.GetCurrentProcess().Id;

        /// <summary>Algum app comecou (true) ou parou (false) de usar o microfone virtual. Chamado em threads do Windows.</summary>
        public event Action<bool> Changed;

        /// <summary>Vigiando um dispositivo (se falso, o TakaVox nunca descansa: na duvida, trabalha).</summary>
        public bool Ok => manager != null;
        public string DeviceId => deviceId;

        void Fire(bool started)
        {
            try { Changed?.Invoke(started); } catch { }
        }

        /// <summary>Passa a vigiar o dispositivo de captura 'id' (null = parar).</summary>
        public void Watch(string id)
        {
            if (id == deviceId && manager != null) return;
            Stop();
            deviceId = id;
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var en = AudioDevices.NewEnumerator();
                if (en.GetDevice(id, out var dev) != 0 || dev == null) return;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o) != 0 || o == null) return;
                manager = (IAudioSessionManager2)o;
                // o Windows so manda avisos de sessao nova depois que alguem pede a lista uma vez
                manager.GetSessionEnumerator(out var first);
                if (first != null) Marshal.ReleaseComObject(first);
                notifier = new Notifier(this);
                if (manager.RegisterSessionNotification(notifier) != 0) notifier = null;
                Rescan();
            }
            catch (Exception ex)
            {
                Log.Write("Economia automática indisponível (" + ex.Message + "): o TakaVox fica sempre processando.");
                Stop();
            }
        }

        /// <summary>Quantos apps estao usando o microfone agora. Tambem passa a ouvir os avisos das sessoes novas.</summary>
        public int CountListening() => CountListening(false, out _);

        /// <summary>Quantos apps estao usando o microfone agora (e, se pedir, quais: so para o registro).</summary>
        public int CountListening(bool names, out string who)
        {
            who = "";
            if (manager == null) return -1;
            int n = 0;
            try
            {
                if (newSession) { newSession = false; Rescan(); }
                if (manager.GetSessionEnumerator(out var e) != 0 || e == null) return -1;
                try
                {
                    e.GetCount(out int count);
                    var list = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        if (e.GetSession(i, out var s) != 0 || s == null) continue;
                        try
                        {
                            if (s.GetState(out int st) != 0 || st != Active) continue;
                            s.GetProcessId(out uint pid);
                            if (pid == MyPid) continue;
                            n++;
                            if (names && list.Count < 3) list.Add(ProcName(pid));
                        }
                        finally { Marshal.ReleaseComObject(s); }
                    }
                    who = string.Join(", ", list);
                }
                finally { Marshal.ReleaseComObject(e); }
            }
            catch { return -1; }
            return n;
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);

        /// <summary>Nome do programa (barato: so abre o processo, sem listar todos os do Windows).</summary>
        static string ProcName(uint pid)
        {
            if (pid == 0) return "Windows";
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return "app " + pid;
            try
            {
                var sb = new System.Text.StringBuilder(512);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? System.IO.Path.GetFileNameWithoutExtension(sb.ToString()) : "app " + pid;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Ouve os avisos de cada sessao (comecou/parou de gravar).</summary>
        void Rescan()
        {
            if (manager == null) return;
            UnwatchSessions();
            if (manager.GetSessionEnumerator(out var e) != 0 || e == null) return;
            try
            {
                e.GetCount(out int count);
                for (int i = 0; i < count && i < 64; i++)
                {
                    if (e.GetSession(i, out var s) != 0 || s == null) continue;
                    var ev = new Events(this);
                    if (s.RegisterAudioSessionNotification(ev) == 0) watched.Add(new KeyValuePair<IAudioSessionControl2, Events>(s, ev));
                    else Marshal.ReleaseComObject(s);
                }
            }
            finally { Marshal.ReleaseComObject(e); }
        }

        void UnwatchSessions()
        {
            foreach (var kv in watched)
            {
                try { kv.Key.UnregisterAudioSessionNotification(kv.Value); } catch { }
                try { Marshal.ReleaseComObject(kv.Key); } catch { }
            }
            watched.Clear();
        }

        void Stop()
        {
            UnwatchSessions();
            if (manager != null && notifier != null) try { manager.UnregisterSessionNotification(notifier); } catch { }
            if (manager != null) try { Marshal.ReleaseComObject(manager); } catch { }
            manager = null;
            notifier = null;
        }

        public void Dispose() => Stop();
    }
}
