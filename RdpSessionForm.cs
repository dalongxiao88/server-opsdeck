using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Devolutions.IronRdp;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using IronPixelFormat = Devolutions.IronRdp.PixelFormat;
using IronAction = Devolutions.IronRdp.Action;
using IronMousePosition = Devolutions.IronRdp.MousePosition;

namespace ServerForge
{
    /// <summary>
    /// A process-local RDP session. The protocol and rendering lifecycle are kept
    /// outside MainForm so several sessions can be opened independently.
    /// </summary>
    public sealed partial class RdpSessionForm : Form
    {
        private static readonly Color Surface = Color.White;
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Orange = Color.FromArgb(184, 116, 25);
        private static readonly Color Red = Color.FromArgb(184, 62, 62);

        private const int WmNcHitTest = 0x0084;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtClient = 1;
        private const int HtCaption = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        private readonly Server server;
        private readonly string serverPassword;
        private readonly RdpSurfaceControl surface;
        private readonly Label statusLabel;
        private readonly Label statusDot;
        private readonly Label latencyLabel;
        private readonly System.Windows.Forms.Timer latencyTimer;
        private readonly ToolTip latencyToolTip;
        private readonly Button uploadButton;
        private readonly Button downloadButton;
        private readonly Button disconnectButton;
        private readonly Button maximizeButton;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim sessionGate = new SemaphoreSlim(1, 1);
        private readonly object mouseMoveSync = new object();

        private Framed<SslStream> framed;
        private ActiveStage activeStage;
        private DecodedImage decodedImage;
        private InputDatabase inputDatabase;
        private WinCliprdr cliprdr;
        private Task readerTask;
        private Task clipboardTask;
        private Task clipboardMaintenanceTask;
        private Task startTask;
        private bool closing;
        private bool shutdownCompleted;
        private bool remoteInputEnabled;
        private bool latencyMonitoring;
        private bool latencyProbeRunning;
        private int latencyGeneration;
        private int resizeGeneration;
        private Point pendingMouseMove;
        private int mouseMoveVersion;
        private bool mouseMoveWorker;

        public RdpSessionForm(Server server, string serverPassword)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server));
            this.serverPassword = serverPassword ?? string.Empty;

            Text = (string.IsNullOrWhiteSpace(server.Name) ? server.IP : server.Name) + " - RDP";
            ClientSize = new Size(1200, 760);
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            ShowInTaskbar = true;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(142, 149, 156);
            Padding = new Padding(1);
            Font = new Font("Microsoft YaHei UI", 9F);
            // Keep keyboard messages at the RDP form level. WinForms can route
            // command keys (Ctrl/Alt/arrows/F-keys) through the form before the
            // drawing control sees them, so relying on the child control alone
            // makes input appear to work only intermittently.
            KeyPreview = true;

            Panel titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Surface,
                Padding = System.Windows.Forms.Padding.Empty
            };

            Bitmap titleIconBitmap = (Icon ?? SystemIcons.Application).ToBitmap();
            PictureBox titleIcon = new PictureBox
            {
                Image = titleIconBitmap,
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false
            };
            titleIcon.Disposed += (sender, args) => titleIconBitmap.Dispose();

            Label titleLabel = new Label
            {
                AutoEllipsis = true,
                Text = Text,
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusDot = new Label
            {
                Text = "●",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI Symbol", 8F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            statusLabel = new Label
            {
                AutoEllipsis = true,
                Text = "正在准备 RDP",
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
            latencyToolTip.SetToolTip(latencyLabel,
                "每 5 秒测量一次 RDP 端口的 TCP 建连耗时，不代表远程桌面画面响应时间");
            latencyTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            latencyTimer.Tick += (sender, args) => _ = MeasureLatencyAsync(latencyGeneration);
            uploadButton = CreateHeaderActionButton("上传文件");
            downloadButton = CreateHeaderActionButton("下载文件");
            StyleTitleCommandButton(uploadButton, Color.FromArgb(43, 113, 184),
                Color.FromArgb(33, 94, 158), Color.FromArgb(25, 77, 132));
            StyleTitleCommandButton(downloadButton, Color.FromArgb(30, 139, 91),
                Color.FromArgb(23, 116, 75), Color.FromArgb(18, 94, 61));
            uploadButton.Enabled = false;
            downloadButton.Enabled = false;
            uploadButton.Click += UploadButton_Click;
            downloadButton.Click += DownloadButton_Click;
            disconnectButton = CreateHeaderActionButton("断开连接");
            StyleTitleCommandButton(disconnectButton, Color.FromArgb(190, 62, 62),
                Color.FromArgb(164, 48, 48), Color.FromArgb(139, 39, 39));
            disconnectButton.Click += (sender, args) => Close();

            Button minimizeButton = CreateCaptionButton("\uE921", "最小化");
            maximizeButton = CreateCaptionButton("\uE922", "最大化");
            Button closeButton = CreateCaptionButton("\uE8BB", "关闭");
            closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35);
            closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(184, 14, 28);
            closeButton.MouseEnter += (sender, args) => closeButton.ForeColor = Color.White;
            closeButton.MouseLeave += (sender, args) => closeButton.ForeColor = TextColor;
            minimizeButton.Click += (sender, args) => WindowState = FormWindowState.Minimized;
            maximizeButton.Click += (sender, args) => ToggleMaximizedState();
            closeButton.Click += (sender, args) => Close();

            titleBar.Controls.Add(titleIcon);
            titleBar.Controls.Add(titleLabel);
            titleBar.Controls.Add(uploadButton);
            titleBar.Controls.Add(downloadButton);
            titleBar.Controls.Add(statusDot);
            titleBar.Controls.Add(statusLabel);
            titleBar.Controls.Add(latencyLabel);
            titleBar.Controls.Add(disconnectButton);
            titleBar.Controls.Add(minimizeButton);
            titleBar.Controls.Add(maximizeButton);
            titleBar.Controls.Add(closeButton);
            titleBar.SizeChanged += (sender, args) => LayoutTitleBar(titleBar, titleIcon, titleLabel,
                minimizeButton, closeButton);
            statusLabel.TextChanged += (sender, args) => LayoutTitleBar(titleBar, titleIcon, titleLabel,
                minimizeButton, closeButton);
            latencyLabel.VisibleChanged += (sender, args) => LayoutTitleBar(titleBar, titleIcon, titleLabel,
                minimizeButton, closeButton);
            AttachTitleBarDrag(titleBar);
            AttachTitleBarDrag(titleIcon);
            AttachTitleBarDrag(titleLabel);
            AttachTitleBarDrag(statusDot);
            AttachTitleBarDrag(statusLabel);
            AttachTitleBarDrag(latencyLabel);
            LayoutTitleBar(titleBar, titleIcon, titleLabel, minimizeButton, closeButton);

            surface = new RdpSurfaceControl();
            surface.Dock = DockStyle.Fill;
            surface.MouseMoved += (sender, point) => QueueMouseMove(point);
            surface.MouseButtonChanged += (sender, args) =>
                SendMouseButton(args.Button, args.Pressed, args.Location);
            surface.MouseWheelRotated += (sender, delta) => SendMouseWheel(delta);
            surface.KeyChanged += (sender, args) => SendKey(args.ScanCode, args.Extended, args.Pressed);
            surface.SizeChanged += (sender, args) => QueueResize(surface.ClientSize);

            Controls.Add(surface);
            Controls.Add(titleBar);
            SizeChanged += (sender, args) => UpdateMaximizeButton();
            Shown += RdpSessionForm_Shown;
        }

        private static Button CreateCaptionButton(string glyph, string accessibleName)
        {
            Button button = new Button
            {
                Text = glyph,
                AccessibleName = accessibleName,
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TextColor,
                Font = new Font("Segoe MDL2 Assets", 9F),
                TabStop = false,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 235, 238);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(218, 222, 226);
            return button;
        }

        private void LayoutTitleBar(Panel titleBar, PictureBox titleIcon, Label titleLabel,
            Button minimizeButton, Button closeButton)
        {
            const int left = 10;
            const int iconSize = 16;
            const int captionButtonWidth = 46;
            const int commandGap = 6;
            const int titleActionGap = 10;
            const int actionStatusGap = 12;
            const int dotWidth = 14;
            const int minimumStatusWidth = 84;
            const int statusLatencyGap = 12;
            const int latencyWidth = 100;
            int height = titleBar.ClientSize.Height;
            int closeLeft = Math.Max(0, titleBar.ClientSize.Width - captionButtonWidth);
            int maximizeLeft = Math.Max(0, closeLeft - captionButtonWidth);
            int minimizeLeft = Math.Max(0, maximizeLeft - captionButtonWidth);

            closeButton.Bounds = new Rectangle(closeLeft, 0, captionButtonWidth, height);
            maximizeButton.Bounds = new Rectangle(maximizeLeft, 0, captionButtonWidth, height);
            minimizeButton.Bounds = new Rectangle(minimizeLeft, 0, captionButtonWidth, height);

            int iconTop = Math.Max(0, (height - iconSize) / 2);
            titleIcon.Bounds = new Rectangle(left, iconTop, iconSize, iconSize);
            int titleLeft = titleIcon.Right + 7;
            int preferredTitleWidth = TextRenderer.MeasureText(titleLabel.Text, titleLabel.Font).Width + 4;
            int titleWidth = Math.Max(88, Math.Min(190, preferredTitleWidth));
            int uploadWidth = Math.Max(92,
                TextRenderer.MeasureText(uploadButton.Text, uploadButton.Font).Width + 28);
            int downloadWidth = Math.Max(92,
                TextRenderer.MeasureText(downloadButton.Text, downloadButton.Font).Width + 28);
            int disconnectWidth = Math.Max(100,
                TextRenderer.MeasureText(disconnectButton.Text, disconnectButton.Font).Width + 28);
            int actionsWidth = uploadWidth + downloadWidth + disconnectWidth + commandGap * 2;
            int statusRight = minimizeLeft - 8;
            int latencyReserve = latencyLabel.Visible ? statusLatencyGap + latencyWidth : 0;
            int fixedAfterTitle = titleActionGap + actionsWidth + actionStatusGap + dotWidth + 3 +
                minimumStatusWidth + latencyReserve;
            int maxTitleWidth = statusRight - titleLeft - fixedAfterTitle;
            titleWidth = Math.Max(78, Math.Min(titleWidth, maxTitleWidth));
            int buttonHeight = Math.Min(28, Math.Max(1, height - 6));
            int buttonTop = Math.Max(0, (height - buttonHeight) / 2);

            titleLabel.Bounds = new Rectangle(titleLeft, 0, titleWidth, height);
            uploadButton.Bounds = new Rectangle(titleLabel.Right + titleActionGap, buttonTop,
                uploadWidth, buttonHeight);
            downloadButton.Bounds = new Rectangle(uploadButton.Right + commandGap, buttonTop,
                downloadWidth, buttonHeight);
            disconnectButton.Bounds = new Rectangle(downloadButton.Right + commandGap, buttonTop,
                disconnectWidth, buttonHeight);
            statusDot.Bounds = new Rectangle(disconnectButton.Right + actionStatusGap, 0, dotWidth, height);
            int statusLeft = statusDot.Right + 3;
            int statusAvailable = Math.Max(0, statusRight - statusLeft - latencyReserve);
            int preferredStatusWidth = TextRenderer.MeasureText(statusLabel.Text, statusLabel.Font,
                Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 4;
            int statusWidth = latencyLabel.Visible
                ? Math.Min(Math.Max(minimumStatusWidth, preferredStatusWidth), statusAvailable)
                : statusAvailable;
            statusLabel.Bounds = new Rectangle(statusLeft, 0, Math.Max(0, statusWidth), height);
            latencyLabel.Bounds = new Rectangle(statusLabel.Right + statusLatencyGap, 0,
                latencyLabel.Visible ? Math.Min(latencyWidth,
                    Math.Max(0, statusRight - statusLabel.Right - statusLatencyGap)) : 0, height);
        }

        private void AttachTitleBarDrag(Control control)
        {
            control.MouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left)
                    return;
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
            };
            control.MouseDoubleClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left)
                    ToggleMaximizedState();
            };
        }

        private void ToggleMaximizedState()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
            UpdateMaximizeButton();
        }

        private void UpdateMaximizeButton()
        {
            if (maximizeButton == null || maximizeButton.IsDisposed)
                return;
            bool maximized = WindowState == FormWindowState.Maximized;
            maximizeButton.Text = maximized ? "\uE923" : "\uE922";
            maximizeButton.AccessibleName = maximized ? "还原" : "最大化";
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                Point point = PointToClient(Cursor.Position);
                const int border = 6;
                bool left = point.X <= border;
                bool right = point.X >= ClientSize.Width - border;
                bool top = point.Y <= border;
                bool bottom = point.Y >= ClientSize.Height - border;
                if (WindowState != FormWindowState.Maximized)
                {
                    if (top && left) { message.Result = (IntPtr)HtTopLeft; return; }
                    if (top && right) { message.Result = (IntPtr)HtTopRight; return; }
                    if (bottom && left) { message.Result = (IntPtr)HtBottomLeft; return; }
                    if (bottom && right) { message.Result = (IntPtr)HtBottomRight; return; }
                    if (left) { message.Result = (IntPtr)HtLeft; return; }
                    if (right) { message.Result = (IntPtr)HtRight; return; }
                    if (top) { message.Result = (IntPtr)HtTop; return; }
                    if (bottom) { message.Result = (IntPtr)HtBottom; return; }
                }
                message.Result = (IntPtr)HtClient;
                return;
            }
            base.WndProc(ref message);
        }

        private async void RdpSessionForm_Shown(object sender, EventArgs e)
        {
            startTask = StartSessionAsync();
            await startTask;
        }

        private async Task StartSessionAsync()
        {
            SetStatus("正在连接", MutedColor);
            Config config = null;
            try
            {
                int port = ParsePort(server.Port, 3389);
                Size requestedSize = surface.ClientSize.Width > 0 && surface.ClientSize.Height > 0
                    ? surface.ClientSize
                    : new Size(1280, 720);

                ConfigBuilder builder = ConfigBuilder.New();
                try
                {
                    builder.WithUsernameAndPassword(server.Username ?? string.Empty, serverPassword);
                    builder.SetDesktopSize((ushort)Math.Min(ushort.MaxValue, requestedSize.Height),
                        (ushort)Math.Min(ushort.MaxValue, requestedSize.Width));
                    builder.SetClientName(ProtectedText.RdpClientName);
                    builder.SetClientDir("C:\\");
                    builder.SetPerformanceFlags(PerformanceFlags.NewDefault());
                    config = builder.Build();
                }
                finally
                {
                    builder.Dispose();
                }

                CliprdrBackendFactory factory = null;
                try
                {
                    // WinCliprdr creates a hidden native window. It must be
                    // constructed on this STA/UI thread so clipboard update
                    // messages continue to be dispatched by WinForms.
                    cliprdr = WinCliprdr.New();
                    if (cliprdr != null)
                        factory = cliprdr.BackendFactory();
                }
                catch (Exception ex)
                {
                    // A clipboard integration failure must not prevent a
                    // usable RDP session from opening.
                    cliprdr = null;
                    SetStatus("剪贴板不可用：" + SanitizeError(ex.Message), Orange);
                }

                ValueTuple<ConnectionResult, Framed<SslStream>> result =
                    await RdpProtocolConnection.ConnectAsync(config, server.IP, factory, port,
                        ValidateServerCertificate, cancellation.Token);
                framed = result.Item2;
                ConnectionResult connectedResult = result.Item1;
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    // ActiveStage.New takes ownership of ConnectionResult. Read the
                    // negotiated desktop size before transferring that handle.
                    ushort width = connectedResult.GetDesktopSize().GetWidth();
                    ushort height = connectedResult.GetDesktopSize().GetHeight();
                    activeStage = ActiveStage.New(connectedResult);
                    connectedResult = null;
                    inputDatabase = InputDatabase.New();
                    decodedImage = DecodedImage.New(IronPixelFormat.BgrX32, width, height);
                    surface.SetRemoteSize(width, height);
                }
                finally
                {
                    // Dispose only if ownership was not transferred to ActiveStage.
                    connectedResult?.Dispose();
                }

                cancellation.Token.ThrowIfCancellationRequested();
                readerTask = Task.Run(ReadPduLoopAsync, cancellation.Token);
                if (cliprdr != null)
                {
                    clipboardTask = Task.Run(ClipboardLoopAsync, cancellation.Token);
                    clipboardMaintenanceTask = Task.Run(ClipboardMaintenanceLoopAsync, cancellation.Token);
                }
                remoteInputEnabled = true;
                SetTransferButtonsEnabled(true, true);
                SetStatus("已连接", Green);
                SetLatencyMonitoring(true);
                BeginInvoke(new System.Action(() =>
                {
                    if (!closing && !surface.IsDisposed && surface.CanFocus)
                    {
                        surface.Select();
                        surface.Focus();
                    }
                }));
            }
            catch (OperationCanceledException)
            {
                try { framed?.GetInner().Item1?.Dispose(); } catch { }
                SetLatencyMonitoring(false);
                if (!closing)
                    SetStatus("连接已取消", MutedColor);
            }
            catch (Exception ex)
            {
                SetLatencyMonitoring(false);
                SetTransferButtonsEnabled(false, false);
                SetStatus("连接失败：" + SanitizeError(ex.Message), Red);
            }
            finally
            {
                if (config != null)
                    config.Dispose();
            }
        }

        private async Task ReadPduLoopAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    ValueTuple<IronAction, byte[]> pdu = await framed.ReadPdu().WaitAsync(cancellation.Token);
                    await sessionGate.WaitAsync(cancellation.Token);
                    try
                    {
                        if (activeStage == null || decodedImage == null)
                            break;
                        ActiveStageOutputIterator output = activeStage.Process(decodedImage, pdu.Item1, pdu.Item2);
                        await HandleOutputsAsync(output);
                    }
                    finally
                    {
                        sessionGate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                {
                    SetLatencyMonitoring(false);
                    SetTransferButtonsEnabled(false, false);
                    SetStatus("连接中断：" + SanitizeError(ex.Message), Red);
                }
            }
        }

        private async Task ClipboardLoopAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested && cliprdr != null)
                {
                    ClipboardMessage message = null;
                    VecU8 frame = null;
                    bool gateHeld = false;
                    try
                    {
                        message = cliprdr.NextClipboardMessage();
                        if (message == null)
                        {
                            await Task.Delay(30, cancellation.Token);
                            continue;
                        }
                        ClipboardMessageType messageType = message.GetMessageType();
                        string[] localClipboardPaths = messageType == ClipboardMessageType.SendInitiateCopy ||
                            messageType == ClipboardMessageType.SendInitiateFileCopy
                            ? await TryReadLocalClipboardFilePathsAsync()
                            : null;
                        await sessionGate.WaitAsync(cancellation.Token);
                        gateHeld = true;
                        switch (messageType)
                        {
                            case ClipboardMessageType.SendFormatData:
                                frame = activeStage == null ? null : activeStage.SubmitClipboardFormatData(message.GetSendFormatData());
                                break;
                            case ClipboardMessageType.SendInitiateCopy:
                            case ClipboardMessageType.SendInitiateFileCopy:
                                frame = activeStage == null ? null : HandleLocalClipboardCopy(localClipboardPaths, message);
                                break;
                            case ClipboardMessageType.SendInitiatePaste:
                                frame = activeStage == null ? null : activeStage.InitiateClipboardPaste(message.GetSendInitiatePaste());
                                break;
                            case ClipboardMessageType.SendFileContentsRequest:
                                using (FfiFileContentsRequest request = message.GetSendFileContentsRequest())
                                    frame = await HandleUploadFileRequestAsync(request);
                                break;
                            case ClipboardMessageType.SendFileContentsResponse:
                                using (FfiFileContentsResponse response = message.GetSendFileContentsResponse())
                                    frame = await HandleDownloadFileResponseAsync(response);
                                break;
                            case ClipboardMessageType.RemoteFileList:
                                using (FfiRemoteFileList files = message.GetRemoteFileList())
                                    frame = BeginDownloadFromRemoteList(files);
                                break;
                            case ClipboardMessageType.Error:
                                if (!closing)
                                    SetStatus("剪贴板通道返回错误", Orange);
                                break;
                        }
                        if (frame != null)
                            await WriteClipboardFrameAsync(frame);
                    }
                    finally
                    {
                        if (gateHeld)
                            sessionGate.Release();
                        frame?.Dispose();
                        message?.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("剪贴板通道异常：" + SanitizeError(ex.Message), Orange);
            }
        }

        private async Task ClipboardMaintenanceLoopAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested && cliprdr != null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                    VecU8 frame = null;
                    await sessionGate.WaitAsync(cancellation.Token);
                    try
                    {
                        if (activeStage == null)
                            return;
                        frame = activeStage.DriveClipboardTimeouts();
                        if (frame != null)
                            await WriteClipboardFrameAsync(frame);
                    }
                    finally
                    {
                        frame?.Dispose();
                        sessionGate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("剪贴板维护异常：" + SanitizeError(ex.Message), Orange);
            }
        }

        private async Task HandleOutputsAsync(ActiveStageOutputIterator output)
        {
            if (output == null)
                return;
            try
            {
                while (!output.IsEmpty())
                {
                    ActiveStageOutput item = output.Next();
                    if (item == null)
                        break;

                    ActiveStageOutputType type = item.GetEnumType();
                    if (type == ActiveStageOutputType.ResponseFrame)
                    {
                        BytesSlice response = item.GetResponseFrame();
                        byte[] bytes = new byte[(int)response.GetSize()];
                        response.Fill(bytes);
                        if (framed != null)
                            await framed.GetInner().Item1.WriteAsync(bytes, cancellation.Token);
                    }
                    else if (type == ActiveStageOutputType.GraphicsUpdate)
                    {
                        PublishFrame();
                    }
                    else if (type == ActiveStageOutputType.Terminate)
                    {
                        SetLatencyMonitoring(false);
                        if (!closing)
                            SetStatus("服务器已断开", MutedColor);
                        return;
                    }
                    else if (type == ActiveStageOutputType.DeactivateAll)
                    {
                        await ReactivateAfterResizeAsync();
                    }
                }
            }
            finally
            {
                output.Dispose();
            }
        }

        private void PublishFrame()
        {
            if (decodedImage == null)
                return;

            ushort width = decodedImage.GetWidth();
            ushort height = decodedImage.GetHeight();
            BytesSlice data = decodedImage.GetData();
            byte[] frame = new byte[(int)data.GetSize()];
            data.Fill(frame);
            surface.SetFrame(frame, width, height);
        }

        private void QueueResize(Size size)
        {
            if (closing || !remoteInputEnabled || activeStage == null ||
                size.Width < 80 || size.Height < 80)
                return;

            int generation = Interlocked.Increment(ref resizeGeneration);
            _ = ResizeAfterDelayAsync(size, generation);
        }

        private async Task ResizeAfterDelayAsync(Size size, int generation)
        {
            try
            {
                await Task.Delay(180, cancellation.Token);
                if (closing || !remoteInputEnabled ||
                    generation != Volatile.Read(ref resizeGeneration))
                    return;

                int width = Math.Max(80, Math.Min(ushort.MaxValue, size.Width));
                int height = Math.Max(80, Math.Min(ushort.MaxValue, size.Height));
                await sessionGate.WaitAsync(cancellation.Token);
                try
                {
                    if (closing || !remoteInputEnabled || activeStage == null || framed == null)
                        return;

                    // EncodedResize emits the Display Control request. The server
                    // answers with DeactivateAll; the reader then reactivates the
                    // session and rebuilds the decoded framebuffer at the new size.
                    ActiveStageOutputIterator output = activeStage.EncodedResize(
                        (uint)width, (uint)height);
                    if (output != null)
                        await HandleOutputsAsync(output);
                }
                finally
                {
                    sessionGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("调整远程分辨率失败：" + SanitizeError(ex.Message), Orange);
            }
        }

        private async Task ReactivateAfterResizeAsync()
        {
            if (closing || cancellation.IsCancellationRequested || activeStage == null || framed == null)
                return;

            using (ConnectionActivationSequence activation = activeStage.CreateConnectionActivation())
            using (WriteBuf writeBuffer = WriteBuf.New())
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await RdpProtocolConnection.SingleSequenceStepAsync(
                        activation, writeBuffer, framed, cancellation.Token);
                    using (ConnectionActivationState state = activation.GetState())
                    {
                        if (state.GetType() != ConnectionActivationStateType.Finalized)
                            continue;

                        using (ConnectionActivationStateFinalized finalized = state.GetFinalized())
                        using (DesktopSize desktopSize = finalized.GetDesktopSize())
                        {
                            ushort width = desktopSize.GetWidth();
                            ushort height = desktopSize.GetHeight();
                            DecodedImage nextImage = DecodedImage.New(
                                IronPixelFormat.BgrX32, width, height);
                            try
                            {
                                activeStage.Reactivate(
                                    activation.GetIoChannelId(),
                                    activation.GetUserChannelId(),
                                    finalized.GetShareId(),
                                    finalized.GetEnableServerPointer(),
                                    finalized.GetPointerSoftwareRendering(),
                                    finalized.GetStaticChannelChunkSize(),
                                    finalized.GetWindowSupportLevel());

                                DecodedImage previousImage = decodedImage;
                                decodedImage = nextImage;
                                nextImage = null;
                                previousImage?.Dispose();
                                surface.SetRemoteSize(width, height);
                                return;
                            }
                            finally
                            {
                                nextImage?.Dispose();
                            }
                        }
                    }
                }
            }
        }

        private void QueueMouseMove(Point point)
        {
            if (closing || activeStage == null)
                return;
            lock (mouseMoveSync)
            {
                pendingMouseMove = point;
                mouseMoveVersion++;
                if (mouseMoveWorker)
                    return;
                mouseMoveWorker = true;
            }
            _ = DrainMouseMovesAsync();
        }

        private async Task DrainMouseMovesAsync()
        {
            int handledVersion = 0;
            try
            {
                while (!closing)
                {
                    Point point;
                    int currentVersion;
                    lock (mouseMoveSync)
                    {
                        currentVersion = mouseMoveVersion;
                        if (currentVersion == handledVersion)
                        {
                            mouseMoveWorker = false;
                            return;
                        }
                        point = pendingMouseMove;
                    }
                    await SendMouseMoveAsync(point);
                    handledVersion = currentVersion;
                }
            }
            finally
            {
                lock (mouseMoveSync)
                    mouseMoveWorker = false;
            }
        }

        private async Task SendMouseMoveAsync(Point point)
        {
            if (closing || cancellation.IsCancellationRequested)
                return;
            await sessionGate.WaitAsync(cancellation.Token);
            try
            {
                if (activeStage == null || decodedImage == null)
                    return;
                Point remote = surface.ToRemotePoint(point);
                IronMousePosition position = IronMousePosition.New((ushort)remote.X, (ushort)remote.Y);
                Operation operation = position.AsMoveOperation();
                FastPathInputEventIterator input = null;
                try
                {
                    input = inputDatabase.Apply(operation);
                    ActiveStageOutputIterator output = activeStage.ProcessFastpathInput(decodedImage, input);
                    await HandleOutputsAsync(output);
                }
                finally
                {
                    input?.Dispose();
                    operation.Dispose();
                    position.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("鼠标输入失败：" + SanitizeError(ex.Message), Orange);
            }
            finally
            {
                sessionGate.Release();
            }
        }

        private async void SendMouseButton(MouseButtonType button, bool pressed, Point point)
        {
            await SendMouseButtonAsync(button, pressed, point);
        }

        private async Task SendMouseButtonAsync(MouseButtonType button, bool pressed, Point point)
        {
            if (closing || cancellation.IsCancellationRequested)
                return;

            bool gateHeld = false;
            IronMousePosition position = null;
            Operation moveOperation = null;
            MouseButton mouseButton = null;
            Operation buttonOperation = null;
            try
            {
                await sessionGate.WaitAsync(cancellation.Token);
                gateHeld = true;
                if (activeStage == null || decodedImage == null || inputDatabase == null)
                    return;

                // A remote resize border is only a few pixels wide. Always send
                // the exact click position immediately before the button state so
                // IronRDP cannot apply the press/release at a stale move position.
                Point remote = surface.ToRemotePoint(point);
                position = IronMousePosition.New((ushort)remote.X, (ushort)remote.Y);
                moveOperation = position.AsMoveOperation();
                mouseButton = MouseButton.New(button);
                buttonOperation = pressed
                    ? mouseButton.AsOperationMouseButtonPressed()
                    : mouseButton.AsOperationMouseButtonReleased();

                await ProcessInputOperationAsync(moveOperation);
                await ProcessInputOperationAsync(buttonOperation);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("鼠标输入失败：" + SanitizeError(ex.Message), Orange);
            }
            finally
            {
                buttonOperation?.Dispose();
                mouseButton?.Dispose();
                moveOperation?.Dispose();
                position?.Dispose();
                if (gateHeld)
                    sessionGate.Release();
            }
        }

        private async Task ProcessInputOperationAsync(Operation operation)
        {
            FastPathInputEventIterator input = null;
            try
            {
                input = inputDatabase.Apply(operation);
                ActiveStageOutputIterator output = activeStage.ProcessFastpathInput(decodedImage, input);
                await HandleOutputsAsync(output);
            }
            finally
            {
                input?.Dispose();
            }
        }

        private async void SendMouseWheel(short delta)
        {
            await SendOperationAsync(WheelRotations.New(delta > 0, (short)Math.Max(1, Math.Abs(delta) / 120)),
                value => value.AsOperation());
        }

        private void SendKey(byte scanCode, bool extended, bool pressed)
        {
            _ = SendKeyAsync(scanCode, extended, pressed);
        }

        private async Task SendKeyAsync(byte scanCode, bool extended, bool pressed)
        {
            // Use IronRDP's packed representation (0xE000 | code for an
            // extended key), matching the library's desktop client sample.
            ushort packedScanCode = (ushort)(scanCode | (extended ? 0xE000 : 0));
            await SendOperationAsync(Scancode.FromU16(packedScanCode), pressed
                ? (Func<Scancode, Operation>)(value => value.AsOperationKeyPressed())
                : value => value.AsOperationKeyReleased());
        }

        private async Task SendOperationAsync<T>(T inputSource, Func<T, Operation> operationFactory)
            where T : IDisposable
        {
            Operation operation = null;
            try
            {
                if (closing || cancellation.IsCancellationRequested)
                    return;
                operation = operationFactory(inputSource);
                await sessionGate.WaitAsync(cancellation.Token);
                try
                {
                    if (activeStage == null || decodedImage == null || inputDatabase == null)
                        return;
                    FastPathInputEventIterator input = null;
                    try
                    {
                        input = inputDatabase.Apply(operation);
                        ActiveStageOutputIterator output = activeStage.ProcessFastpathInput(decodedImage, input);
                        await HandleOutputsAsync(output);
                    }
                    finally
                    {
                        input?.Dispose();
                    }
                }
                finally
                {
                    sessionGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("键鼠输入失败：" + SanitizeError(ex.Message), Orange);
            }
            finally
            {
                if (operation != null)
                    operation.Dispose();
                inputSource.Dispose();
            }
        }

        private void SetStatus(string text, Color color)
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new System.Action(() => SetStatus(text, color))); }
                catch (InvalidOperationException) { }
                return;
            }
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
            statusDot.ForeColor = color;
        }

        private void SetLatencyMonitoring(bool enabled)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new System.Action<bool>(SetLatencyMonitoring), enabled); }
                catch (InvalidOperationException) { }
                return;
            }
            if (closing && enabled)
                return;
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
                int port = ParsePort(server.Port, 3389);
                using (CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                using (TcpClient probe = new TcpClient())
                {
                    timeout.CancelAfter(2500);
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    await probe.ConnectAsync(server.IP, port, timeout.Token);
                    stopwatch.Stop();
                    latency = stopwatch.ElapsedMilliseconds;
                }
            }
            catch (Exception)
            {
                // A failed probe must not affect an already active RDP session.
            }
            finally
            {
                latencyProbeRunning = false;
            }

            if (closing || IsDisposed || !latencyMonitoring || generation != latencyGeneration)
                return;
            latencyLabel.Text = latency.HasValue ? "延迟 " + latency.Value + " ms" : "延迟 --";
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate,
            X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null)
                return false;
            if (errors == SslPolicyErrors.None)
                return true;
            if (closing || IsDisposed)
                return false;

            X509Certificate2 certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
            string fingerprint = certificate2.GetCertHashString(HashAlgorithmName.SHA256);
            string message = "RDP 服务器证书无法由系统验证。\r\n\r\n" +
                "主题：" + certificate2.Subject + "\r\n" +
                "颁发者：" + certificate2.Issuer + "\r\n" +
                "SHA-256：" + fingerprint + "\r\n\r\n" +
                "只有在确认地址和证书指纹属于目标服务器时才继续连接。";
            DialogResult result = DialogResult.No;
            System.Action showPrompt = () => result = MessageBox.Show(this, message, "确认 RDP 服务器证书",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            try
            {
                if (InvokeRequired)
                    Invoke(showPrompt);
                else
                    showPrompt();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return result == DialogResult.Yes;
        }

        private async Task ShutdownAsync()
        {
            remoteInputEnabled = false;
            SetLatencyMonitoring(false);
            SetTransferButtonsEnabled(false, false);
            cancellation.Cancel();
            try { framed?.GetInner().Item1?.Dispose(); } catch { }

            if (startTask != null && !startTask.IsCompleted)
            {
                try { await startTask; }
                catch { }
            }

            Task[] tasks = new[] { readerTask, clipboardTask, clipboardMaintenanceTask };
            foreach (Task task in tasks)
            {
                if (task == null)
                    continue;
                try { await task; }
                catch { }
            }

            // The clipboard loop is the only producer of this task. Waiting for
            // it first makes this final capture stable during shutdown.
            Task publishTask = clipboardPublishTask;
            if (publishTask != null)
            {
                try { await publishTask; }
                catch { }
            }

            CancelFileTransfers();

            await sessionGate.WaitAsync();
            try
            {
                inputDatabase?.Dispose();
                decodedImage?.Dispose();
                activeStage?.Dispose();
                inputDatabase = null;
                decodedImage = null;
                activeStage = null;
                framed = null;
            }
            finally
            {
                sessionGate.Release();
            }
            // WinCliprdr owns a window created on the UI thread. Dispose after
            // both the polling loop and the channel backend have stopped.
            try { cliprdr?.Dispose(); } catch { }
            sessionGate.Dispose();
            cancellation.Dispose();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!shutdownCompleted)
            {
                e.Cancel = true;
                if (!closing)
                {
                    closing = true;
                    disconnectButton.Enabled = false;
                    SetTransferButtonsEnabled(false, false);
                    SetStatus("正在断开", MutedColor);
                    _ = FinishClosingAsync();
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            latencyTimer.Dispose();
            latencyToolTip.Dispose();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // ProcessCmdKey runs before the normal child-control key path for
            // shortcuts. Forward the original Win32 message so the scan code,
            // extended-key bit, and press/release state remain intact.
            if (TryForwardKeyMessage(ref msg))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessKeyPreview(ref Message msg)
        {
            if (TryForwardKeyMessage(ref msg))
                return true;
            return base.ProcessKeyPreview(ref msg);
        }

        private bool TryForwardKeyMessage(ref Message msg)
        {
            const int WmKeyDown = 0x0100;
            const int WmKeyUp = 0x0101;
            const int WmChar = 0x0102;
            const int WmSysKeyDown = 0x0104;
            const int WmSysKeyUp = 0x0105;
            const int WmSysChar = 0x0106;

            if (!remoteInputEnabled || closing || surface == null || surface.IsDisposed)
                return false;

            if ((msg.Msg == WmKeyDown || msg.Msg == WmSysKeyDown) &&
                msg.WParam.ToInt64() == (long)Keys.V &&
                (Control.ModifierKeys & Keys.Control) == Keys.Control &&
                ClipboardContainsFiles())
            {
                _ = SendLocalClipboardFilesToRemoteAndPasteAsync();
                msg.Result = IntPtr.Zero;
                return true;
            }

            if (msg.Msg == WmChar || msg.Msg == WmSysChar)
            {
                // The RDP input channel receives physical key events. Consume
                // the translated character message so it cannot leak into a
                // local WinForms control or trigger a system beep.
                msg.Result = IntPtr.Zero;
                return true;
            }

            if (msg.Msg != WmKeyDown && msg.Msg != WmKeyUp &&
                msg.Msg != WmSysKeyDown && msg.Msg != WmSysKeyUp)
                return false;

            long data = msg.LParam.ToInt64();
            byte scanCode = (byte)((data >> 16) & 0xff);
            bool extended = (data & 0x01000000) != 0;
            if (scanCode == 0)
            {
                // Synthetic key messages may not carry a scan code. Use the
                // extended virtual-key mapping as a reliable fallback.
                uint mapped = MapVirtualKey((uint)msg.WParam.ToInt64(), 4);
                scanCode = (byte)(mapped & 0xff);
                extended = (mapped & 0xff00) == 0xe000;
            }

            if (scanCode == 0)
                return true;

            bool pressed = msg.Msg == WmKeyDown || msg.Msg == WmSysKeyDown;
            SendKey(scanCode, extended, pressed);
            msg.Result = IntPtr.Zero;
            return true;
        }

        private static bool ClipboardContainsFiles()
        {
            try { return IsClipboardFormatAvailable(ClipboardFormatFileDrop); }
            catch (Exception) { return false; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private async Task FinishClosingAsync()
        {
            try
            {
                await ShutdownAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("RDP shutdown failed: " + ex);
            }
            finally
            {
                shutdownCompleted = true;
                if (!IsDisposed)
                {
                    try { BeginInvoke(new System.Action(Close)); }
                    catch (InvalidOperationException) { }
                }
            }
        }

        private static int ParsePort(string value, int fallback)
        {
            int port;
            return int.TryParse(value, out port) && port > 0 && port <= 65535 ? port : fallback;
        }

        private static string SanitizeError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "未知错误";
            return message.Length > 140 ? message.Substring(0, 140) : message;
        }

        private sealed class RdpSurfaceControl : Control
        {
            private Bitmap bitmap;
            private int remoteWidth;
            private int remoteHeight;
            private MouseButtons pressedMouseButtons;

            public event EventHandler<Point> MouseMoved;
            public event EventHandler<MouseButtonEventArgs> MouseButtonChanged;
            public event EventHandler<short> MouseWheelRotated;
            public event EventHandler<KeyChangedEventArgs> KeyChanged;

            public RdpSurfaceControl()
            {
                BackColor = Color.FromArgb(15, 18, 22);
                TabStop = true;
                SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override bool IsInputKey(Keys keyData)
            {
                // Treat navigation and shortcut keys as remote input instead of
                // letting the WinForms dialog-navigation layer consume them.
                return true;
            }

            public void SetRemoteSize(int width, int height)
            {
                if (InvokeRequired)
                {
                    try { BeginInvoke(new System.Action(() => SetRemoteSize(width, height))); }
                    catch (InvalidOperationException) { }
                    return;
                }
                remoteWidth = Math.Max(1, width);
                remoteHeight = Math.Max(1, height);
                Bitmap old = bitmap;
                bitmap = null;
                old?.Dispose();
                Invalidate();
            }

            public void SetFrame(byte[] data, int width, int height)
            {
                if (data == null || width <= 0 || height <= 0)
                    return;
                if (InvokeRequired)
                {
                    try { BeginInvoke(new System.Action(() => SetFrame(data, width, height))); }
                    catch (InvalidOperationException) { }
                    return;
                }

                Bitmap next = new Bitmap(width, height, DrawingPixelFormat.Format32bppRgb);
                BitmapData locked = next.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppRgb);
                try
                {
                    int sourceStride = width * 4;
                    for (int row = 0; row < height; row++)
                    {
                        IntPtr destination = IntPtr.Add(locked.Scan0, row * locked.Stride);
                        System.Runtime.InteropServices.Marshal.Copy(data, row * sourceStride, destination, sourceStride);
                    }
                }
                finally
                {
                    next.UnlockBits(locked);
                }

                Bitmap old = bitmap;
                bitmap = next;
                remoteWidth = width;
                remoteHeight = height;
                old?.Dispose();
                Invalidate();
            }

            public Point ToRemotePoint(Point point)
            {
                Rectangle destination = GetDestinationRectangle();
                if (destination.Width <= 0 || destination.Height <= 0)
                    return Point.Empty;
                int x = (point.X - destination.X) * remoteWidth / destination.Width;
                int y = (point.Y - destination.Y) * remoteHeight / destination.Height;
                return new Point(Math.Max(0, Math.Min(remoteWidth - 1, x)),
                    Math.Max(0, Math.Min(remoteHeight - 1, y)));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.Clear(BackColor);
                if (bitmap == null)
                    return;
                Rectangle destination = GetDestinationRectangle();
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(bitmap, destination);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                MouseMoved?.Invoke(this, e.Location);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (CanFocus)
                {
                    Select();
                    Focus();
                }
                pressedMouseButtons |= e.Button;
                Capture = true;
                MouseButtonType button = ToMouseButton(e.Button);
                if (button != MouseButtonType.Left || e.Button == MouseButtons.Left)
                    MouseButtonChanged?.Invoke(this, new MouseButtonEventArgs(button, true, e.Location));
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool wasPressed = (pressedMouseButtons & e.Button) != 0;
                pressedMouseButtons &= ~e.Button;
                if (wasPressed)
                    MouseButtonChanged?.Invoke(this,
                        new MouseButtonEventArgs(ToMouseButton(e.Button), false, e.Location));
                if (pressedMouseButtons == MouseButtons.None)
                    Capture = false;
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (Capture || pressedMouseButtons == MouseButtons.None)
                    return;

                // Another window can take capture before WinForms receives the
                // normal MouseUp. Release every tracked remote button so a lost
                // capture never leaves the RDP session in a permanent drag state.
                MouseButtons buttonsToRelease = pressedMouseButtons;
                pressedMouseButtons = MouseButtons.None;
                Point location = PointToClient(Cursor.Position);
                ReleasePressedButton(buttonsToRelease, MouseButtons.Left, location);
                ReleasePressedButton(buttonsToRelease, MouseButtons.Right, location);
                ReleasePressedButton(buttonsToRelease, MouseButtons.Middle, location);
                ReleasePressedButton(buttonsToRelease, MouseButtons.XButton1, location);
                ReleasePressedButton(buttonsToRelease, MouseButtons.XButton2, location);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                short delta = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, e.Delta));
                MouseWheelRotated?.Invoke(this, delta);
            }

            protected override void WndProc(ref Message m)
            {
                const int WmGetDlgCode = 0x0087;
                const int WmKeyDown = 0x0100;
                const int WmKeyUp = 0x0101;
                const int WmSysKeyDown = 0x0104;
                const int WmSysKeyUp = 0x0105;
                if (m.Msg == WmGetDlgCode)
                {
                    // Ask WinForms/the dialog manager to deliver every
                    // navigation and character key to this surface.
                    m.Result = new IntPtr(0x0004 | 0x0001 | 0x0002 | 0x0080);
                    return;
                }
                if (m.Msg == WmKeyDown || m.Msg == WmKeyUp || m.Msg == WmSysKeyDown || m.Msg == WmSysKeyUp)
                {
                    long data = m.LParam.ToInt64();
                    byte scanCode = (byte)((data >> 16) & 0xff);
                    bool extended = (data & 0x01000000) != 0;
                    bool pressed = m.Msg == WmKeyDown || m.Msg == WmSysKeyDown;
                    if (scanCode != 0)
                        KeyChanged?.Invoke(this, new KeyChangedEventArgs(scanCode, extended, pressed));
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    bitmap?.Dispose();
                    bitmap = null;
                }
                base.Dispose(disposing);
            }

            private Rectangle GetDestinationRectangle()
            {
                if (remoteWidth <= 0 || remoteHeight <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                    return Rectangle.Empty;
                float scale = Math.Min((float)ClientSize.Width / remoteWidth,
                    (float)ClientSize.Height / remoteHeight);
                int width = Math.Max(1, (int)(remoteWidth * scale));
                int height = Math.Max(1, (int)(remoteHeight * scale));
                return new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2,
                    width, height);
            }

            private static MouseButtonType ToMouseButton(MouseButtons button)
            {
                if ((button & MouseButtons.Right) != 0)
                    return MouseButtonType.Right;
                if ((button & MouseButtons.Middle) != 0)
                    return MouseButtonType.Middle;
                if ((button & MouseButtons.XButton1) != 0)
                    return MouseButtonType.X1;
                if ((button & MouseButtons.XButton2) != 0)
                    return MouseButtonType.X2;
                return MouseButtonType.Left;
            }

            private void ReleasePressedButton(MouseButtons pressedButtons, MouseButtons button, Point location)
            {
                if ((pressedButtons & button) != 0)
                    MouseButtonChanged?.Invoke(this,
                        new MouseButtonEventArgs(ToMouseButton(button), false, location));
            }
        }

        private sealed class MouseButtonEventArgs : EventArgs
        {
            public MouseButtonType Button { get; }
            public bool Pressed { get; }
            public Point Location { get; }

            public MouseButtonEventArgs(MouseButtonType button, bool pressed, Point location)
            {
                Button = button;
                Pressed = pressed;
                Location = location;
            }
        }

        private sealed class KeyChangedEventArgs : EventArgs
        {
            public byte ScanCode { get; }
            public bool Extended { get; }
            public bool Pressed { get; }

            public KeyChangedEventArgs(byte scanCode, bool extended, bool pressed)
            {
                ScanCode = scanCode;
                Extended = extended;
                Pressed = pressed;
            }
        }
    }
}
