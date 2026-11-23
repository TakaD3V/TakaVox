using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TakaVox.Interop;

namespace TakaVox.Audio
{
    public sealed class DeviceInfo
    {
        public string Id;
        public string Name;
        public bool IsCapture;
        public override string ToString() => Name;
    }

    public sealed class VolumeInfo
    {
        public float Scalar, Db, MinDb, MaxDb;
        public bool Mute;
    }

    /// <summary>Funcoes de alto nivel sobre os dispositivos de audio do Windows.</summary>
    public static class AudioDevices
    {
        internal static IMMDeviceEnumerator NewEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();

        static string ReadString(IPropertyStore ps, PropertyKey key)
        {
            var k = key;
            if (ps.GetValue(ref k, out var pv) != 0) return null;
            try { return pv.vt == PropVariant.VT_LPWSTR ? Marshal.PtrToStringUni(pv.pointerValue) : null; }
            finally { NativeMethods.PropVariantClear(ref pv); }
        }

        internal static string GetName(IMMDevice d)
        {
            try
            {
                if (d.OpenPropertyStore(ComConst.STGM_READ, out var ps) != 0) return "?";
                try { return ReadString(ps, PKeys.DeviceFriendlyName) ?? "?"; }
                finally { Marshal.ReleaseComObject(ps); }
            }
            catch { return "?"; }
        }

        public static List<DeviceInfo> List(bool capture)
        {
            var result = new List<DeviceInfo>();
            var en = NewEnumerator();
            try
            {
                if (en.EnumAudioEndpoints(capture ? EDataFlow.Capture : EDataFlow.Render, DeviceState.Active, out var col) != 0) return result;
                col.GetCount(out var n);
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var d) != 0) continue;
                    d.GetId(out var id);
                    result.Add(new DeviceInfo { Id = id, Name = GetName(d), IsCapture = capture });
                    Marshal.ReleaseComObject(d);
                }
                Marshal.ReleaseComObject(col);
            }
            finally { Marshal.ReleaseComObject(en); }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        /// <summary>Tipo do dispositivo: 1 = alto-falante, 3 = fone, 5 = headset, 10 = desconhecido (-1 = erro).</summary>
        public static int GetFormFactor(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            var en = NewEnumerator();
            try
            {
                if (en.GetDevice(id, out var d) != 0 || d == null) return -1;
                try
                {
                    if (d.OpenPropertyStore(ComConst.STGM_READ, out var ps) != 0) return -1;
                    var k = PKeys.EndpointFormFactor;
                    int r = -1;
                    if (ps.GetValue(ref k, out var pv) == 0)
                    {
                        if (pv.vt == PropVariant.VT_UI4 || pv.vt == PropVariant.VT_I4) r = (int)pv.uintValue;
                        NativeMethods.PropVariantClear(ref pv);
                    }
                    Marshal.ReleaseComObject(ps);
                    return r;
                }
                finally { Marshal.ReleaseComObject(d); }
            }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static bool IsActive(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var en = NewEnumerator();
            try
            {
                if (en.GetDevice(id, out var d) != 0 || d == null) return false;
                d.GetState(out var st);
                Marshal.ReleaseComObject(d);
                return st == DeviceState.Active;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static string GetDefaultId(bool capture, ERole role)
        {
            var en = NewEnumerator();
            try
            {
                if (en.GetDefaultAudioEndpoint(capture ? EDataFlow.Capture : EDataFlow.Render, role, out var d) != 0 || d == null) return null;
                d.GetId(out var id);
                Marshal.ReleaseComObject(d);
                return id;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static string GetNameById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var en = NewEnumerator();
            try
            {
                if (en.GetDevice(id, out var d) != 0 || d == null) return null;
                var n = GetName(d);
                Marshal.ReleaseComObject(d);
                return n;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(en); }
        }

        /// <summary>Define o dispositivo padrao do Windows (mesmo metodo usado pelo painel de som).</summary>
        public static bool SetDefault(string id, params ERole[] roles)
        {
            if (string.IsNullOrEmpty(id)) return false;
            object o = null;
            try
            {
                o = new PolicyConfigClientCo();
                var pc = (IPolicyConfig)o;
                bool ok = true;
                foreach (var r in roles) ok &= pc.SetDefaultEndpoint(id, r) == 0;
                return ok;
            }
            catch { return false; }
            finally { if (o != null) Marshal.ReleaseComObject(o); }
        }

        /// <summary>"Microfone (4- M8)" -> "Microfone (M8)": o Windows numera de novo quando muda a porta USB.</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return Regex.Replace(name, @"\(\d+-\s*", "(").Trim();
        }

        public static DeviceInfo FindByName(bool capture, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string norm = NormalizeName(name);
            foreach (var d in List(capture)) if (d.Name == name) return d;
            foreach (var d in List(capture)) if (NormalizeName(d.Name) == norm) return d;
            return null;
        }

        public static bool IsVirtualName(string name)
        {
            if (name == null) return true;
            string n = name.ToLowerInvariant();
            return n.Contains("cable") || n.Contains("vb-audio") || n.Contains("voicemeeter") || n.Contains("sonar") ||
                   n.Contains("droidcam") || n.Contains("broadcast") || n.Contains("steam streaming") || n.Contains("virtual");
        }

        /// <summary>Entrada do cabo virtual (onde o TakaVox toca a voz tratada).</summary>
        public static DeviceInfo FindCableInput()
        {
            foreach (var d in List(false))
                if (d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 && d.Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return null;
        }

        /// <summary>Saida do cabo virtual (o "microfone" que Discord/jogos usam).</summary>
        public static DeviceInfo FindCableOutput()
        {
            foreach (var d in List(true))
                if (d.Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0 && d.Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return null;
        }

        /// <summary>Escolhe o microfone fisico mais provavel (ignora dispositivos virtuais).</summary>
        public static DeviceInfo GuessPhysicalMic(string preferredName)
        {
            var byName = FindByName(true, preferredName);
            if (byName != null && !IsVirtualName(byName.Name)) return byName;
            var list = List(true);
            string def = GetDefaultId(true, ERole.Console);
            foreach (var d in list) if (d.Id == def && !IsVirtualName(d.Name)) return d;
            foreach (var d in list) if (!IsVirtualName(d.Name)) return d;
            return null;
        }

        static T ActivateOn<T>(string id, Guid iid) where T : class
        {
            var en = NewEnumerator();
            try
            {
                if (en.GetDevice(id, out var d) != 0 || d == null) return null;
                var g = iid;
                int hr = d.Activate(ref g, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o);
                Marshal.ReleaseComObject(d);
                return hr == 0 ? o as T : null;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static VolumeInfo GetVolume(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var v = ActivateOn<IAudioEndpointVolume>(id, Iids.IAudioEndpointVolume);
            if (v == null) return null;
            try
            {
                var info = new VolumeInfo();
                v.GetMasterVolumeLevelScalar(out info.Scalar);
                v.GetMasterVolumeLevel(out info.Db);
                v.GetVolumeRange(out info.MinDb, out info.MaxDb, out _);
                v.GetMute(out var m); info.Mute = m != 0;
                return info;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(v); }
        }

        public static bool SetVolumeScalar(string id, float scalar)
        {
            var v = ActivateOn<IAudioEndpointVolume>(id, Iids.IAudioEndpointVolume);
            if (v == null) return false;
            try { var ctx = Guid.Empty; return v.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, scalar)), ref ctx) == 0; }
            catch { return false; }
            finally { Marshal.ReleaseComObject(v); }
        }

        public static bool SetMute(string id, bool mute)
        {
            var v = ActivateOn<IAudioEndpointVolume>(id, Iids.IAudioEndpointVolume);
            if (v == null) return false;
            try { var ctx = Guid.Empty; return v.SetMute(mute ? 1 : 0, ref ctx) == 0; }
            catch { return false; }
            finally { Marshal.ReleaseComObject(v); }
        }

        /// <summary>Cria um WAVEFORMATEXTENSIBLE float32 (memoria CoTaskMem: liberar com FreeCoTaskMem).</summary>
        internal static IntPtr MakeFloatFormat(int rate, int channels)
        {
            var f = new WaveFormatExtensible();
            f.Format.wFormatTag = WaveFormatTags.Extensible;
            f.Format.nChannels = (ushort)channels;
            f.Format.nSamplesPerSec = (uint)rate;
            f.Format.wBitsPerSample = 32;
            f.Format.nBlockAlign = (ushort)(4 * channels);
            f.Format.nAvgBytesPerSec = (uint)(rate * 4 * channels);
            f.Format.cbSize = 22;
            f.wValidBitsPerSample = 32;
            f.dwChannelMask = channels == 1 ? 0x4u : channels == 2 ? 0x3u : 0u;
            f.SubFormat = WaveFormatTags.SubTypeIeeeFloat;
            IntPtr p = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatExtensible>());
            Marshal.StructureToPtr(f, p, false);
            return p;
        }

        internal static void ReadFormat(IntPtr p, out int rate, out int channels, out bool isFloat, out int bits)
        {
            var wf = Marshal.PtrToStructure<WaveFormatEx>(p);
            rate = (int)wf.nSamplesPerSec; channels = wf.nChannels; bits = wf.wBitsPerSample;
            isFloat = wf.wFormatTag == WaveFormatTags.IeeeFloat;
            if (wf.wFormatTag == WaveFormatTags.Extensible && wf.cbSize >= 22)
                isFloat = Marshal.PtrToStructure<WaveFormatExtensible>(p).SubFormat == WaveFormatTags.SubTypeIeeeFloat;
        }
    }
}
