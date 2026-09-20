using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.IronRdp;

namespace ServerForge
{
    /// <summary>
    /// Direct IronRDP connection with a certificate callback. The package's
    /// public Connection.Connect helper currently accepts every certificate.
    /// </summary>
    internal static class RdpProtocolConnection
    {
        private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(30);

        public static async Task<ValueTuple<ConnectionResult, Framed<SslStream>>> ConnectAsync(
            Config config,
            string serverName,
            CliprdrBackendFactory factory,
            int port,
            RemoteCertificateValidationCallback certificateValidation,
            CancellationToken cancellationToken)
        {
            TcpClient client = await CreateTcpConnectionAsync(serverName, port, cancellationToken);
            using CancellationTokenRegistration cancelClient = cancellationToken.Register(client.Dispose);
            NetworkStream networkStream = client.GetStream();
            Framed<NetworkStream> framed = new Framed<NetworkStream>(networkStream);
            string clientAddress = client.Client.LocalEndPoint == null
                ? string.Empty
                : client.Client.LocalEndPoint.ToString();
            ClientConnector connector = ClientConnector.New(config, clientAddress);
            bool connected = false;
            try
            {
                SetupConnector(connector, config, factory);
                await ConnectBeginAsync(framed, connector, cancellationToken);
                ValueTuple<byte[], Framed<SslStream>> secure = await UpgradeToTlsAsync(
                    framed, connector, serverName, certificateValidation, cancellationToken);
                ConnectionResult result = await FinalizeConnectionAsync(
                    serverName, connector, secure.Item1, secure.Item2, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                connected = true;
                return new ValueTuple<ConnectionResult, Framed<SslStream>>(result, secure.Item2);
            }
            finally
            {
                if (!connected)
                {
                    try { connector.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                }
            }
        }

        private static async Task<ValueTuple<byte[], Framed<SslStream>>> UpgradeToTlsAsync(
            Framed<NetworkStream> framed,
            ClientConnector connector,
            string serverName,
            RemoteCertificateValidationCallback certificateValidation,
            CancellationToken cancellationToken)
        {
            ValueTuple<NetworkStream, List<byte>> inner = framed.GetInner();
            byte[] serverPublicKey = null;
            using CancellationTokenSource tlsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tlsTimeout.CancelAfter(TlsHandshakeTimeout);
            SslStream sslStream = new SslStream(inner.Item1, false, (sender, certificate, chain, errors) =>
            {
                // Certificate confirmation is interactive. Do not count the time the
                // user spends reading the prompt as TLS network timeout.
                tlsTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                try
                {
                    if (certificate == null)
                        return false;

                    serverPublicKey = certificate.GetPublicKey();
                    return certificateValidation == null || certificateValidation(sender, certificate, chain, errors);
                }
                finally
                {
                    if (!tlsTimeout.IsCancellationRequested)
                        tlsTimeout.CancelAfter(TlsHandshakeTimeout);
                }
            });
            try
            {
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = serverName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowTlsResume = false
                }, tlsTimeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sslStream.Dispose();
                throw;
            }
            catch (OperationCanceledException) when (tlsTimeout.IsCancellationRequested)
            {
                sslStream.Dispose();
                throw new TimeoutException("RDP TLS 握手超时");
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }

            if (serverPublicKey == null)
            {
                sslStream.Dispose();
                throw new AuthenticationException("RDP 服务器没有提供有效证书");
            }

            Framed<SslStream> framedSsl = new Framed<SslStream>(sslStream);
            connector.MarkSecurityUpgradeAsDone();
            return new ValueTuple<byte[], Framed<SslStream>>(serverPublicKey, framedSsl);
        }

        private static async Task ConnectBeginAsync(Framed<NetworkStream> framed, ClientConnector connector,
            CancellationToken cancellationToken)
        {
            WriteBuf writeBuffer = WriteBuf.New();
            try
            {
                while (!connector.ShouldPerformSecurityUpgrade())
                    await SingleSequenceStepAsync(connector, writeBuffer, framed, cancellationToken);
            }
            finally
            {
                writeBuffer.Dispose();
            }
        }

        private static async Task<ClientState> ResolveGeneratorAsync(
            CredsspProcessGenerator generator, TcpClient tcpClient, CancellationToken cancellationToken)
        {
            GeneratorState state = generator.Start();
            NetworkStream stream = null;
            while (true)
            {
                if (state.IsSuspended())
                {
                    NetworkRequest request = state.GetNetworkRequestIfSuspended();
                    if (request.GetProtocol() != NetworkRequestProtocol.Tcp)
                        throw new InvalidOperationException("IronRDP 请求了不支持的 CredSSP 传输协议");

                    if (stream == null)
                    {
                        string url = request.GetUrl().Replace("tcp://", string.Empty);
                        string[] parts = url.Split(':');
                        if (parts.Length != 2 || !int.TryParse(parts[1], out int requestedPort))
                            throw new InvalidOperationException("CredSSP 返回了无效的 TCP 地址");
                        await tcpClient.ConnectAsync(parts[0], requestedPort)
                            .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                        stream = tcpClient.GetStream();
                    }

                    byte[] requestData = ToBytes(request.GetData());
                    await stream.WriteAsync(requestData, cancellationToken);
                    byte[] readBuffer = new byte[8096];
                    int readLength = await stream.ReadAsync(readBuffer, 0, readBuffer.Length)
                        .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                    if (readLength <= 0)
                        throw new IOException("CredSSP 认证通道已关闭");
                    byte[] response = new byte[readLength];
                    Buffer.BlockCopy(readBuffer, 0, response, 0, readLength);
                    state = generator.Resume(response);
                }
                else if (state.IsCompleted())
                {
                    return state.GetClientStateIfCompleted();
                }
                else
                {
                    throw new InvalidOperationException("CredSSP 状态机未处于可继续状态");
                }
            }
        }

        private static async Task PerformCredsspAsync<T>(
            ClientConnector connector,
            string serverName,
            WriteBuf writeBuffer,
            Framed<T> framed,
            byte[] serverPublicKey,
            CancellationToken cancellationToken) where T : Stream
        {
            string hostname = serverName;
            int colon = serverName.IndexOf(':');
            if (colon > 0)
                hostname = serverName.Substring(0, colon);

            CredsspSequenceInitResult init = CredsspSequence.Init(connector, hostname, serverPublicKey, null);
            CredsspSequence sequence = init.GetCredsspSequence();
            TsRequest request = init.GetTsRequest();
            TcpClient tcpClient = new TcpClient();
            try
            {
                while (true)
                {
                    CredsspProcessGenerator generator = sequence.ProcessTsRequest(request);
                    ClientState state = await ResolveGeneratorAsync(generator, tcpClient, cancellationToken);
                    writeBuffer.Clear();
                    Written written = sequence.HandleProcessResult(state, writeBuffer);
                    if (written.GetSize().IsSome())
                    {
                        int size = (int)written.GetSize().Get();
                        byte[] response = new byte[size];
                        writeBuffer.ReadIntoBuf(response);
                        await framed.GetInner().Item1.WriteAsync(response, cancellationToken);
                    }

                    PduHint hint = sequence.NextPduHint();
                    if (hint == null)
                        break;
                    byte[] pdu = await framed.ReadByHint(hint).WaitAsync(cancellationToken);
                    TsRequest decoded = sequence.DecodeServerMessage(pdu);
                    if (decoded == null)
                        break;
                    request = decoded;
                }
            }
            finally
            {
                tcpClient.Dispose();
                sequence.Dispose();
            }
        }

        private static async Task<ConnectionResult> FinalizeConnectionAsync<T>(
            string serverName,
            ClientConnector connector,
            byte[] serverPublicKey,
            Framed<T> framed,
            CancellationToken cancellationToken) where T : Stream
        {
            WriteBuf writeBuffer = WriteBuf.New();
            try
            {
                if (connector.ShouldPerformCredssp())
                    await PerformCredsspAsync(connector, serverName, writeBuffer, framed, serverPublicKey,
                        cancellationToken);

                while (!connector.GetDynState().IsTerminal())
                    await SingleSequenceStepAsync(connector, writeBuffer, framed, cancellationToken);

                ClientConnectorState state = connector.ConsumeAndCastToClientConnectorState();
                if (state.GetEnumType() != ClientConnectorStateType.Connected)
                    throw new InvalidOperationException("RDP 连接状态未进入 Connected");
                return state.GetConnectedResult();
            }
            finally
            {
                writeBuffer.Dispose();
                connector.Dispose();
            }
        }

        internal static async Task SingleSequenceStepAsync<S, T>(S sequence, WriteBuf writeBuffer,
            Framed<T> framed, CancellationToken cancellationToken)
            where S : ISequence
            where T : Stream
        {
            writeBuffer.Clear();
            PduHint hint = sequence.NextPduHint();
            Written written = hint == null
                ? sequence.StepNoInput(writeBuffer)
                : sequence.Step(await framed.ReadByHint(hint).WaitAsync(cancellationToken), writeBuffer);
            if (written.GetWrittenType() == WrittenType.Nothing)
                return;

            int size = (int)written.GetSize().Get();
            byte[] response = new byte[size];
            writeBuffer.ReadIntoBuf(response);
            await framed.GetInner().Item1.WriteAsync(response, cancellationToken);
        }

        private static void SetupConnector(ClientConnector connector, Config config, CliprdrBackendFactory factory)
        {
            connector.WithDynamicChannelDisplayControl();
            if (config.GetDvcPipeProxy() != null)
                connector.WithDynamicChannelPipeProxy(config.GetDvcPipeProxy());
            if (factory != null)
                connector.AttachStaticCliprdr(factory.BuildCliprdr());
        }

        private static async Task<TcpClient> CreateTcpConnectionAsync(string serverName, int port,
            CancellationToken cancellationToken)
        {
            IPAddress address;
            try
            {
                address = IPAddress.Parse(serverName);
            }
            catch (FormatException)
            {
                IPHostEntry entry = await Dns.GetHostEntryAsync(serverName)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (entry.AddressList == null || entry.AddressList.Length == 0)
                    throw new InvalidOperationException("无法解析 RDP 服务器地址");
                address = entry.AddressList[0];
            }

            TcpClient client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(new IPEndPoint(address, port))
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            return client;
        }

        private static byte[] ToBytes(VecU8 value)
        {
            int size = (int)value.GetSize();
            byte[] bytes = new byte[size];
            value.Fill(bytes);
            return bytes;
        }
    }
}
