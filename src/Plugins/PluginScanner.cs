using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TakaVox.Plugins
{
    /// <summary>
    /// Procura plugins nas pastas. Cada arquivo novo/alterado e lido num processo separado
    /// (TakaVox.exe --scan-plugins): se um plugin travar ao ser lido, so esse processo cai.
    /// </summary>
    static class PluginScanner
    {
        static readonly int HangSeconds = int.TryParse(Environment.GetEnvironmentVariable("TAKAVOX_SCAN_TIMEOUT"), out int t) && t > 0 ? t : 25;

        public static List<PluginEntry> Scan(List<string> folders, List<PluginEntry> cache, Action<string> progress)
        {
            var found = new List<KeyValuePair<string, long>>();
            var results = new List<PluginEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders) Walk(f, 0, found, results, seen);

            var byPath = cache.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var todo = new List<string>();
            var stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in found)
            {
                stamps[kv.Key] = kv.Value;
                if (byPath.TryGetValue(kv.Key, out var list) && list.All(e => e.Stamp == kv.Value)) results.AddRange(list);
                else todo.Add(kv.Key);
            }
            if (todo.Count > 0) results.AddRange(RunChild(todo, stamps, progress));
            foreach (var e in results) PluginManager.ApplyBundledRules(e);
            results.Sort((a, b) =>
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }

        static void Walk(string dir, int depth, List<KeyValuePair<string, long>> found, List<PluginEntry> results, HashSet<string> seen)
        {
            if (depth > 8 || string.IsNullOrEmpty(dir)) return;
            string[] subdirs, files;
            try
            {
                if (!Directory.Exists(dir)) return;
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { return; }
            foreach (var f in files)
            {
                if (!seen.Add(f)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".vst3") found.Add(new KeyValuePair<string, long>(f, Stamp(f)));
                else if (ext == ".dll")
                {
                    if (!PeFile.Read(f, out ushort machine, out bool vst2, out bool _)) continue;
                    if (!vst2) continue; // dependencia/biblioteca comum, nao e plugin
                    if (machine == 0x14c)
                    {
                        results.Add(new PluginEntry
                        {
                            Path = f, Name = Path.GetFileNameWithoutExtension(f), Format = 2, Stamp = Stamp(f),
                            Error = "plugin de 32 bits: o TakaVox é 64 bits e só abre plugins de 64 bits",
                        });
                        continue;
                    }
                    if (machine != 0x8664) continue;
                    if (KnownInstrument(f, out var entry)) { results.Add(entry); continue; }
                    found.Add(new KeyValuePair<string, long>(f, Stamp(f)));
                }
            }
            foreach (var d in subdirs)
            {
                if (!seen.Add(d)) continue;
                if (d.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) { found.Add(new KeyValuePair<string, long>(d, Stamp(d))); continue; }
                try { if (depth > 0 && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                Walk(d, depth + 1, found, results, seen);
            }
        }

        /// <summary>Pontes de instrumento conhecidas (carregar so para descobrir que e instrumento e pesado).</summary>
        static bool KnownInstrument(string f, out PluginEntry e)
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (n.StartsWith("FL Studio VSTi", StringComparison.OrdinalIgnoreCase))
            {
                e = new PluginEntry { Path = f, Name = n, Vendor = "Image-Line", Category = "Instrument", Format = 2, IsInstrument = true, Stamp = Stamp(f) };
                return true;
            }
            e = null;
            return false;
        }

        static long Stamp(string path)
        {
            try
            {
                string p = path.TrimEnd('\\', '/');
                if (Directory.Exists(p))
                {
                    string bin = Path.Combine(p, "Contents", "x86_64-win", Path.GetFileName(p));
                    if (File.Exists(bin)) p = bin;
                    else return Directory.GetLastWriteTimeUtc(p).Ticks;
                }
                var fi = new FileInfo(p);
                return fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 20);
            }
            catch { return 0; }
        }

        static List<PluginEntry> RunChild(List<string> todo, Dictionary<string, long> stamps, Action<string> progress)
        {
            var results = new List<PluginEntry>();
            var queue = new List<string>(todo);
            int total = todo.Count;
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            string listFile = Path.Combine(Path.GetTempPath(), "takavox_scan_" + tag + ".txt");
            string outFile = Path.Combine(Path.GetTempPath(), "takavox_scan_" + tag + ".out");
            try
            {
                while (queue.Count > 0)
                {
                    File.WriteAllLines(listFile, queue, new UTF8Encoding(false));
                    try { File.Delete(outFile); } catch { }
                    bool hung = false;
                    var psi = new ProcessStartInfo(exe, $"--scan-plugins \"{listFile}\" \"{outFile}\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe),
                    };
                    using (var proc = Process.Start(psi))
                    {
                        var idle = Stopwatch.StartNew();
                        int lastCount = -1;
                        while (!proc.WaitForExit(250))
                        {
                            var now = ReadLines(outFile);
                            if (now.Count != lastCount)
                            {
                                lastCount = now.Count;
                                idle.Restart();
                                string cur = now.LastOrDefault(l => l.StartsWith("B\t"));
                                if (cur != null)
                                {
                                    int doneHere = now.Count(l => l.StartsWith("D\t"));
                                    int doneTotal = total - queue.Count + doneHere;
                                    progress?.Invoke($"Lendo plugins… {Math.Min(total, doneTotal + 1)} de {total}: {Path.GetFileName(cur.Substring(2).TrimEnd('\\'))}");
                                }
                            }
                            if (idle.Elapsed.TotalSeconds > HangSeconds)
                            {
                                hung = true;
                                try { proc.Kill(); } catch { }
                                proc.WaitForExit(3000);
                                break;
                            }
                        }
                    }

                    var lines = ReadLines(outFile);
                    var started = new List<string>();
                    var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var withEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string fatal = null;
                    foreach (var line in lines)
                    {
                        var f = line.Split('\t');
                        if (f.Length < 2) continue;
                        switch (f[0])
                        {
                            case "B": started.Add(f[1]); break;
                            case "D": finished.Add(f[1]); break;
                            case "X": errors[f[1]] = f.Length > 2 ? f[2] : "erro"; break;
                            case "F": fatal = f[1]; break;
                            case "E":
                                if (f.Length < 8) break;
                                withEffects.Add(f[1]);
                                results.Add(new PluginEntry
                                {
                                    Path = f[1], Format = PluginManager.Int(f[2]), ClassIndex = PluginManager.Int(f[3]), IsInstrument = f[4] == "1",
                                    Name = f[5].Length > 0 ? f[5] : Path.GetFileNameWithoutExtension(f[1].TrimEnd('\\')), Vendor = f[6], Category = f[7],
                                    Stamp = stamps.TryGetValue(f[1], out long st) ? st : 0,
                                });
                                break;
                        }
                    }
                    if (fatal != null || started.Count == 0)
                    {
                        // o processo de leitura nem comecou: marca o resto e para
                        foreach (var p in queue)
                            results.Add(ErrorEntry(p, stamps, "não deu para ler (" + (fatal ?? "o leitor de plugins não iniciou") + ")", false));
                        break;
                    }
                    foreach (var p in started)
                    {
                        if (withEffects.Contains(p)) continue;
                        bool isVst3 = p.TrimEnd('\\', '/').EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);
                        if (finished.Contains(p))
                        {
                            string msg = errors.TryGetValue(p, out var m) ? m : "o arquivo não tem nenhum efeito de áudio";
                            bool notPlugin = !isVst3 && (msg.StartsWith("não é um plugin VST") || msg.StartsWith("não deu para carregar"));
                            results.Add(ErrorEntry(p, stamps, msg, notPlugin));
                        }
                        else
                        {
                            string msg = hung ? "demorou demais para responder ao ser lido" : "o plugin travou ao ser lido";
                            results.Add(ErrorEntry(p, stamps, msg, false));
                            Log.Write($"Plugins: '{p}' {msg}");
                        }
                    }
                    queue.RemoveAll(p => started.Contains(p, StringComparer.OrdinalIgnoreCase));
                }
            }
            finally
            {
                try { File.Delete(listFile); } catch { }
                try { File.Delete(outFile); } catch { }
            }
            return results;
        }

        static PluginEntry ErrorEntry(string p, Dictionary<string, long> stamps, string msg, bool hidden) => new PluginEntry
        {
            Path = p, Name = Path.GetFileNameWithoutExtension(p.TrimEnd('\\', '/')),
            Format = p.TrimEnd('\\', '/').EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) ? 3 : 2,
            Error = msg, Hidden = hidden, Stamp = stamps.TryGetValue(p, out long st) ? st : 0,
        };

        static List<string> ReadLines(string path)
        {
            var l = new List<string>();
            try
            {
                if (!File.Exists(path)) return l;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string s;
                    while ((s = sr.ReadLine()) != null) l.Add(s);
                }
            }
            catch { }
            return l;
        }

        // ================================================================== processo de leitura

        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);

        /// <summary>TakaVox.exe --scan-plugins lista saida: le cada plugin da lista e escreve o resultado linha a linha.</summary>
        public static int ChildMain(string listFile, string outFile)
        {
            SetErrorMode(0x0001 | 0x0002 | 0x8000); // sem janelas de erro do Windows se um plugin travar
            string[] paths;
            try { paths = File.ReadAllLines(listFile, Encoding.UTF8); }
            catch { return 3; }
            using (var w = new StreamWriter(new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true })
            {
                if (!Tvh.Init(out string ie)) { w.WriteLine("F\t" + PluginManager.Clean(ie)); return 2; }
                foreach (var p in paths)
                {
                    if (p.Trim().Length == 0) continue;
                    w.WriteLine("B\t" + p);
                    TvhInfo[] info;
                    string err;
                    try { info = Tvh.Scan(p, out err); }
                    catch (Exception ex) { info = new TvhInfo[0]; err = ex.Message; }
                    if (err != null) w.WriteLine("X\t" + p + "\t" + PluginManager.Clean(err));
                    foreach (var i in info)
                        w.WriteLine("E\t" + p + "\t" + i.Format + "\t" + i.ClassIndex + "\t" + i.IsInstrument + "\t" +
                                    PluginManager.Clean(i.Name) + "\t" + PluginManager.Clean(i.Vendor) + "\t" + PluginManager.Clean(i.Category));
                    w.WriteLine("D\t" + p);
                }
            }
            return 0;
        }
    }

    /// <summary>Le o cabecalho de uma DLL sem carregar: arquitetura e se exporta as funcoes de plugin.</summary>
    static class PeFile
    {
        public static bool Read(string path, out ushort machine, out bool vst2Entry, out bool vst3Entry)
        {
            machine = 0;
            vst2Entry = vst3Entry = false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return false;
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe > fs.Length - 24) return false;
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x4550) return false;
                    machine = br.ReadUInt16();
                    int nSect = br.ReadUInt16();
                    fs.Position += 12;
                    int optSize = br.ReadUInt16();
                    fs.Position += 2;
                    long opt = fs.Position;
                    bool plus = br.ReadUInt16() == 0x20B;
                    fs.Position = opt + (plus ? 112 : 96);
                    uint expRva = br.ReadUInt32();
                    fs.Position = opt + optSize;
                    var va = new uint[nSect];
                    var vsz = new uint[nSect];
                    var raw = new uint[nSect];
                    for (int i = 0; i < nSect; i++)
                    {
                        fs.Position += 8;
                        vsz[i] = br.ReadUInt32();
                        va[i] = br.ReadUInt32();
                        uint rawSz = br.ReadUInt32();
                        raw[i] = br.ReadUInt32();
                        if (rawSz > vsz[i]) vsz[i] = rawSz;
                        fs.Position += 16;
                    }
                    long R(uint rva)
                    {
                        for (int i = 0; i < nSect; i++)
                            if (rva >= va[i] && rva < va[i] + vsz[i]) return rva - va[i] + raw[i];
                        return -1;
                    }
                    if (expRva == 0) return true;
                    long eo = R(expRva);
                    if (eo < 0 || eo + 40 > fs.Length) return true;
                    fs.Position = eo + 24;
                    uint nNames = Math.Min(br.ReadUInt32(), 20000u);
                    fs.Position = eo + 32;
                    long names = R(br.ReadUInt32());
                    if (names < 0) return true;
                    var sb = new StringBuilder();
                    for (uint i = 0; i < nNames; i++)
                    {
                        fs.Position = names + i * 4;
                        long so = R(br.ReadUInt32());
                        if (so < 0 || so >= fs.Length) continue;
                        fs.Position = so;
                        sb.Clear();
                        for (int k = 0; k < 40; k++)
                        {
                            int c = fs.ReadByte();
                            if (c <= 0) break;
                            sb.Append((char)c);
                        }
                        string s = sb.ToString();
                        if (s == "VSTPluginMain" || s == "main" || s == "VstPluginMain") vst2Entry = true;
                        else if (s == "GetPluginFactory") vst3Entry = true;
                    }
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
