using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace RDPManager
{
    public sealed class SshDatabaseTunnel : IDisposable
    {
        private readonly SshClient client;
        private readonly ForwardedPortLocal forwardedPort;
        private bool disposed;

        public int LocalPort { get; private set; }

        private SshDatabaseTunnel(SshClient client, ForwardedPortLocal forwardedPort, int localPort)
        {
            this.client = client;
            this.forwardedPort = forwardedPort;
            LocalPort = localPort;
        }

        public static async Task<SshDatabaseTunnel> OpenAsync(
            Server server,
            string serverPassword,
            string remoteHost,
            int remotePort,
            CancellationToken cancellationToken)
        {
            if (server == null)
                throw new ArgumentNullException(nameof(server));
            if (string.IsNullOrWhiteSpace(server.IP))
                throw new InvalidOperationException("服务器地址为空");
            if (string.IsNullOrWhiteSpace(server.Username))
                throw new InvalidOperationException("服务器管理账号为空");
            if (remotePort < 1 || remotePort > 65535)
                throw new InvalidOperationException("数据库端口无效");

            int sshPort = RemoteExecutorFactory.GetManagementPort(server, RemoteTransport.SSH);
            PasswordConnectionInfo connection = new PasswordConnectionInfo(server.IP, sshPort, server.Username, serverPassword ?? "")
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            SshClient client = new SshClient(connection);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    client.Connect();
                }, cancellationToken);
                if (!client.IsConnected)
                    throw new InvalidOperationException("SSH 隧道连接未建立");

                int localPort = FindFreePort();
                ForwardedPortLocal forwardedPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
                client.AddForwardedPort(forwardedPort);
                forwardedPort.Start();
                if (!forwardedPort.IsStarted)
                    throw new InvalidOperationException("SSH 本地端口转发未启动");

                return new SshDatabaseTunnel(client, forwardedPort, localPort);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                if (forwardedPort.IsStarted)
                    forwardedPort.Stop();
            }
            catch { }
            try { client.RemoveForwardedPort(forwardedPort); } catch { }
            client.Dispose();
        }

        private static int FindFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
