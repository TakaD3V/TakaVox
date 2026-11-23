using System;
using System.Runtime.InteropServices;
using System.Threading;
using TakaVox.Interop;

namespace TakaVox.Audio
{
    /// <summary>
    /// Saida WASAPI (modo compartilhado, por evento) numa thread propria de alta prioridade.
    /// Se o dispositivo sumir, tenta de novo sozinha a cada 1,5 s.
    /// </summary>
    public sealed class RenderOutput : IDisposable
    {
        public string DeviceId { get; }
        public string Label { get; }
        readonly ISampleSource source;
        readonly Thread thread;
        readonly IntPtr evt;
        volatile bool stop;

        public volatile float Volume = 1f;
        public volatile bool Running;
        public int SilenceInserted;
        public volatile string Status = "iniciando";
        public double BufferMs { get; private set; }

        readonly double padPeriods;

        public RenderOutput(string deviceId, ISampleSource src, string label, double padPeriods = 2.0)
        {
            DeviceId = deviceId;
            source = src;
            Label = label;
            this.padPeriods = Math.Max(1.25, padPeriods);
            evt = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
            thread = new Thread(Run) { IsBackground = true, Name = "TakaVox saida " + label, Priority = ThreadPriority.Highest };
            thread.Start();
        }

        void Run()
        {
            uint task = 0;
            IntPtr avrt = IntPtr.Zero;
            try { avrt = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref task); } catch { }
            PowerHints.PreferQuietCore(1);
            try
            {
                while (!stop)
                {
                    try { RunOnce(); }
                    catch (Exception ex)
                    {
                        Running = false;
                        Status = DescribeError(ex);
                        Log.Write($"Saída '{Label}': {ex.Message}");
                    }
                    for (int i = 0; i < 15 && !stop; i++) Thread.Sleep(100);
                }
            }
            finally
            {
                if (avrt != IntPtr.Zero) try { NativeMethods.AvRevertMmThreadCharacteristics(avrt); } catch { }
            }
        }

        internal static string DescribeError(Exception ex)
        {
            if (ex is COMException ce)
            {
                switch (ce.ErrorCode)
                {
                    case HResult.AUDCLNT_E_DEVICE_INVALIDATED: return "dispositivo desconectado";
                    case HResult.AUDCLNT_E_DEVICE_IN_USE: return "dispositivo em uso exclusivo por outro programa";
                    case HResult.AUDCLNT_E_SERVICE_NOT_RUNNING: return "serviço de áudio do Windows parado";
                    case unchecked((int)0x80070490): return "dispositivo não encontrado";
                }
            }
            return ex.Message;
        }

        void RunOnce()
        {
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioClient ac = null; IAudioRenderClient rc = null;
            IntPtr fmt = IntPtr.Zero;
            bool started = false;
            try
            {
                en = AudioDevices.NewEnumerator();
                HResult.Check(en.GetDevice(DeviceId, out dev), "Abrir dispositivo");
                dev.GetState(out var state);
                if (state != DeviceState.Active) throw new COMException("dispositivo não está ativo", unchecked((int)0x80070490));
                var iid = Iids.IAudioClient;
                HResult.Check(dev.Activate(ref iid, ComConst.CLSCTX_ALL, IntPtr.Zero, out var o), "Ativar saída");
                ac = (IAudioClient)o;

                fmt = AudioDevices.MakeFloatFormat(48000, 2);
                var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
                HResult.Check(ac.Initialize(AudioClientShareMode.Shared, flags, 300000, 0, fmt, IntPtr.Zero), "Inicializar saída");
                HResult.Check(ac.GetBufferSize(out uint bufFrames), "Tamanho do buffer");
                ac.GetDevicePeriod(out long defPeriod, out _);
                int periodFrames = (int)Math.Max(96, Math.Min(1440, defPeriod * 48000 / 10000000));
                HResult.Check(ac.SetEventHandle(evt), "Evento");
                var iidR = Iids.IAudioRenderClient;
                HResult.Check(ac.GetService(ref iidR, out var ro), "Cliente de saída");
                rc = (IAudioRenderClient)ro;

                int targetPad = (int)Math.Min(bufFrames, (uint)Math.Round(periodFrames * padPeriods));
                BufferMs = targetPad / 48.0;

                // pre-enche com silencio para comecar sem estalo
                if (rc.GetBuffer((uint)targetPad, out var p0) == 0) rc.ReleaseBuffer((uint)targetPad, AudioClientBufferFlags.Silent);

                HResult.Check(ac.Start(), "Iniciar saída");
                started = true;
                Running = true;
                Status = "ok";
                var mono = new float[bufFrames];
                int silentWaits = 0;

                while (!stop)
                {
                    uint w = NativeMethods.WaitForSingleObject(evt, 200);
                    if (stop) break;
                    if (w == NativeMethods.WAIT_TIMEOUT)
                    {
                        // o dispositivo parou de pedir audio (ex.: driver travou) -> reinicia
                        if (++silentWaits > 10) throw new Exception("o dispositivo de saída parou de responder");
                        continue;
                    }
                    silentWaits = 0;
                    HResult.Check(ac.GetCurrentPadding(out uint pad), "Padding");
                    int want = targetPad - (int)pad;
                    if (want <= 0) continue;
                    want = Math.Min(want, (int)(bufFrames - pad));
                    int got = source.Read(mono, want);
                    if (got < want)
                    {
                        // faltou audio: o proprio buffer do dispositivo segura; so completa com
                        // silencio se ele fosse ficar com menos de 1 periodo (evita travar o driver)
                        int need = periodFrames - (int)pad - got;
                        if (need > 0)
                        {
                            Array.Clear(mono, got, Math.Min(need, want - got));
                            got += Math.Min(need, want - got);
                            SilenceInserted++;
                        }
                    }
                    if (got <= 0) continue;
                    want = got;
                    HResult.Check(rc.GetBuffer((uint)want, out IntPtr data), "GetBuffer saída");
                    float vol = Volume;
                    unsafe
                    {
                        float* dst = (float*)data;
                        for (int i = 0; i < want; i++)
                        {
                            float s = mono[i] * vol;
                            dst[2 * i] = s;
                            dst[2 * i + 1] = s;
                        }
                    }
                    HResult.Check(rc.ReleaseBuffer((uint)want, AudioClientBufferFlags.None), "ReleaseBuffer saída");
                }
            }
            finally
            {
                Running = false;
                if (started) try { ac.Stop(); } catch { }
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (rc != null) Marshal.ReleaseComObject(rc);
                if (ac != null) Marshal.ReleaseComObject(ac);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        public void Dispose()
        {
            stop = true;
            NativeMethods.SetEvent(evt);
            if (!thread.Join(3000)) Log.Write($"Saída '{Label}' demorou para fechar");
            NativeMethods.CloseHandle(evt);
        }
    }
}
