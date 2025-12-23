using System.Collections.Generic;

namespace WpfXrayQA.Services
{
    public sealed class AppOptions
    {
        public string Reviewer { get; set; } = "QA";
        public ScanOptions Scan { get; set; } = new();
    }

    public sealed class ScanOptions
    {
        public string FolderGdName { get; set; } = "GD";
        public string Suffix { get; set; } = "_1_8";
        public string Extension { get; set; } = ".jpg";
        public int PollIntervalMs { get; set; } = 3000;
        public int StableMs { get; set; } = 400;
        public int MaxPendingPerMachine { get; set; } = 1000;
    }
}