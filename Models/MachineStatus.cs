namespace WpfXrayQA.Models;

public sealed class MachineStatus
{
    public string DisplayName { get; set; } = "";
    public string Ip { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string LatestDateFolder { get; set; } = "";
    public int PendingCount { get; set; }
    public DateTimeOffset? LastImageTime { get; set; }
    public string PathStatus { get; set; } = "Unknown"; // OK / NotFound / AccessDenied / Error
    public DateTimeOffset LastScanAt { get; set; } = DateTimeOffset.UtcNow;
}
