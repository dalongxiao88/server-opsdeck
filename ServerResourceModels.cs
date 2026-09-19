using System;
using System.Collections.Generic;

namespace ServerForge
{
    public sealed class ResourceCollectionProgress
    {
        public int Percentage { get; set; }
        public string Message { get; set; }

        public ResourceCollectionProgress(int percentage, string message)
        {
            Percentage = Math.Max(0, Math.Min(100, percentage));
            Message = message ?? "";
        }
    }

    public sealed class ServerResourceSnapshot
    {
        public string ServerName { get; set; }
        public string HostName { get; set; }
        public string OperatingSystem { get; set; }
        public string Transport { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public double UptimeSeconds { get; set; }
        public int LogicalProcessorCount { get; set; }
        public double CpuUsagePercent { get; set; }
        public long MemoryTotalBytes { get; set; }
        public long MemoryAvailableBytes { get; set; }
        public long MemoryUsedBytes { get; set; }
        public long NetworkTotalReceivedBytes { get; set; }
        public long NetworkTotalSentBytes { get; set; }
        public List<DiskResourceSnapshot> Disks { get; set; }
        public List<NetworkRateSample> NetworkSamples { get; set; }
        public List<ProcessResourceSnapshot> ExtraProcesses { get; set; }

        public double MemoryUsagePercent
        {
            get
            {
                return MemoryTotalBytes <= 0
                    ? 0
                    : Math.Max(0, Math.Min(100, MemoryUsedBytes * 100D / MemoryTotalBytes));
            }
        }

        public double CurrentDownloadBytesPerSecond
        {
            get
            {
                return NetworkSamples == null || NetworkSamples.Count == 0
                    ? 0
                    : NetworkSamples[NetworkSamples.Count - 1].ReceivedBytesPerSecond;
            }
        }

        public double CurrentUploadBytesPerSecond
        {
            get
            {
                return NetworkSamples == null || NetworkSamples.Count == 0
                    ? 0
                    : NetworkSamples[NetworkSamples.Count - 1].SentBytesPerSecond;
            }
        }

        public ServerResourceSnapshot()
        {
            Disks = new List<DiskResourceSnapshot>();
            NetworkSamples = new List<NetworkRateSample>();
            ExtraProcesses = new List<ProcessResourceSnapshot>();
        }
    }

    public sealed class DiskResourceSnapshot
    {
        public string Name { get; set; }
        public string Label { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        public long UsedBytes
        {
            get { return Math.Max(0, TotalBytes - FreeBytes); }
        }

        public double UsagePercent
        {
            get
            {
                return TotalBytes <= 0
                    ? 0
                    : Math.Max(0, Math.Min(100, UsedBytes * 100D / TotalBytes));
            }
        }
    }

    public sealed class NetworkRateSample
    {
        public double ReceivedBytesPerSecond { get; set; }
        public double SentBytesPerSecond { get; set; }
    }

    public sealed class ProcessResourceSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double CpuPercent { get; set; }
        public long WorkingSetBytes { get; set; }
        public string Path { get; set; }
    }
}
