using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TakaVox.Dsp;

namespace TakaVox
{
    /// <summary>Configuracoes do app (salvas em %APPDATA%\TakaVox\config.ini, texto legivel).</summary>
    public sealed class AppSettings
    {
        public string InputDeviceId = "", InputDeviceName = "";
        public string OutputDeviceId = "", OutputDeviceName = "";
        public string MonitorDeviceId = "", MonitorDeviceName = "";
        public bool MonitorOn = false;
        public float MonitorVolume = 0.7f;
        public string MonitorConfirmed = "";   // IDs de saidas confirmadas como fone (sem risco de microfonia)
        public bool CloseToTray = true;
        public bool RestoreMicOnExit = true;
        public bool KeepDefault = true;
        public bool TrayTipShown = false;
        public string PresetName = "Padrão TakaVox";
        public bool Calibrated = false;
        public string CalibrationSummary = "";
        public bool AutoEq = true;            // a calibracao tambem ajusta o equalizador ao timbre do mic
        public string VoicePreset = "Garota de anime"; // ultima voz pronta escolhida no modificador (o atalho liga nela)
        public string ThemeKey = "escuro";             // tema do app (pagina Temas)
        public float AiSrcPitch = 0f;                  // tom medio da sua voz (nota MIDI) medido pela voz com IA; 0 = ainda nao medido

        // Sound pad
        public bool SoundToMic = true;                 // os sons saem para quem te escuta (microfone virtual)
        public bool SoundToPhone = true;               // e no seu fone
        public float SoundMicDb = 0f, SoundPhoneDb = -6f;
        public float SoundDuckDb = 0f;                 // abaixa a sua voz enquanto um som toca (0 = nao abaixa)
        public bool SoundWhileMuted = true;            // os sons saem mesmo com o mic no mudo
        public bool SoundExclusive = false;            // um som por vez
        public bool AutoRest = true;                   // descansa (sem IA) quando nenhum app esta usando o microfone virtual

        // Estado original do Windows (para "Restaurar tudo")
        public bool OriginalsSaved = false;
        public string OrigCaptureConsole = "", OrigCaptureMultimedia = "", OrigCaptureComms = "";
        public string OrigDucking = "";      // "" = valor ausente (padrao do Windows)
        public float OrigMicVolume = -1f;
        public string OrigMicId = "";

        public double WinLeft = double.NaN, WinTop = double.NaN, WinWidth = 1180, WinHeight = 780;

        public static string Dir => Log.Dir;
        public static string ConfigPath => Path.Combine(Dir, "config.ini");
        public static string PresetDir => Path.Combine(Dir, "presets");

        // ------------------------------------------------------------ INI simples
        static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return res;
            string sec = "";
            res[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    sec = line.Substring(1, line.Length - 2).Trim();
                    if (!res.ContainsKey(sec)) res[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                res[sec][line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return res;
        }

        static void WriteIni(string path, string header, IEnumerable<KeyValuePair<string, Dictionary<string, string>>> sections)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.AppendLine("; " + header);
            foreach (var s in sections)
            {
                sb.AppendLine();
                sb.AppendLine("[" + s.Key + "]");
                foreach (var kv in s.Value) sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static string S(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string S(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string S(bool v) => v ? "1" : "0";

        Dictionary<string, string> ToDict()
        {
            return new Dictionary<string, string>
            {
                ["InputDeviceId"] = InputDeviceId, ["InputDeviceName"] = InputDeviceName,
                ["OutputDeviceId"] = OutputDeviceId, ["OutputDeviceName"] = OutputDeviceName,
                ["MonitorDeviceId"] = MonitorDeviceId, ["MonitorDeviceName"] = MonitorDeviceName,
                ["MonitorOn"] = S(MonitorOn), ["MonitorVolume"] = S(MonitorVolume), ["MonitorConfirmed"] = MonitorConfirmed,
                ["CloseToTray"] = S(CloseToTray), ["RestoreMicOnExit"] = S(RestoreMicOnExit), ["KeepDefault"] = S(KeepDefault),
                ["TrayTipShown"] = S(TrayTipShown), ["PresetName"] = PresetName,
                ["Calibrated"] = S(Calibrated), ["CalibrationSummary"] = CalibrationSummary.Replace("\r", " ").Replace("\n", " | "),
                ["AutoEq"] = S(AutoEq), ["VoicePreset"] = VoicePreset, ["Theme"] = ThemeKey, ["AiSrcPitch"] = S(AiSrcPitch),
                ["SoundToMic"] = S(SoundToMic), ["SoundToPhone"] = S(SoundToPhone), ["SoundMicDb"] = S(SoundMicDb), ["SoundPhoneDb"] = S(SoundPhoneDb),
                ["SoundDuckDb"] = S(SoundDuckDb), ["SoundWhileMuted"] = S(SoundWhileMuted), ["SoundExclusive"] = S(SoundExclusive),
                ["AutoRest"] = S(AutoRest),
                ["OriginalsSaved"] = S(OriginalsSaved),
                ["OrigCaptureConsole"] = OrigCaptureConsole, ["OrigCaptureMultimedia"] = OrigCaptureMultimedia, ["OrigCaptureComms"] = OrigCaptureComms,
                ["OrigDucking"] = OrigDucking, ["OrigMicVolume"] = S(OrigMicVolume), ["OrigMicId"] = OrigMicId,
                ["WinLeft"] = S(WinLeft), ["WinTop"] = S(WinTop), ["WinWidth"] = S(WinWidth), ["WinHeight"] = S(WinHeight),
            };
        }

        void FromDict(Dictionary<string, string> d)
        {
            string G(string k, string def) => d.TryGetValue(k, out var v) ? v : def;
            bool B(string k, bool def) => d.TryGetValue(k, out var v) ? v == "1" : def;
            float F(string k, float def) => d.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
            double D(string k, double def) => d.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
            InputDeviceId = G("InputDeviceId", ""); InputDeviceName = G("InputDeviceName", "");
            OutputDeviceId = G("OutputDeviceId", ""); OutputDeviceName = G("OutputDeviceName", "");
            MonitorDeviceId = G("MonitorDeviceId", ""); MonitorDeviceName = G("MonitorDeviceName", "");
            MonitorOn = B("MonitorOn", false); MonitorVolume = Math.Max(0f, Math.Min(1f, F("MonitorVolume", 0.7f)));
            MonitorConfirmed = G("MonitorConfirmed", "");
            CloseToTray = B("CloseToTray", true); RestoreMicOnExit = B("RestoreMicOnExit", true); KeepDefault = B("KeepDefault", true);
            TrayTipShown = B("TrayTipShown", false); PresetName = G("PresetName", PresetName);
            Calibrated = B("Calibrated", false); CalibrationSummary = G("CalibrationSummary", "").Replace(" | ", "\n");
            AutoEq = B("AutoEq", true);
            VoicePreset = G("VoicePreset", VoicePreset);
            ThemeKey = G("Theme", ThemeKey);
            AiSrcPitch = F("AiSrcPitch", 0f);
            SoundToMic = B("SoundToMic", true); SoundToPhone = B("SoundToPhone", true);
            SoundMicDb = Math.Max(-40f, Math.Min(12f, F("SoundMicDb", 0f)));
            SoundPhoneDb = Math.Max(-40f, Math.Min(12f, F("SoundPhoneDb", -6f)));
            SoundDuckDb = Math.Max(-40f, Math.Min(0f, F("SoundDuckDb", 0f)));
            SoundWhileMuted = B("SoundWhileMuted", true); SoundExclusive = B("SoundExclusive", false);
            AutoRest = B("AutoRest", true);
            if (AiSrcPitch != 0f && (AiSrcPitch < 30f || AiSrcPitch > 90f)) AiSrcPitch = 0f;
            OriginalsSaved = B("OriginalsSaved", false);
            OrigCaptureConsole = G("OrigCaptureConsole", ""); OrigCaptureMultimedia = G("OrigCaptureMultimedia", ""); OrigCaptureComms = G("OrigCaptureComms", "");
            OrigDucking = G("OrigDucking", ""); OrigMicVolume = F("OrigMicVolume", -1f); OrigMicId = G("OrigMicId", "");
            WinLeft = D("WinLeft", double.NaN); WinTop = D("WinTop", double.NaN);
            WinWidth = Math.Max(900, D("WinWidth", 1180)); WinHeight = Math.Max(620, D("WinHeight", 780));
        }

        public static void Load(AppSettings app, ChainParams chain)
        {
            try
            {
                var ini = ReadIni(ConfigPath);
                if (ini.TryGetValue("app", out var a)) app.FromDict(a);
                if (ini.TryGetValue("voz", out var v))
                {
                    chain.FromDictionary(v);
                    string up = VoicePresets.MigrateOld(chain);
                    if (up != null) Log.Write("Modificador de voz: '" + up + "' atualizada para a versão nova (mais limpa).");
                }
            }
            catch (Exception ex) { Log.Write("Falha ao ler config: " + ex.Message); }
        }

        public static void Save(AppSettings app, ChainParams chain)
        {
            try
            {
                WriteIni(ConfigPath, "TakaVox - configuracoes (pode editar com o app fechado)", new[]
                {
                    new KeyValuePair<string, Dictionary<string, string>>("app", app.ToDict()),
                    new KeyValuePair<string, Dictionary<string, string>>("voz", chain.ToDictionary()),
                });
            }
            catch (Exception ex) { Log.Write("Falha ao salvar config: " + ex.Message); }
        }

        // ------------------------------------------------------------ presets do usuario
        public static List<string> UserPresets()
        {
            try
            {
                if (!Directory.Exists(PresetDir)) return new List<string>();
                return Directory.GetFiles(PresetDir, "*.ini").Select(Path.GetFileNameWithoutExtension).OrderBy(s => s).ToList();
            }
            catch { return new List<string>(); }
        }

        public static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        public static void SaveUserPreset(string name, ChainParams p)
        {
            WriteIni(Path.Combine(PresetDir, SafeName(name) + ".ini"), "Preset TakaVox: " + name,
                new[] { new KeyValuePair<string, Dictionary<string, string>>("voz", p.ToDictionary()) });
        }

        public static bool LoadUserPreset(string name, ChainParams p)
        {
            var ini = ReadIni(Path.Combine(PresetDir, SafeName(name) + ".ini"));
            if (!ini.TryGetValue("voz", out var v)) return false;
            p.FromDictionary(v);
            return true;
        }

        public static void DeleteUserPreset(string name)
        {
            try { File.Delete(Path.Combine(PresetDir, SafeName(name) + ".ini")); } catch { }
        }

        // ------------------------------------------------------------ vozes do modificador salvas pelo usuario
        public static string VoiceDir => Path.Combine(Dir, "vozes");

        public static List<string> UserVoices()
        {
            try
            {
                if (!Directory.Exists(VoiceDir)) return new List<string>();
                return Directory.GetFiles(VoiceDir, "*.ini").Select(Path.GetFileNameWithoutExtension).OrderBy(s => s).ToList();
            }
            catch { return new List<string>(); }
        }

        public static void SaveUserVoice(string name, ChainParams p)
        {
            WriteIni(Path.Combine(VoiceDir, SafeName(name) + ".ini"), "Voz do modificador TakaVox: " + name,
                new[] { new KeyValuePair<string, Dictionary<string, string>>("voz", p.VoiceToDictionary()) });
        }

        /// <summary>Carrega uma voz salva (so os ajustes do modificador; o resto da cadeia fica igual).</summary>
        public static bool LoadUserVoice(string name, ChainParams p)
        {
            try
            {
                var ini = ReadIni(Path.Combine(VoiceDir, SafeName(name) + ".ini"));
                if (!ini.TryGetValue("voz", out var v)) return false;
                bool on = p.VcOn;
                VoicePresets.ResetVoice(p);
                p.FromDictionary(v);
                p.VcOn = on;
                return true;
            }
            catch { return false; }
        }

        public static void DeleteUserVoice(string name)
        {
            try { File.Delete(Path.Combine(VoiceDir, SafeName(name) + ".ini")); } catch { }
        }
    }

    /// <summary>Presets prontos. Nao mexem no ganho de entrada calibrado.</summary>
    public static class BuiltInPresets
    {
        public static readonly string[] Names =
        {
            "Padrão TakaVox", "Rádio / Locutor", "Gamer – silêncio total", "Natural / Suave", "Voz encorpada (grave)", "Podcast limpo",
        };

        public static void Apply(string name, ChainParams p)
        {
            var d = new ChainParams(); // valores padrao
            d.CopyVoiceFrom(p);        // o modificador de voz tem as vozes dele: trocar o preset de estudio nao mexe nele
            // preserva a calibracao
            d.InputGainDb = p.InputGainDb;
            d.GateThresholdDb = p.GateThresholdDb;
            d.ChannelMode = p.ChannelMode;
            switch (name)
            {
                case "Rádio / Locutor":
                    d.HpfFreq = 75;
                    SetEq(d, 4.5f, -3.5f, -1.5f, 4.0f, 3.5f);
                    d.Bands[0].Freq = 115;
                    d.CompThresholdDb = -30; d.CompRatio = 4.5f; d.CompAttackMs = 6; d.CompReleaseMs = 140; d.CompMakeupDb = 6.5f;
                    d.AgcTargetDb = -19; d.OutputGainDb = 2;
                    d.DeessMaxDb = 7;
                    break;
                case "Gamer – silêncio total":
                    d.HpfFreq = 100;
                    d.GateRangeDb = -70; d.GateVadThreshold = 0.6f; d.GateHoldMs = 180;
                    SetEq(d, 1.0f, -3.0f, -1.0f, 4.5f, 2.5f);
                    d.CompThresholdDb = -26; d.CompRatio = 3.5f; d.CompMakeupDb = 4.5f;
                    d.OutputGainDb = 3;
                    break;
                case "Natural / Suave":
                    d.HpfFreq = 70;
                    d.GateRangeDb = -18; d.GateHoldMs = 300;
                    SetEq(d, 1.0f, -1.5f, 0f, 2.0f, 1.5f);
                    d.CompThresholdDb = -22; d.CompRatio = 2f; d.CompMakeupDb = 2.5f;
                    d.AgcMaxBoostDb = 8; d.DeessMaxDb = 4;
                    break;
                case "Voz encorpada (grave)":
                    d.HpfFreq = 65;
                    SetEq(d, 5.5f, -2.0f, -1.5f, 2.5f, 2.0f);
                    d.Bands[0].Freq = 110;
                    d.CompThresholdDb = -28; d.CompRatio = 3.5f; d.CompMakeupDb = 5f;
                    break;
                case "Podcast limpo":
                    d.HpfFreq = 80;
                    d.GateRangeDb = -40;
                    SetEq(d, 2.5f, -3.0f, -1.5f, 3.0f, 2.5f);
                    d.CompThresholdDb = -28; d.CompRatio = 3f; d.CompAttackMs = 10; d.CompReleaseMs = 200; d.CompMakeupDb = 5f;
                    d.AgcTargetDb = -18;
                    break;
                default:
                    break;
            }
            d.Bypass = p.Bypass;
            p.CopyFrom(d);
            p.Bypass = d.Bypass;
        }

        static void SetEq(ChainParams d, float low, float mud, float nasal, float presence, float air)
        {
            d.Bands[0].GainDb = low; d.Bands[1].GainDb = mud; d.Bands[2].GainDb = nasal; d.Bands[3].GainDb = presence; d.Bands[4].GainDb = air;
        }
    }
}
