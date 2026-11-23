// Teste do hospedeiro de plugins (tvhost.dll) com os plugins de teste tvtest.vst3 e tvtest2.dll:
// escanear, carregar, processar, latencia, parametros, estado, janela original do plugin e travamento.
// A janela do plugin e testada EMBUTIDA (como no app) numa janela fora da tela que nao pega o foco
// (nao atrapalha quem esta usando o PC ou jogando).
// Uso: plugintest.exe <pasta com tvhost.dll, tvtest.vst3 e tvtest2.dll>
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TakaVox.Plugins;

/// <summary>Moldura de teste: fora da tela e sem pegar o foco.</summary>
sealed class QuietForm : Form
{
    protected override bool ShowWithoutActivation => true;
}

static unsafe class PluginTest
{
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string p);
    struct RECT { public int L, T, R, B; }
    const int WM_HSCROLL = 0x114, WM_CLOSE = 0x10, TBM_GETPOS = 0x400, TBM_SETPOS = 0x405, SB_ENDSCROLL = 8;

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }

    // processa blocos de 480 de um seno de 1 kHz (amplitude 0,5) e devolve entrada e saida
    static void Run(IntPtr h, int blocks, out float[] input, out float[] output, ref long phase)
    {
        input = new float[blocks * 480];
        output = new float[blocks * 480];
        var buf = new float[480];
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < 480; i++)
            {
                buf[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * (phase + i) / 48000.0);
                input[b * 480 + i] = buf[i];
            }
            phase += 480;
            fixed (float* p = buf) Tvh.tvh_process(h, p, 480);
            Array.Copy(buf, 0, output, b * 480, 480);
        }
    }

    // maior diferenca entre a saida e (entrada atrasada * ganho), ignorando o primeiro bloco
    static double Err(float[] inp, float[] outp, int delay, double gain)
    {
        double e = 0;
        for (int i = 480 + delay; i < outp.Length; i++) e = Math.Max(e, Math.Abs(outp[i] - gain * inp[i - delay]));
        return e;
    }

    static byte[] GetState(IntPtr h)
    {
        int n = Tvh.tvh_get_state(h, null, 0);
        if (n <= 0) return null;
        var b = new byte[n];
        return Tvh.tvh_get_state(h, b, n) == n ? b : null;
    }

    static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }

    [STAThread]
    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        LoadLibraryW(Path.Combine(dir, "tvhost.dll"));
        string v3 = Path.Combine(dir, "tvtest.vst3"), v2 = Path.Combine(dir, "tvtest2.dll");
        Check(Tvh.Init(out string ie), "hospedeiro iniciado " + ie);
        var err = new StringBuilder(512);
        var nm = new StringBuilder(256);
        var disp = new StringBuilder(256);
        double val, def;
        int steps;
        var buf = new float[480];
        int r;

        Console.WriteLine("[escanear]");
        var info = Tvh.Scan(v3, out string se);
        Check(info.Length == 2 && info[0].Name == "TV Teste Ganho" && info[1].Name == "TV Teste Inverte" && info[0].Format == 3 && info[0].Vendor == "TakaVox Teste",
            $"VST3: {info.Length} efeitos ({(info.Length > 0 ? info[0].Name : se)}, {(info.Length > 1 ? info[1].Name : "")}), fabricante '{(info.Length > 0 ? info[0].Vendor : "")}', categoria '{(info.Length > 0 ? info[0].Category : "")}'");
        info = Tvh.Scan(v2, out se);
        Check(info.Length == 1 && info[0].Name == "TV Teste VST2" && info[0].Format == 2 && info[0].IsInstrument == 0 && info[0].Vendor == "TakaVox Teste",
            $"VST2: {info.Length} efeito ({(info.Length > 0 ? info[0].Name : se)})");
        info = Tvh.Scan(Path.Combine(dir, "tvhost.dll"), out se);
        Check(info.Length == 0 && se != null, $"DLL que não é plugin recusada: \"{se}\"");
        info = Tvh.Scan(@"C:\Windows\SysWOW64\kernel32.dll", out se);
        Check(info.Length == 0 && se != null && se.Contains("32 bits"), $"DLL de 32 bits recusada sem carregar: \"{se}\"");
        info = Tvh.Scan(Path.Combine(dir, "nao_existe.vst3"), out se);
        Check(info.Length == 0 && se != null, $"arquivo inexistente: \"{se}\"");

        Console.WriteLine("[VST3: carregar e processar]");
        IntPtr h = Tvh.tvh_load(v3, 0, 48000, 480, null, 0, err, 512);
        Check(h != IntPtr.Zero, "carregou " + err);
        if (h == IntPtr.Zero) return 1;
        Check(Tvh.tvh_latency(h) == 64, "latência informada = " + Tvh.tvh_latency(h) + " amostras");
        Check(Tvh.tvh_channels(h) == 101, "aceitou mono → mono (" + Tvh.tvh_channels(h) + ")");
        long ph = 0;
        Run(h, 10, out var a, out var b, ref ph);
        Check(Err(a, b, 64, 1.0) < 1e-5, $"ganho 1x com atraso de 64: erro {Err(a, b, 64, 1.0):E1}");
        int pc = Tvh.tvh_param_count(h);
        int vis = Tvh.tvh_param_info(h, 0, nm, 256, disp, 256, out val, out steps, out def);
        Check(pc == 3 && vis == 1 && nm.ToString() == "Ganho" && Math.Abs(val - 0.5) < 1e-9 && Math.Abs(def - 0.5) < 1e-9,
            $"parâmetros: {pc}; [0] '{nm}' = {val} ('{disp}'), passos {steps}, padrão {def}");
        Check(Tvh.tvh_param_info(h, 1, nm, 256, disp, 256, out val, out steps, out def) == 0, "medidor (somente leitura) fica escondido");
        Tvh.tvh_param_info(h, 2, nm, 256, disp, 256, out val, out steps, out def);
        Check(nm.ToString() == "Travar" && steps == 1, $"parâmetro liga/desliga: '{nm}' passos {steps}");
        Tvh.tvh_param_set(h, 0, 0.25);
        Run(h, 6, out a, out b, ref ph);
        Check(Err(a, b, 64, 0.5) < 1e-5, $"parâmetro 0,25 → ganho 0,5x: erro {Err(a, b, 64, 0.5):E1}");
        byte[] st = GetState(h);
        Check(st != null && st.Length > 9, "estado salvo: " + (st?.Length ?? 0) + " bytes");
        Tvh.tvh_param_set(h, 0, 1.0);
        Run(h, 3, out a, out b, ref ph);
        Check(Err(a, b, 64, 2.0) < 1e-5, "parâmetro 1,0 → ganho 2x");
        Tvh.tvh_poll(h);
        Check(Tvh.tvh_set_state(h, st, st.Length) == 1, "estado restaurado");
        Run(h, 3, out a, out b, ref ph);
        Check(Err(a, b, 64, 0.5) < 1e-5, $"depois de restaurar: ganho 0,5x (erro {Err(a, b, 64, 0.5):E1})");
        Tvh.tvh_param_info(h, 0, nm, 256, disp, 256, out val, out steps, out def);
        Check(Math.Abs(val - 0.25) < 1e-9, "a interface do plugin também voltou para 0,25 (" + val + ")");
        Check((Tvh.tvh_poll(h) & Tvh.PendParams) != 0, "aviso de 'parâmetros mudaram' para o app atualizar a tela");
        Check(Tvh.tvh_set_state(h, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 10) == 0, "estado inválido recusado sem travar");
        Check(Tvh.tvh_reactivate(h) == 1 && Tvh.tvh_latency(h) == 64, "reativar (nova latência) funciona");

        IntPtr h2 = Tvh.tvh_load(v3, 1, 48000, 480, st, st.Length, err, 512);
        Check(h2 != IntPtr.Zero, "segundo efeito do mesmo arquivo (Inverte) carregado já com o estado salvo " + err);
        long ph2 = 0;
        Run(h2, 6, out a, out b, ref ph2);
        Check(Err(a, b, 64, -0.5) < 1e-5, $"Inverte + estado: ganho −0,5x (erro {Err(a, b, 64, -0.5):E1})");

        Console.WriteLine("[VST3: janela original do plugin, embutida]");
        Check(Tvh.tvh_has_editor(h) == 1, "tem janela própria");
        var owner = new QuietForm { Text = "moldura", ShowInTaskbar = false, Width = 600, Height = 400, StartPosition = FormStartPosition.Manual, Left = -20000, Top = 40 };
        owner.Show();
        Pump(100);
        IntPtr ed = Tvh.tvh_editor_create(h, owner.Handle, out int ew, out int eh);
        Pump(200);
        Check(ed != IntPtr.Zero && Tvh.tvh_editor_open(h) == 1, $"janela embutida aberta ({ew}×{eh})");
        GetClientRect(ed, out var rc);
        Check(rc.R == 420 && rc.B == 180, $"plugin pediu 420×180 e a janela ficou {rc.R}×{rc.B} (dpi {GetDpiForWindow(ed)})");
        Check(Tvh.tvh_editor_resizable(h) == 1, "plugin redimensionável reconhecido");
        IntPtr cont = FindWindowExW(ed, IntPtr.Zero, "TVTestEditor", null);
        IntPtr tb = cont == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(cont, IntPtr.Zero, "msctls_trackbar32", null);
        Check(tb != IntPtr.Zero, "controles do plugin dentro da janela");
        int pos = (int)SendMessageW(tb, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);
        Check(pos == 25, "slider do plugin mostra o valor atual (25) = " + pos);
        SendMessageW(tb, TBM_SETPOS, (IntPtr)1, (IntPtr)100);
        SendMessageW(cont, WM_HSCROLL, (IntPtr)SB_ENDSCROLL, tb); // como se arrastasse com o mouse
        Run(h, 4, out a, out b, ref ph);
        Check(Err(a, b, 64, 2.0) < 1e-5, $"mexer na janela do plugin muda o áudio: ganho 2x (erro {Err(a, b, 64, 2.0):E1})");
        Tvh.tvh_param_info(h, 0, nm, 256, disp, 256, out val, out steps, out def);
        Check(Math.Abs(val - 1.0) < 1e-9, "o app enxerga o valor novo (" + val + " = '" + disp + "')");
        Tvh.tvh_idle(h);
        Pump(50);
        IntPtr title = FindWindowExW(cont, IntPtr.Zero, "Static", null);
        IntPtr meter = FindWindowExW(cont, title, "Static", null);
        Check(Text(meter).StartsWith("Pico: 1.0"), "medidor do plugin recebe o que o áudio publicou: '" + Text(meter) + "'");
        Tvh.tvh_close_editor(h);
        Pump(50);
        Check(Tvh.tvh_editor_open(h) == 0 && FindWindowExW(ed, IntPtr.Zero, "TVTestEditor", null) == IntPtr.Zero, "plugin solto da moldura pelo app (tirar da cadeia)");
        DestroyWindow(ed);
        ed = Tvh.tvh_editor_create(h, owner.Handle, out ew, out eh);
        Pump(100);
        DestroyWindow(ed); // fechar a moldura destroi a janela filha
        Pump(50);
        Check(ed != IntPtr.Zero && Tvh.tvh_editor_open(h) == 0, "reabrir e fechar a moldura");
        Check(Tvh.tvh_program_count(h) == 0 && Tvh.tvh_get_program(h) == -1, "sem presets de fábrica: lista vazia");

        Console.WriteLine("[desempenho]");
        var sw = Stopwatch.StartNew();
        fixed (float* p = buf) for (int i = 0; i < 20000; i++) Tvh.tvh_process(h, p, 480);
        double us = sw.Elapsed.TotalMilliseconds * 1000 / 20000;
        Check(us < 50, $"custo do hospedeiro por bloco de 10 ms: {us:0.0} µs");

        Console.WriteLine("[proteção contra plugin que trava]");
        Tvh.tvh_param_set(h2, 2, 1.0); // "Travar"
        fixed (float* p = buf) r = Tvh.tvh_process(h2, p, 480);
        Check(r == -2 && Tvh.tvh_failed(h2) == 1, $"VST3 travou no processamento: retorno {r}; o app continuou de pé");
        fixed (float* p = buf) r = Tvh.tvh_process(h2, p, 480);
        Check(r == -1, "plugin travado fica desligado (não é chamado de novo)");
        Tvh.tvh_free(h2);
        Check(true, "plugin travado liberado sem derrubar o app");

        Console.WriteLine("[VST2]");
        IntPtr g = Tvh.tvh_load(v2, 0, 48000, 480, null, 0, err, 512);
        Check(g != IntPtr.Zero, "carregou " + err);
        Check(Tvh.tvh_channels(g) == 202 && Tvh.tvh_latency(g) == 0, "estéreo, latência 0 (" + Tvh.tvh_channels(g) + ")");
        ph = 0;
        Run(g, 6, out a, out b, ref ph);
        Check(Err(a, b, 0, 1.0) < 1e-5, "ganho 1x");
        Tvh.tvh_param_info(g, 0, nm, 256, disp, 256, out val, out steps, out def);
        Check(nm.ToString() == "Ganho" && disp.ToString() == "1.00 x", $"parâmetro '{nm}' = '{disp}'");
        Tvh.tvh_param_set(g, 0, 0.25);
        Run(g, 3, out a, out b, ref ph);
        Check(Err(a, b, 0, 0.5) < 1e-5, "parâmetro 0,25 → ganho 0,5x");
        st = GetState(g);
        Tvh.tvh_param_set(g, 0, 1);
        Check(st != null && Tvh.tvh_set_state(g, st, st.Length) == 1, "estado (chunk) salvo e restaurado");
        Run(g, 3, out a, out b, ref ph);
        Check(Err(a, b, 0, 0.5) < 1e-5, "depois de restaurar: ganho 0,5x");
        ed = Tvh.tvh_editor_create(g, owner.Handle, out ew, out eh);
        Pump(150);
        GetClientRect(ed, out rc);
        Check(ed != IntPtr.Zero && rc.R > 0, $"janela VST2 embutida {rc.R}×{rc.B} (pedido 400×170; dpi {GetDpiForWindow(ed)})");
        cont = FindWindowExW(ed, IntPtr.Zero, "TVTest2Editor", null);
        tb = cont == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(cont, IntPtr.Zero, "msctls_trackbar32", null);
        SendMessageW(tb, TBM_SETPOS, (IntPtr)1, (IntPtr)100);
        SendMessageW(cont, WM_HSCROLL, (IntPtr)SB_ENDSCROLL, tb);
        Run(g, 3, out a, out b, ref ph);
        Check(tb != IntPtr.Zero && Err(a, b, 0, 2.0) < 1e-5, "mexer na janela do VST2 muda o áudio (ganho 2x)");
        DestroyWindow(ed);
        Pump(50);
        Check(Tvh.tvh_editor_open(g) == 0, "janela VST2 fechada com a moldura");
        Tvh.tvh_param_set(g, 0, 0.123);
        fixed (float* p = buf) r = Tvh.tvh_process(g, p, 480);
        Check(r == -2 && Tvh.tvh_failed(g) == 1, "VST2 travou no processamento e o app continuou de pé");
        Tvh.tvh_free(g);

        Console.WriteLine("[PluginInstance (classe usada pelo app)]");
        var pi = PluginInstance.Load(v3, 0, "TakaVox Teste", st3(), out string le);
        Check(pi != null && pi.Name == "TV Teste Ganho" && pi.Latency == 64 && pi.HasEditor && pi.FormatName == "VST3", "carregou: " + (pi?.Name ?? le));
        var p0 = pi?.GetParam(0);
        Check(p0 != null && p0.Name == "Ganho" && !p0.IsBypass, "parâmetro 0: " + p0?.Name + " = " + p0?.Display);
        pi?.Free();
        Check(pi != null && !pi.Alive, "liberado");

        Tvh.tvh_free(h);
        owner.Close();
        Console.WriteLine(fails == 0 ? "TUDO OK" : "FALHAS: " + fails);
        return fails;
    }

    // estado VST3 valido do "TV Teste Ganho" com ganho 0,5 (TVS1 + formato 3 + 8 bytes do componente + 0 do controlador)
    static byte[] st3()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new[] { (byte)'T', (byte)'V', (byte)'S', (byte)'1', (byte)3 });
        w.Write(8); w.Write(0.5);
        w.Write(0);
        return ms.ToArray();
    }
}
