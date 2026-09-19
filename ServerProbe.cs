using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ServerForge
{
    public sealed class ServerProbeResult
    {
        public bool IsOnline { get; set; }
        public bool IsServiceAvailable { get; set; }
        public long? LatencyMilliseconds { get; set; }
        public string DisplayText { get; set; }
        public string DetailText { get; set; }
        public DateTime CheckedAt { get; set; }

        public string RatingText
        {
            get
            {
                if (CheckedAt == DateTime.MinValue)
                    return "待检测";
                if (!IsServiceAvailable || !LatencyMilliseconds.HasValue)
                    return "失败";
                if (LatencyMilliseconds.Value < 50)
                    return "良好";
                if (LatencyMilliseconds.Value < 100)
                    return "一般";
                if (LatencyMilliseconds.Value < 200)
                    return "较弱";
                return "卡顿";
            }
        }

        public string LatencyDisplayText
        {
            get { return LatencyMilliseconds.HasValue ? LatencyMilliseconds.Value + " ms" : "--"; }
        }

        public string CompactDisplayText
        {
            get { return LatencyDisplayText + " · " + RatingText; }
        }

        public static ServerProbeResult Pending()
        {
            return new ServerProbeResult
            {
                DisplayText = "-- · 待检测",
                DetailText = "尚未检测",
                CheckedAt = DateTime.MinValue
            };
        }
    }

    public static class ServerProbe
    {
        private const int TimeoutMilliseconds = 2500;

        public static async Task<ServerProbeResult> CheckAsync(Server server)
        {
            int port;
            if (!int.TryParse(server.Port, out port) || port < 1 || port > 65535)
            {
                return new ServerProbeResult
                {
                    DisplayText = "-- · 失败",
                    DetailText = "端口必须在 1-65535 之间",
                    CheckedAt = DateTime.Now
                };
            }

            Task<long?> pingTask = MeasurePingAsync(server.IP);
            Task<long?> tcpTask = MeasureTcpAsync(server.IP, port);
            await Task.WhenAll(pingTask, tcpTask);

            long? pingLatency = pingTask.Result;
            long? tcpLatency = tcpTask.Result;
            if (tcpLatency.HasValue)
            {
                return new ServerProbeResult
                {
                    IsOnline = true,
                    IsServiceAvailable = true,
                    LatencyMilliseconds = tcpLatency,
                    DisplayText = string.Format("{0} ms · {1}", tcpLatency.Value, GetLatencyRating(tcpLatency)),
                    DetailText = pingLatency.HasValue
                        ? string.Format("服务端口正常，Ping {0} ms", pingLatency.Value)
                        : "服务端口正常，服务器未响应 Ping",
                    CheckedAt = DateTime.Now
                };
            }

            if (pingLatency.HasValue)
            {
                return new ServerProbeResult
                {
                    IsOnline = true,
                    IsServiceAvailable = false,
                    LatencyMilliseconds = pingLatency,
                    DisplayText = string.Format("{0} · 失败", pingLatency.HasValue ? pingLatency.Value + " ms" : "--"),
                    DetailText = string.Format("主机可达（Ping {0} ms），但 {1} 端口未响应", pingLatency.Value, port),
                    CheckedAt = DateTime.Now
                };
            }

            return new ServerProbeResult
            {
                IsOnline = false,
                IsServiceAvailable = false,
                DisplayText = "-- · 失败",
                DetailText = "Ping 和服务端口均无响应",
                CheckedAt = DateTime.Now
            };
        }

        private static async Task<long?> MeasurePingAsync(string host)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(host, TimeoutMilliseconds);
                    return reply.Status == IPStatus.Success ? (long?)reply.RoundtripTime : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetLatencyRating(long? latency)
        {
            if (!latency.HasValue)
                return "失败";
            if (latency.Value < 50)
                return "良好";
            if (latency.Value < 100)
                return "一般";
            if (latency.Value < 200)
                return "较弱";
            return "卡顿";
        }

        private static async Task<long?> MeasureTcpAsync(string host, int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeoutMilliseconds))
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    await client.ConnectAsync(host, port, timeout.Token);
                    stopwatch.Stop();
                    return stopwatch.ElapsedMilliseconds;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
