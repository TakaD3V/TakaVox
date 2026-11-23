using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TakaVox
{
    /// <summary>Registro tecnico (arquivo) + historico de mudancas visivel no app.</summary>
    public static class Log
    {
        static readonly object sync = new object();
        /// <summary>Pasta de dados (%APPDATA%\TakaVox). TAKAVOX_DATA troca a pasta - so para os testes automaticos.</summary>
        public static string Dir => Environment.GetEnvironmentVariable("TAKAVOX_DATA") is string d && d.Length > 0
            ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TakaVox");
        static string FilePath => Path.Combine(Dir, "takavox.log");
        static string HistoryPath => Path.Combine(Dir, "historico.txt");

        public static event Action<string> HistoryAdded;

        public static void Write(string msg)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>Registra uma mudanca feita no sistema (aparece em "Historico" no app).</summary>
        public static void Change(string msg)
        {
            string line = $"{DateTime.Now:dd/MM/yyyy HH:mm}  {msg}";
            Write("[mudança] " + msg);
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(HistoryPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
            try { HistoryAdded?.Invoke(line); } catch { }
        }

        public static List<string> ReadHistory()
        {
            var l = new List<string>();
            try
            {
                lock (sync)
                {
                    if (File.Exists(HistoryPath)) l.AddRange(File.ReadAllLines(HistoryPath, Encoding.UTF8));
                }
            }
            catch { }
            return l;
        }
    }
}
