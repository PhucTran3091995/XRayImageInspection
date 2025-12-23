using System;
using System.ComponentModel.DataAnnotations;

namespace WpfXrayQA.Models
{
    public class ReviewLog
    {
        [Key]
        public int Id { get; set; }

        public string FullPath { get; set; } = string.Empty;
        public DateTime LastWriteTimeUtc { get; set; }
        public long FileSize { get; set; }

        public string? MachineRoot { get; set; }
        public string? DateFolder { get; set; }
        public string? ModelName { get; set; }
        public string? Filename { get; set; }

        public DateTime ReviewedAt { get; set; }
        public string? Reviewer { get; set; }
        public string? Decision { get; set; }   // OK, NG, Rerun
        public string? DefectType { get; set; } // Short, Missing, Both
        public string? Comment { get; set; }
    }
}