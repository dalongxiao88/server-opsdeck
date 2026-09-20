using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Renci.SshNet;

namespace ServerForge
{
    public sealed class SshTerminalForm : Form
    {
        private static readonly Color Surface = Color.White;
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Blue = Color.FromArgb(42, 125, 185);
        private static readonly Color Red = Color.FromArgb(184, 62, 62);

        private readonly Server server;
        private readonly string serverPassword;
        private readonly string displayName;
        private readonly string webViewDataFolder;
        private readonly object streamSync = new object();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Label statusLabel;
        private readonly Label statusDot;
        private readonly Label latencyLabel;
        private readonly System.Windows.Forms.Timer latencyTimer;
        private readonly ToolTip latencyToolTip;
        private readonly WebView2 webView;
        private SshRemoteClient client;
        private ShellStream shell;
        private bool pageReady;
        private bool sessionStarted;
        private bool closing;
        private bool latencyMonitoring;
        private bool latencyProbeRunning;
        private int latencyGeneration;

        public SshTerminalForm(Server server, string serverPassword)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server));
            this.serverPassword = serverPassword ?? "";
            displayName = Redact(server.Name ?? "Linux服务器");
            webViewDataFolder = Path.Combine(Path.GetTempPath(), "XiaoBaiTerminal_" + Guid.NewGuid().ToString("N"));

            Text = displayName + " - Linux终端";
            ClientSize = new Size(1000, 650);
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            ShowInTaskbar = true;
            BackColor = Surface;
            Font = new Font("Microsoft YaHei UI", 9F);

            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = Surface,
                Padding = new Padding(18, 0, 18, 0)
            };

            Label nameLabel = new Label
            {
                AutoEllipsis = true,
                Text = displayName,
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusDot = new Label
            {
                Text = "●",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI Symbol", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            statusLabel = new Label
            {
                AutoEllipsis = true,
                Text = "正在准备终端",
                ForeColor = MutedColor,
                TextAlign = ContentAlignment.MiddleLeft
            };
            latencyLabel = new Label
            {
                AutoEllipsis = true,
                Text = "延迟 --",
                ForeColor = MutedColor,
                TextAlign = ContentAlignment.MiddleLeft,
                Visible = false
            };
            latencyToolTip = new ToolTip();
            latencyToolTip.SetToolTip(latencyLabel, "每 5 秒测量一次 SSH 端口的 TCP 建连耗时，不代表终端命令响应时间");
            latencyTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            latencyTimer.Tick += (sender, args) => _ = MeasureLatencyAsync(latencyGeneration);
            header.Controls.Add(nameLabel);
            header.Controls.Add(statusDot);
            header.Controls.Add(statusLabel);
            header.Controls.Add(latencyLabel);
            header.SizeChanged += (sender, args) => LayoutHeader(header, nameLabel);
            statusLabel.TextChanged += (sender, args) => LayoutHeader(header, nameLabel);
            latencyLabel.VisibleChanged += (sender, args) => LayoutHeader(header, nameLabel);
            LayoutHeader(header, nameLabel);

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 24, 28)
            };

            Controls.Add(webView);
            Controls.Add(header);
            Shown += SshTerminalForm_Shown;
        }

        private void LayoutHeader(Panel header, Label nameLabel)
        {
            const int left = 18;
            const int right = 18;
            const int nameStatusGap = 14;
            const int dotWidth = 18;
            const int dotTextGap = 4;
            const int minimumStatusWidth = 96;
            const int statusLatencyGap = 14;
            const int latencyWidth = 110;

            int availableWidth = Math.Max(1, header.ClientSize.Width - left - right);
            int preferredStatusWidth = TextRenderer.MeasureText(statusLabel.Text, statusLabel.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 10;
            int reservedStatusWidth = nameStatusGap + dotWidth + dotTextGap +
                (latencyLabel.Visible ? preferredStatusWidth : minimumStatusWidth) +
                (latencyLabel.Visible ? statusLatencyGap + latencyWidth : 0);
            int maximumNameWidth = Math.Max(100, availableWidth - reservedStatusWidth);
            int preferredNameWidth = TextRenderer.MeasureText(nameLabel.Text, nameLabel.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 12;
            int nameWidth = Math.Min(preferredNameWidth, maximumNameWidth);
            int statusX = left + nameWidth + nameStatusGap;
            int labelX = statusX + dotWidth + dotTextGap;
            int remainingStatusWidth = header.ClientSize.Width - right - labelX -
                (latencyLabel.Visible ? statusLatencyGap + latencyWidth : 0);
            int statusWidth = Math.Min(latencyLabel.Visible ? preferredStatusWidth :
                Math.Max(minimumStatusWidth, preferredStatusWidth),
                Math.Max(1, remainingStatusWidth));
            int rowHeight = Math.Min(28, header.ClientSize.Height);
            int rowTop = Math.Max(0, (header.ClientSize.Height - rowHeight) / 2);

            nameLabel.Bounds = new Rectangle(left, rowTop, nameWidth, rowHeight);
            statusDot.Bounds = new Rectangle(statusX, rowTop, dotWidth, rowHeight);
            statusLabel.Bounds = new Rectangle(labelX, rowTop, statusWidth, rowHeight);
            latencyLabel.Bounds = new Rectangle(labelX + statusWidth + statusLatencyGap, rowTop,
                latencyWidth, rowHeight);
        }

        private async void SshTerminalForm_Shown(object sender, EventArgs e)
        {
            try
            {
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, webViewDataFolder, null);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.WebMessageReceived += WebMessageReceived;
                webView.NavigateToString(BuildTerminalPage());
            }
            catch (Exception ex)
            {
                SetDisconnected(GetConnectionError(ex));
            }
        }

        private void WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (closing)
                return;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(e.TryGetWebMessageAsString()))
                {
                    JsonElement root = document.RootElement;
                    string type = root.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() : "";
                    if (string.Equals(type, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        pageReady = true;
                        if (!sessionStarted)
                        {
                            sessionStarted = true;
                            _ = StartSessionAsync();
                        }
                    }
                    else if (string.Equals(type, "input", StringComparison.OrdinalIgnoreCase) &&
                             root.TryGetProperty("data", out JsonElement input))
                    {
                        WriteInput(input.GetString() ?? "");
                    }
                    else if (string.Equals(type, "resize", StringComparison.OrdinalIgnoreCase))
                    {
                        uint columns = ReadDimension(root, "cols", 120, 2, 300);
                        uint rows = ReadDimension(root, "rows", 32, 1, 120);
                        ChangeWindowSize(columns, rows);
                    }
                }
            }
            catch
            {
                // Ignore malformed browser messages; they never contain connection metadata.
            }
        }

        private async Task StartSessionAsync()
        {
            SetConnecting();
            try
            {
                client = new SshRemoteClient(server, serverPassword);
                await client.ConnectAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();

                lock (streamSync)
                {
                    shell = client.CreateShellStream(120, 32);
                }

                SetConnected();
                _ = Task.Run(() => ReadOutputAsync(cancellation.Token), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!closing)
                    SetDisconnected("连接已取消");
            }
            catch (Exception ex)
            {
                SetDisconnected(GetConnectionError(ex));
            }
        }

        private async Task ReadOutputAsync(CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int count = 0;
                    lock (streamSync)
                    {
                        if (shell != null && shell.DataAvailable)
                            count = shell.Read(buffer, 0, buffer.Length);
                    }

                    if (count > 0)
                    {
                        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
                        decoder.Convert(buffer, 0, count, chars, 0, chars.Length, false, out _, out int used, out _);
                        if (used > 0)
                            SendOutput(Redact(new string(chars, 0, used)));
                    }
                    else
                    {
                        await Task.Delay(25, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetDisconnected(GetShellError(ex));
            }
        }

        private void WriteInput(string value)
        {
            if (string.IsNullOrEmpty(value) || closing)
                return;

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Task.Run(() =>
            {
                try
                {
                    lock (streamSync)
                    {
                        if (shell == null || !shell.CanWrite)
                            return;
                        shell.Write(bytes, 0, bytes.Length);
                        shell.Flush();
                    }
                }
                catch
                {
                    if (!closing)
                        SetDisconnected("发送输入失败");
                }
            }, cancellation.Token);
        }

        private void ChangeWindowSize(uint columns, uint rows)
        {
            try
            {
                lock (streamSync)
                {
                    shell?.ChangeWindowSize(columns, rows, columns * 8, rows * 16);
                }
            }
            catch
            {
                if (!closing)
                    SetDisconnected("终端尺寸同步失败");
            }
        }

        private void SetConnecting()
        {
            UpdateStatus("连接中", Blue);
            SendStatus("连接中");
        }

        private void SetConnected()
        {
            UpdateStatus("已连接", Green);
            SetLatencyMonitoring(true);
            SendStatus("已连接");
        }

        private void SetDisconnected(string detail)
        {
            UpdateStatus(detail, Red);
            SetLatencyMonitoring(false);
            SendStatus(detail);
        }

        private void SetLatencyMonitoring(bool enabled)
        {
            if (closing || IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool>(SetLatencyMonitoring), enabled);
                return;
            }
            if (latencyMonitoring == enabled)
                return;

            latencyMonitoring = enabled;
            latencyGeneration++;
            latencyTimer.Stop();
            latencyLabel.Visible = enabled;
            latencyLabel.Text = "延迟 --";
            if (enabled)
            {
                latencyTimer.Start();
                _ = MeasureLatencyAsync(latencyGeneration);
            }
        }

        private async Task MeasureLatencyAsync(int generation)
        {
            if (closing || !latencyMonitoring || latencyProbeRunning)
                return;

            latencyProbeRunning = true;
            long? latency = null;
            try
            {
                int port = RemoteExecutorFactory.GetManagementPort(server, RemoteTransport.SSH);
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                using (TcpClient probe = new TcpClient())
                {
                    timeout.CancelAfter(2500);
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    await probe.ConnectAsync(server.IP, port, timeout.Token);
                    latency = stopwatch.ElapsedMilliseconds;
                }
            }
            catch (Exception)
            {
                // A failed TCP probe does not change the active SSH session's status.
            }
            finally
            {
                latencyProbeRunning = false;
            }

            if (closing || IsDisposed || !latencyMonitoring || generation != latencyGeneration)
                return;
            latencyLabel.Text = latency.HasValue ? "延迟 " + latency.Value + " ms" : "延迟 --";
        }

        private void UpdateStatus(string text, Color color)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, Color>(UpdateStatus), text, color);
                return;
            }
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
            statusDot.ForeColor = color;
        }

        private void SendOutput(string value)
        {
            if (string.IsNullOrEmpty(value) || closing)
                return;
            SendToPage(new TerminalPageMessage { Type = "output", Data = value });
        }

        private void SendStatus(string value)
        {
            SendToPage(new TerminalPageMessage { Type = "status", Value = value });
        }

        private void SendToPage(TerminalPageMessage message)
        {
            if (!pageReady || closing)
                return;

            try
            {
                if (webView.InvokeRequired)
                {
                    webView.BeginInvoke(new Action(() => SendToPage(message)));
                    return;
                }

                if (webView.IsDisposed || webView.CoreWebView2 == null)
                    return;
                webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(
                    message, ServerForgeJsonContext.Default.TerminalPageMessage));
            }
            catch
            {
                // The browser may be closing while an SSH read is in flight.
            }
        }

        private string GetConnectionError(Exception error)
        {
            if (error is Renci.SshNet.Common.SshAuthenticationException)
            {
                return server.SshCredentialMode == SshCredentialMode.PrivateKey
                    ? "SSH 私钥认证失败"
                    : "SSH 密码认证失败，请检查服务器凭据";
            }

            return "连接失败：" + Redact(RemoteErrorFormatter.Format(error == null ? "" : error.Message, ""));
        }

        private string GetShellError(Exception error)
        {
            string detail = Redact(RemoteErrorFormatter.Format(error == null ? "" : error.Message, ""));
            return string.IsNullOrWhiteSpace(detail) || string.Equals(detail, "远程命令失败", StringComparison.Ordinal)
                ? "SSH 会话已断开"
                : "SSH 会话已断开：" + detail;
        }

        private string Redact(string value)
        {
            string result = value ?? "";
            if (!string.IsNullOrWhiteSpace(server.IP))
                result = result.Replace(server.IP, "[已隐藏]", StringComparison.OrdinalIgnoreCase);

            int port = RemoteExecutorFactory.GetManagementPort(server, RemoteTransport.SSH);
            if (port > 0)
                result = result.Replace(port.ToString(), "[已隐藏]", StringComparison.Ordinal);
            return result;
        }

        private static uint ReadDimension(JsonElement root, string property, uint fallback, uint minimum, uint maximum)
        {
            if (!root.TryGetProperty(property, out JsonElement value) || !value.TryGetUInt32(out uint dimension))
                return fallback;
            return Math.Max(minimum, Math.Min(maximum, dimension));
        }

        private static string ReadResource(string name)
        {
            using (Stream stream = typeof(SshTerminalForm).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("终端资源不可用");
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private static string BuildTerminalPage()
        {
            string xterm = ReadResource(ProtectedText.XtermResource);
            string fit = ReadResource(ProtectedText.FitResource);
            string css = ReadResource(ProtectedText.CssResource);
            return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" + css +
                "html,body,#terminal{width:100%;height:100%;margin:0;overflow:hidden;background:#14181c;}" +
                ".xterm{height:100%;padding:10px 12px;box-sizing:border-box;}" +
                "</style></head><body><div id=\"terminal\"></div><script>" + xterm +
                "</script><script>" + fit + "</script><script>" +
                "(function(){var term=new Terminal({cursorBlink:true,scrollback:5000,convertEol:false,fontSize:14,fontFamily:'Cascadia Mono,Consolas,monospace',theme:{background:'#14181c',foreground:'#e7edf2',cursor:'#78c69b',selection:'#31556b'}});" +
                "var addon=new FitAddon.FitAddon();term.loadAddon(addon);term.open(document.getElementById('terminal'));" +
                "function send(o){window.chrome.webview.postMessage(JSON.stringify(o));}" +
                "term.onData(function(data){send({type:'input',data:data});});" +
                "term.onResize(function(size){send({type:'resize',cols:size.cols,rows:size.rows});});" +
                "window.chrome.webview.addEventListener('message',function(e){try{var m=JSON.parse(e.data);if(m.type==='output')term.write(m.data);else if(m.type==='status'&&m.value==='已连接')term.focus();}catch(_){}});" +
                "function fit(){addon.fit();}window.addEventListener('resize',fit);fit();send({type:'ready',cols:term.cols,rows:term.rows});term.focus();})();" +
                "</script></body></html>";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            latencyMonitoring = false;
            latencyGeneration++;
            latencyTimer.Stop();
            latencyTimer.Dispose();
            latencyToolTip.Dispose();
            cancellation.Cancel();
            lock (streamSync)
            {
                try { shell?.Dispose(); } catch { }
                shell = null;
            }
            try { client?.Dispose(); } catch { }
            try { webView.Dispose(); } catch { }
            CleanupWebViewDataFolder();
            cancellation.Dispose();
            base.OnFormClosing(e);
        }

        private void CleanupWebViewDataFolder()
        {
            string folder = webViewDataFolder;
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        if (!Directory.Exists(folder))
                            return;
                        Directory.Delete(folder, true);
                        return;
                    }
                    catch
                    {
                        await Task.Delay(250);
                    }
                }
            });
        }
    }
}
