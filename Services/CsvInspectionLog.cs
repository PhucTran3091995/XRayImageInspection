using System;
using System.Globalization;
using System.IO;
using System.Text;
using WpfXrayQA.Models;

namespace WpfXrayQA.Services
{
    public sealed class CsvInspectionLog
    {
        private readonly object _lock = new();
        private readonly string _dir;
        private readonly string _file;

        public CsvInspectionLog()
        {
            // Lưu log vào folder Logs cạnh file exe
            _dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);

            _file = Path.Combine(_dir, $"inspect_{DateTime.Now:yyyyMMdd}.csv");
            EnsureHeader();
        }

        private void EnsureHeader()
        {
            lock (_lock)
            {
                if (File.Exists(_file) && new FileInfo(_file).Length > 0) return;

                using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, new UTF8Encoding(true));
                sw.WriteLine("Timestamp,Machine,Model,Filename,FullPath,AutoDecision,AutoDefect,MissingCount,ShortCount");
            }
        }

        public void Append(PendingFileItem item)
        {
            lock (_lock)
            {
                using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));

                string line = string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Esc("LocalMachine"), // Hoặc lấy từ setting
                    Esc(item.ModelName),
                    Esc(item.FileName),
                    Esc(item.FullPath),
                    Esc(item.AutoDecision),
                    Esc(item.AutoDefectType),
                    item.AutoMissingCount,
                    item.AutoShortCount
                );
                sw.WriteLine(line);
            }
        }

        private static string Esc(string? s)
        {
            s ??= "";
            if (s.Contains(",") || s.Contains("\""))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}