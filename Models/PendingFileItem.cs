using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfXrayQA.Models
{
    public class PendingFileItem : INotifyPropertyChanged
    {
        // --- Thông tin File cơ bản ---
        public string FullPath { get; set; }
        public string FileName => Path.GetFileName(FullPath);
        public string ModelName { get; set; } = "Unknown";
        public string TimeStr { get; set; } = "";
        public DateTime LastWriteTimeUtc { get; set; }
        public long FileSize { get; set; }

        // --- Kết quả Auto Inspection (Step 2) ---
        private bool _autoInspected = false;
        public bool AutoInspected
        {
            get => _autoInspected;
            set => SetProperty(ref _autoInspected, value);
        }

        private string _autoDecision = "WAIT"; // OK/NG/NO_RECIPE
        public string AutoDecision
        {
            get => _autoDecision;
            set
            {
                if (SetProperty(ref _autoDecision, value))
                {
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        private string _autoDefectType = ""; // Missing/Short
        public string AutoDefectType
        {
            get => _autoDefectType;
            set => SetProperty(ref _autoDefectType, value);
        }

        private int _autoMissingCount = 0;
        public int AutoMissingCount
        {
            get => _autoMissingCount;
            set => SetProperty(ref _autoMissingCount, value);
        }

        private int _autoShortCount = 0;
        public int AutoShortCount
        {
            get => _autoShortCount;
            set => SetProperty(ref _autoShortCount, value);
        }

        // Chuỗi hiển thị tóm tắt trên UI
        private string _autoSummary = "";
        public string AutoSummary
        {
            get => _autoSummary;
            set => SetProperty(ref _autoSummary, value);
        }

        // Màu sắc hiển thị (Optional: Xanh=OK, Đỏ=NG)
        public string StatusColor => AutoDecision == "OK" ? "Green" : (AutoDecision == "NG" ? "Red" : "Gray");

        public PendingFileItem(string path)
        {
            FullPath = path;
            ParseInfo();
        }

        private void ParseInfo()
        {
            try
            {
                var info = new FileInfo(FullPath);
                LastWriteTimeUtc = info.LastWriteTimeUtc;
                FileSize = info.Length;

                // Parse Model: ...\ModelName\GD\file.jpg
                var parts = FullPath.Split(Path.DirectorySeparatorChar);
                if (parts.Length >= 3 && parts[^2].Equals("GD", StringComparison.OrdinalIgnoreCase))
                {
                    ModelName = parts[^3];
                }

                // Parse Time: yyyyMMddHHmmss...
                if (FileName.Length >= 14 && long.TryParse(FileName.Substring(0, 14), out _))
                {
                    TimeStr = FileName.Substring(8, 6); // HHmmss
                }
            }
            catch { }
        }

        // --- INotifyPropertyChanged implementation ---
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}