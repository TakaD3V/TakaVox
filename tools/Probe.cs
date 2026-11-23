// Ferramenta de diagnostico: lista dispositivos de audio, padroes, volumes, formatos e topologia (boost/AGC).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TakaVox.Interop;

static class Probe
{
    static IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();

    static string GetString(IPropertyStore ps, PropertyKey key)
    {
        var k = key;
        if (ps.GetValue(ref k, out var pv) != 0) return null;
        try { return pv.vt == PropVariant.VT_LPWSTR ? Marshal.PtrToStringUni(pv.pointerValue) : null; }
        finally { NativeMethods.PropVariantClear(ref pv); }
    }
    static uint? GetUInt(IPropertyStore ps, PropertyKey key)
    {
        var k = key;
        if (ps.GetValue(ref k, out var pv) != 0) return null;
        try { return (pv.vt == PropVariant.VT_UI4 || pv.vt == PropVariant.VT_I4) ? pv.uintValue : (uint?)null; }
        finally { NativeMethods.PropVariantClear(ref pv); }
    }
    static string GetBlobFormat(IPropertyStore ps, PropertyKey key)
    {
        var k = key;
        if (ps.GetValue(ref k, out var pv) != 0) return null;
        try
        {
            if (pv.vt != PropVariant.VT_BLOB || pv.blobData == IntPtr.Zero) return null;
            return FormatToString(pv.blobData);
        }
        finally { NativeMethods.PropVariantClear(ref pv); }
    }
    static string FormatToString(IntPtr p)
    {
        var wf = Marshal.PtrToStructure<WaveFormatEx>(p);
        string type = wf.wFormatTag.ToString();
        if (wf.wFormatTag == WaveFormatTags.Extensible && wf.cbSize >= 22)
        {
            var wfx = Marshal.PtrToStructure<WaveFormatExtensible>(p);
            type = wfx.SubFormat == WaveFormatTags.SubTypeIeeeFloat ? "float" : wfx.SubFormat == WaveFormatTags.SubTypePcm ? "pcm" : wfx.SubFormat.ToString();
            type += $" valid={wfx.wValidBitsPerSample} mask=0x{wfx.dwChannelMask:X}";
        }
        else if (wf.wFormatTag == WaveFormatTags.IeeeFloat) type = "float";
        else if (wf.wFormatTag == WaveFormatTags.Pcm) type = "pcm";
        return $"{wf.nSamplesPerSec} Hz, {wf.nChannels} ch, {wf.wBitsPerSample} bit {type}";
    }

    static string Id(IMMDevice d) { d.GetId(out var id); return id; }

    static void Main()
    {
        var defaults = new Dictionary<string, List<string>>();
        foreach (EDataFlow flow in new[] { EDataFlow.Capture, EDataFlow.Render })
            foreach (ERole role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                if (en.GetDefaultAudioEndpoint(flow, role, out var d) == 0)
                {
                    var id = Id(d);
                    if (!defaults.ContainsKey(id)) defaults[id] = new List<string>();
                    defaults[id].Add((flow == EDataFlow.Capture ? "REC-" : "PLAY-") + role);
                }
            }

        foreach (EDataFlow flow in new[] { EDataFlow.Capture, EDataFlow.Render })
        {
            Console.WriteLine();
            Console.WriteLine(flow == EDataFlow.Capture ? "==================== GRAVACAO (capture) ====================" : "==================== REPRODUCAO (render) ====================");
            HResult.Check(en.EnumAudioEndpoints(flow, DeviceState.All, out var col), "Enum");
            col.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                col.Item(i, out var dev);
                var id = Id(dev);
                dev.GetState(out var st);
                dev.OpenPropertyStore(ComConst.STGM_READ, out var ps);
                var name = GetString(ps, PKeys.DeviceFriendlyName);
                var ff = GetUInt(ps, PKeys.EndpointFormFactor);
                var en2 = GetString(ps, PKeys.DeviceEnumeratorName);
                var sysfx = GetUInt(ps, PKeys.EndpointDisableSysFx);
                var devFmt = GetBlobFormat(ps, PKeys.EngineDeviceFormat);
                string defs = defaults.ContainsKey(id) ? "  <== PADRAO: " + string.Join(",", defaults[id]) : "";
                Console.WriteLine($"- [{st}] {name}{defs}");
                if (st != DeviceState.Active) continue;
                Console.WriteLine($"    id={id}");
                Console.WriteLine($"    formFactor={ff} enumerator={en2} disableSysFx={sysfx}");
                Console.WriteLine($"    deviceFormat(engine)={devFmt}");
                try
                {
                    var iid = Iids.IAudioClient;
                    if (dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var acObj) == 0)
                    {
                        var ac = (IAudioClient)acObj;
                        if (ac.GetMixFormat(out var mf) == 0) { Console.WriteLine($"    mixFormat={FormatToString(mf)}"); Marshal.FreeCoTaskMem(mf); }
                        ac.GetDevicePeriod(out var defP, out var minP);
                        Console.WriteLine($"    period default={defP / 10000.0}ms min={minP / 10000.0}ms");
                        Marshal.ReleaseComObject(ac);
                    }
                }
                catch (Exception ex) { Console.WriteLine("    (audioclient erro: " + ex.Message + ")"); }

                try
                {
                    var iid = Iids.IAudioEndpointVolume;
                    if (dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var vObj) == 0)
                    {
                        var v = (IAudioEndpointVolume)vObj;
                        v.GetMasterVolumeLevelScalar(out var sc); v.GetMasterVolumeLevel(out var db);
                        v.GetVolumeRange(out var mn, out var mx, out var inc); v.GetMute(out var mute);
                        v.QueryHardwareSupport(out var hw); v.GetChannelCount(out var ch);
                        Console.WriteLine($"    volume={sc * 100:F0}% ({db:F2} dB) range=[{mn:F2} .. {mx:F2}] step={inc:F3} mute={mute} hwSupport=0x{hw:X} channels={ch}");
                    }
                }
                catch (Exception ex) { Console.WriteLine("    (volume erro: " + ex.Message + ")"); }

                if (flow == EDataFlow.Capture) DumpTopology(dev);
            }
        }
    }

    static void DumpTopology(IMMDevice dev)
    {
        try
        {
            var iid = Iids.IDeviceTopology;
            if (dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var tObj) != 0) { Console.WriteLine("    (sem topologia)"); return; }
            var topo = (IDeviceTopology)tObj;
            topo.GetConnectorCount(out var cc);
            for (uint c = 0; c < cc; c++)
            {
                topo.GetConnector(c, out var conn);
                if (conn.GetConnectedTo(out var other) != 0 || other == null) continue;
                var part = (IPart)other;
                var seen = new HashSet<string>();
                Walk(part, 0, seen);
            }
        }
        catch (Exception ex) { Console.WriteLine("    (topologia erro: " + ex.Message + ")"); }
    }

    static void Walk(IPart part, int depth, HashSet<string> seen)
    {
        if (depth > 12) return;
        part.GetGlobalId(out var gid);
        if (gid == null || !seen.Add(gid)) return;
        part.GetName(out var name); part.GetPartType(out var ptype); part.GetSubType(out var sub);
        string extra = "";
        var iid = Iids.IAudioVolumeLevel;
        if (part.Activate(ComConst.CLSCTX_ALL, ref iid, out var volObj) == 0 && volObj != null)
        {
            var vl = (IAudioVolumeLevel)volObj;
            vl.GetChannelCount(out var chs);
            if (chs > 0 && vl.GetLevelRange(0, out var mn, out var mx, out var stp) == 0)
            {
                vl.GetLevel(0, out var lvl);
                extra += $" [VOLUME level={lvl:F1}dB range={mn:F1}..{mx:F1} step={stp:F1}]";
            }
        }
        iid = Iids.IAudioAutoGainControl;
        if (part.Activate(ComConst.CLSCTX_ALL, ref iid, out var agcObj) == 0 && agcObj != null)
        {
            ((IAudioAutoGainControl)agcObj).GetEnabled(out var en);
            extra += $" [AGC enabled={en}]";
        }
        Console.WriteLine($"    {new string(' ', depth * 2)}part: '{name}' type={(ptype == 0 ? "Connector" : "Subunit")} sub={sub}{extra}");
        if (part.EnumPartsIncoming(out var list) == 0 && list != null)
        {
            list.GetCount(out var pc);
            for (uint i = 0; i < pc; i++) { list.GetPart(i, out var p2); Walk(p2, depth + 1, seen); }
        }
    }
}
