using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Devolutions.IronRdp;

namespace ServerForge
{
    public sealed partial class RdpSessionForm
    {
        private const uint DownloadChunkSize = 1024 * 1024;
        private const uint MaxUploadChunkSize = 16 * 1024 * 1024;
        private const uint ClipboardFormatFileDrop = 15;
        private const uint GlobalMemoryMoveable = 0x0002;
        private const uint GlobalMemoryZeroInit = 0x0040;
        private const int DropFilesHeaderSize = 20;

        private readonly object fileTransferSync = new object();
        private UploadTransferState uploadTransfer;
        private DownloadTransferState downloadTransfer;
        private string pendingDownloadDirectory;
        private int pendingDownloadGeneration;
        private int filePasteInProgress;
        private string publishedRemoteClipboardRoot;
        private string publishingRemoteClipboardRoot;
        private Task clipboardPublishTask;
        private uint nextFileStreamId = 1;

        private static Button CreateHeaderActionButton(string text)
        {
            Button button = new Button
            {
                Text = text,
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Padding = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 235, 238);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(218, 222, 226);
            return button;
        }

        private static void StyleTitleCommandButton(Button button, Color backColor,
            Color hoverColor, Color pressedColor)
        {
            button.BackColor = backColor;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = hoverColor;
            button.FlatAppearance.MouseDownBackColor = pressedColor;
        }

        private VecU8 HandleLocalClipboardCopy(string[] paths, ClipboardMessage message)
        {
            try
            {
                // Files downloaded from the remote clipboard are published as real
                // local paths. Do not advertise those paths back to the same remote
                // session when Windows raises the resulting clipboard update.
                if (ArePathsFromPublishedRemoteClipboard(paths))
                    return null;

                UploadTransferState transfer = CreateUploadTransfer(paths);
                if (transfer != null)
                {
                    uploadTransfer = transfer;
                    SetStatus("已准备剪贴板文件：" + transfer.DisplayName, Orange);
                    return activeStage.InitiateClipboardFileCopy(EncodeUploadPaths(transfer), 0);
                }

                // A non-file clipboard update must clear the local file state. The
                // native CLIPRDR processor also drops its file descriptor list when
                // it receives a normal FormatList.
                uploadTransfer = null;
                using (ClipboardFormatIterator formats = message.GetSendInitiateCopy())
                {
                    return formats == null ? null : activeStage.InitiateClipboardCopy(formats);
                }
            }
            catch (Exception ex)
            {
                uploadTransfer = null;
                if (!closing)
                    SetStatus("本机剪贴板同步失败：" + SanitizeError(ex.Message), Orange);
                return null;
            }
        }

        private async Task<string[]> TryReadLocalClipboardFilePathsAsync()
        {
            // Clipboard access is an STA/UI operation. The native clipboard
            // monitor already observed the change, so marshal the short read to
            // the form thread and retry only when Windows reports a busy clipboard.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string[] paths = await ReadLocalClipboardFilePathsOnUiThreadAsync();
                if (paths != null)
                    return paths;

                try
                {
                    await Task.Delay(40, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return Array.Empty<string>();
                }
            }
            return Array.Empty<string>();
        }

        private Task<string[]> ReadLocalClipboardFilePathsOnUiThreadAsync()
        {
            if (!InvokeRequired)
                return Task.FromResult(ReadLocalClipboardFilePaths());

            TaskCompletionSource<string[]> completion = new TaskCompletionSource<string[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                BeginInvoke(new System.Action(() =>
                {
                    try { completion.TrySetResult(ReadLocalClipboardFilePaths()); }
                    catch (Exception) { completion.TrySetResult(null); }
                }));
            }
            catch (Exception)
            {
                completion.TrySetResult(Array.Empty<string>());
            }
            return completion.Task;
        }

        private static string[] ReadLocalClipboardFilePaths()
        {
            try
            {
                if (!IsClipboardFormatAvailable(ClipboardFormatFileDrop))
                    return Array.Empty<string>();

                if (!OpenClipboard(IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                try
                {
                    IntPtr dropHandle = GetClipboardData(ClipboardFormatFileDrop);
                    if (dropHandle == IntPtr.Zero)
                        throw new InvalidOperationException("Windows 剪贴板没有返回文件列表");

                    uint count = DragQueryFile(dropHandle, uint.MaxValue, null, 0);
                    if (count > 4096)
                        throw new InvalidOperationException("剪贴板中的文件数量过多");

                    List<string> paths = new List<string>((int)count);
                    for (uint index = 0; index < count; index++)
                    {
                        uint length = DragQueryFile(dropHandle, index, null, 0);
                        if (length == 0 || length >= short.MaxValue)
                            continue;

                        StringBuilder value = new StringBuilder(checked((int)length + 1));
                        if (DragQueryFile(dropHandle, index, value, (uint)value.Capacity) > 0 &&
                            !string.IsNullOrWhiteSpace(value.ToString()))
                        {
                            paths.Add(value.ToString());
                        }
                    }
                    return paths.ToArray();
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception)
            {
                // Clipboard may be temporarily locked by Explorer or another
                // clipboard viewer. The caller will retry on the UI thread.
                return null;
            }
        }

        private static UploadTransferState CreateUploadTransfer(IEnumerable<string> paths)
        {
            if (paths == null)
                return null;

            List<UploadFileItem> files = new List<UploadFileItem>();
            foreach (string path in paths)
            {
                if (files.Count >= 4096 || string.IsNullOrWhiteSpace(path))
                    break;

                string fullPath;
                try { fullPath = Path.GetFullPath(path); }
                catch (Exception) { continue; }

                bool isDirectory = Directory.Exists(fullPath);
                if (!isDirectory && !File.Exists(fullPath))
                    continue;

                string name;
                ulong length;
                try
                {
                    if (isDirectory)
                    {
                        name = new DirectoryInfo(fullPath).Name;
                        length = 0;
                    }
                    else
                    {
                        FileInfo info = new FileInfo(fullPath);
                        name = info.Name;
                        length = (ulong)info.Length;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\n') >= 0 || name.IndexOf('\r') >= 0)
                    continue;
                files.Add(new UploadFileItem(fullPath, name, length, isDirectory));
            }

            return files.Count == 0 ? null : new UploadTransferState(files);
        }

        private static string EncodeUploadPaths(UploadTransferState transfer)
        {
            List<string> paths = new List<string>(transfer.Files.Count);
            foreach (UploadFileItem file in transfer.Files)
                paths.Add(file.Path);
            return string.Join("\n", paths);
        }

        private async Task SendLocalClipboardFilesToRemoteAndPasteAsync()
        {
            if (!CanTransferFiles() || System.Threading.Interlocked.Exchange(ref filePasteInProgress, 1) != 0)
                return;

            try
            {
                string[] paths = await TryReadLocalClipboardFilePathsAsync();
                UploadTransferState transfer = CreateUploadTransfer(paths);
                if (transfer == null)
                    return;

                VecU8 frame = null;
                bool gateHeld = false;
                try
                {
                    await sessionGate.WaitAsync(cancellation.Token);
                    gateHeld = true;
                    if (!CanTransferFiles())
                        return;

                    uploadTransfer = transfer;
                    frame = activeStage.InitiateClipboardFileCopy(EncodeUploadPaths(transfer), 0);
                    await WriteClipboardFrameAsync(frame);
                }
                finally
                {
                    frame?.Dispose();
                    if (gateHeld)
                        sessionGate.Release();
                }

                SetTransferButtonsEnabled(false, false);
                SetStatus("正在通知远程服务器：" + transfer.DisplayName, Orange);
                if (!await TriggerRemoteFilePasteAsync(transfer))
                    FailUploadStart(transfer);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                uploadTransfer = null;
                SetStatus("本机文件粘贴失败：" + SanitizeError(ex.Message), Red);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref filePasteInProgress, 0);
            }
        }

        private async void UploadButton_Click(object sender, EventArgs e)
        {
            if (!CanTransferFiles())
            {
                SetStatus("文件通道尚未连接", Orange);
                return;
            }

            using OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "选择要上传到远程服务器的文件",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            FileInfo file = new FileInfo(dialog.FileName);
            if (!file.Exists)
            {
                SetStatus("上传失败：本机文件不存在", Red);
                return;
            }

            SetTransferButtonsEnabled(false, false);
            SetStatus("正在准备上传：" + file.Name, Orange);
            VecU8 frame = null;
            try
            {
                await sessionGate.WaitAsync(cancellation.Token);
                try
                {
                    if (!CanTransferFiles())
                        throw new InvalidOperationException("RDP 文件通道已断开");

                    uploadTransfer = CreateUploadTransfer(new[] { file.FullName });
                    if (uploadTransfer == null)
                        throw new InvalidOperationException("无法读取本机文件");
                    frame = activeStage.InitiateClipboardFileCopy(EncodeUploadPaths(uploadTransfer), 0);
                    await WriteClipboardFrameAsync(frame);
                }
                finally
                {
                    frame?.Dispose();
                        sessionGate.Release();
                }

                SetStatus("正在通知远程服务器：" + file.Name, Orange);
                if (!await TriggerRemoteFilePasteAsync(uploadTransfer))
                    FailUploadStart(uploadTransfer);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                uploadTransfer = null;
                SetTransferButtonsEnabled(true, true);
                SetStatus("上传失败：" + SanitizeError(ex.Message), Red);
            }
        }

        private async void DownloadButton_Click(object sender, EventArgs e)
        {
            if (!CanTransferFiles())
            {
                SetStatus("文件通道尚未连接", Orange);
                return;
            }

            using FolderBrowserDialog dialog = new FolderBrowserDialog
            {
                Description = "选择远程文件在本机的保存目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            int generation;
            lock (fileTransferSync)
            {
                pendingDownloadDirectory = Path.GetFullPath(dialog.SelectedPath);
                pendingDownloadGeneration++;
                generation = pendingDownloadGeneration;
            }

            SetTransferButtonsEnabled(false, false);
            SetStatus("正在读取远程选中的文件", Orange);
            try
            {
                await SendRemoteShortcutAsync(0x2e); // Ctrl+C
                _ = MonitorDownloadStartTimeoutAsync(generation);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                FailPendingDownload("下载失败：" + SanitizeError(ex.Message), generation);
            }
        }

        private bool CanTransferFiles()
        {
            return !closing && !cancellation.IsCancellationRequested && remoteInputEnabled &&
                cliprdr != null && activeStage != null && framed != null;
        }

        private async Task<bool> TriggerRemoteFilePasteAsync(UploadTransferState transfer)
        {
            // A CLIPRDR FormatList is asynchronous. NativeAOT changes scheduling
            // enough that the old fixed 120 ms delay can send Ctrl+V before the
            // server has accepted the advertised file descriptors. Retry only
            // until the first FileContentsRequest proves that the paste started.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!ReferenceEquals(uploadTransfer, transfer) || transfer.Completed)
                    return transfer.PullStarted;

                await Task.Delay(attempt == 0 ? 350 : 500, cancellation.Token);
                if (transfer.PullStarted)
                    return true;

                await SendRemoteShortcutAsync(0x2f); // Ctrl+V
                Task wait = Task.Delay(900, cancellation.Token);
                Task completed = await Task.WhenAny(transfer.FirstRequest.Task, wait);
                if (completed == transfer.FirstRequest.Task)
                    return true;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return transfer.PullStarted;
        }

        private void FailUploadStart(UploadTransferState transfer)
        {
            if (!ReferenceEquals(uploadTransfer, transfer) || transfer.PullStarted)
                return;
            uploadTransfer = null;
            SetTransferButtonsEnabled(true, true);
            SetStatus("上传未开始：请在远程目标文件夹内重试", Red);
        }

        private async Task MonitorDownloadStartTimeoutAsync(int generation)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            FailPendingDownload("没有读取到远程文件，请先在远程资源管理器中选中文件", generation);
        }

        private void FailPendingDownload(string message, int generation)
        {
            bool shouldFail = false;
            lock (fileTransferSync)
            {
                if (generation == pendingDownloadGeneration && pendingDownloadDirectory != null)
                {
                    pendingDownloadDirectory = null;
                    shouldFail = true;
                }
            }
            if (!shouldFail)
                return;

            SetTransferButtonsEnabled(true, true);
            SetStatus(message, Red);
        }

        private async Task SendRemoteShortcutAsync(byte keyScanCode)
        {
            const byte controlScanCode = 0x1d;
            await SendKeyAsync(controlScanCode, false, true);
            try
            {
                await SendKeyAsync(keyScanCode, false, true);
                await SendKeyAsync(keyScanCode, false, false);
            }
            finally
            {
                await SendKeyAsync(controlScanCode, false, false);
            }
        }

        private async Task<VecU8> HandleUploadFileRequestAsync(FfiFileContentsRequest request)
        {
            if (activeStage == null || request == null)
                return null;

            uint streamId = request.StreamId();
            try
            {
                UploadTransferState transfer = uploadTransfer;
                int fileIndex = request.Index();
                if (transfer == null || fileIndex < 0 || fileIndex >= transfer.Files.Count)
                    return activeStage.SubmitClipboardFileError(streamId);

                if (transfer.MarkPullStarted())
                    SetStatus("正在上传：" + transfer.DisplayName, Orange);

                UploadFileItem file = transfer.Files[fileIndex];
                if (!file.IsDirectory && !File.Exists(file.Path))
                    return activeStage.SubmitClipboardFileError(streamId);

                if (request.IsSizeRequest())
                {
                    if (file.Length == 0)
                        MarkUploadFileComplete(transfer, fileIndex);
                    return activeStage.SubmitClipboardFileSize(streamId, file.Length);
                }

                if (file.IsDirectory || !request.IsRangeRequest())
                    return activeStage.SubmitClipboardFileError(streamId);

                ulong position = request.Position();
                uint requestedSize = request.RequestedSize();
                if (requestedSize == 0 || position > file.Length || position > long.MaxValue)
                {
                    return activeStage.SubmitClipboardFileError(streamId);
                }

                // cbRequested is the maximum response size, not an exact size.
                // Explorer commonly asks for a full block even when a small file
                // has fewer bytes remaining.
                ulong remaining = file.Length - position;
                uint responseSize = (uint)Math.Min(remaining,
                    Math.Min((ulong)requestedSize, MaxUploadChunkSize));
                byte[] data = new byte[checked((int)responseSize)];
                int totalRead = 0;
                using (FileStream input = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
                {
                    input.Position = (long)position;
                    while (totalRead < data.Length)
                    {
                        int read = await input.ReadAsync(data, totalRead, data.Length - totalRead, cancellation.Token);
                        if (read == 0)
                            break;
                        totalRead += read;
                    }
                }

                if (totalRead != data.Length)
                    return activeStage.SubmitClipboardFileError(streamId);

                ulong completed = position + (uint)totalRead;
                int percent = file.Length == 0 ? 100 : (int)Math.Min(100, completed * 100 / file.Length);
                SetStatus("上传 " + percent + "%：" + file.Name, Orange);
                if (completed >= file.Length)
                    MarkUploadFileComplete(transfer, fileIndex);

                return activeStage.SubmitClipboardFileData(streamId, data);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                SetStatus("上传失败：" + SanitizeError(ex.Message), Red);
                SetTransferButtonsEnabled(true, true);
                try { return activeStage?.SubmitClipboardFileError(streamId); }
                catch { return null; }
            }
        }

        private void MarkUploadFileComplete(UploadTransferState transfer, int fileIndex)
        {
            if (transfer.Completed || !transfer.CompletedIndexes.Add(fileIndex))
                return;

            if (transfer.CompletedIndexes.Count < transfer.Files.Count)
                return;

            transfer.Completed = true;
            SetTransferButtonsEnabled(true, true);
            SetStatus("上传完成：" + transfer.DisplayName, Green);
        }

        private VecU8 BeginDownloadFromRemoteList(FfiRemoteFileList remoteFiles)
        {
            string destination;
            string clipboardRoot = null;
            bool publishToClipboard;
            lock (fileTransferSync)
            {
                // Only one streamed download can own the response sequence at a
                // time. The transfer buttons are disabled while this is active.
                if (downloadTransfer != null)
                    return null;

                destination = pendingDownloadDirectory;
                publishToClipboard = destination == null;
                if (!publishToClipboard)
                    pendingDownloadDirectory = null;
            }

            try
            {
                if (publishToClipboard)
                {
                    clipboardRoot = CreateClipboardTempDirectory();
                    destination = clipboardRoot;
                }

                int count = checked((int)remoteFiles.Len());
                if (count == 0)
                    throw new InvalidOperationException("远程剪贴板没有文件");
                if (count > 10000)
                    throw new InvalidOperationException("一次下载的文件数量过多");

                List<RemoteDownloadItem> items = new List<RemoteDownloadItem>(count);
                HashSet<string> reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> clipboardPaths = publishToClipboard ? new List<string>() : null;
                HashSet<string> reservedClipboardPaths = publishToClipboard
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : null;
                for (int index = 0; index < count; index++)
                {
                    string name = SanitizeLocalPathPart(remoteFiles.GetName((nuint)index));
                    string relativePath = SanitizeRelativePath(remoteFiles.GetRelativePath((nuint)index));
                    bool isDirectory = remoteFiles.IsDirectory((nuint)index);
                    string targetPath = BuildDownloadPath(destination, relativePath, name);
                    if (!isDirectory)
                        targetPath = GetAvailableFilePath(targetPath, reservedPaths);
                    reservedPaths.Add(targetPath);

                    items.Add(new RemoteDownloadItem(index, name, targetPath, isDirectory));
                    if (publishToClipboard)
                    {
                        string topLevelPath = GetClipboardTopLevelPath(destination, relativePath, targetPath);
                        if (reservedClipboardPaths.Add(topLevelPath))
                            clipboardPaths.Add(topLevelPath);
                    }
                }

                bool hasDataId = remoteFiles.HasDataId();
                uint dataId = hasDataId ? remoteFiles.DataId() : 0;
                downloadTransfer = new DownloadTransferState(items, hasDataId, dataId,
                    publishToClipboard, clipboardRoot, clipboardPaths);
                SetTransferButtonsEnabled(false, false);
                SetStatus(publishToClipboard
                    ? "正在接收远程剪贴板文件：共 " + items.Count + " 项"
                    : "正在下载：共 " + items.Count + " 项", Orange);
                return StartNextDownloadFile();
            }
            catch (Exception ex)
            {
                if (clipboardRoot != null)
                    TryDeleteClipboardTempDirectory(clipboardRoot);
                FailDownload((publishToClipboard ? "文件复制失败：" : "下载失败：") +
                    SanitizeError(ex.Message));
                return null;
            }
        }

        private VecU8 StartNextDownloadFile()
        {
            DownloadTransferState transfer = downloadTransfer;
            if (transfer == null || activeStage == null)
                return null;

            while (transfer.NextItemIndex < transfer.Items.Count)
            {
                RemoteDownloadItem item = transfer.Items[transfer.NextItemIndex++];
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(item.TargetPath);
                    continue;
                }

                string parent = Path.GetDirectoryName(item.TargetPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                transfer.CurrentItem = item;
                transfer.CurrentPosition = 0;
                transfer.ExpectedSize = 0;
                transfer.WaitingForSize = true;
                transfer.PendingStreamId = AllocateFileStreamId();
                return activeStage.RequestClipboardFileSize(transfer.PendingStreamId, item.RemoteIndex,
                    transfer.HasDataId, transfer.DataId);
            }

            CompleteDownload(transfer);
            return null;
        }

        private async Task<VecU8> HandleDownloadFileResponseAsync(FfiFileContentsResponse response)
        {
            DownloadTransferState transfer = downloadTransfer;
            if (transfer == null || activeStage == null || response == null ||
                response.StreamId() != transfer.PendingStreamId)
            {
                return null;
            }

            if (response.IsError())
            {
                FailDownload((transfer.PublishToClipboard ? "文件复制失败：" : "下载失败：") +
                    "远程服务器拒绝了文件读取请求");
                return null;
            }

            try
            {
                byte[] data;
                using (VecU8 value = response.Data())
                {
                    nuint size = value.GetSize();
                    if (size > int.MaxValue)
                        throw new InvalidDataException("远程返回的文件块过大");
                    data = new byte[(int)size];
                    value.Fill(data);
                }

                if (transfer.WaitingForSize)
                {
                    if (data.Length != sizeof(ulong))
                        throw new InvalidDataException("远程文件大小响应无效");

                    transfer.ExpectedSize = BitConverter.ToUInt64(data, 0);
                    transfer.WaitingForSize = false;
                    transfer.Output = new FileStream(transfer.CurrentItem.TargetPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    if (transfer.ExpectedSize == 0)
                    {
                        transfer.Output.Dispose();
                        transfer.Output = null;
                        transfer.CompletedFiles++;
                        return StartNextDownloadFile();
                    }

                    return RequestNextDownloadChunk(transfer);
                }

                if (data.Length == 0 || (uint)data.Length > transfer.PendingRequestedSize ||
                    (ulong)data.Length > transfer.ExpectedSize - transfer.CurrentPosition)
                {
                    throw new InvalidDataException("远程返回的文件数据长度无效");
                }

                await transfer.Output.WriteAsync(data, 0, data.Length, cancellation.Token);
                transfer.CurrentPosition += (uint)data.Length;
                int percent = (int)Math.Min(100, transfer.CurrentPosition * 100 / transfer.ExpectedSize);
                SetStatus((transfer.PublishToClipboard ? "复制文件 " : "下载 ") + percent + "%：" +
                    transfer.CurrentItem.Name, Orange);

                if (transfer.CurrentPosition >= transfer.ExpectedSize)
                {
                    await transfer.Output.FlushAsync(cancellation.Token);
                    transfer.Output.Dispose();
                    transfer.Output = null;
                    transfer.CompletedFiles++;
                    return StartNextDownloadFile();
                }

                return RequestNextDownloadChunk(transfer);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                FailDownload((transfer.PublishToClipboard ? "文件复制失败：" : "下载失败：") +
                    SanitizeError(ex.Message));
                return null;
            }
        }

        private VecU8 RequestNextDownloadChunk(DownloadTransferState transfer)
        {
            ulong remaining = transfer.ExpectedSize - transfer.CurrentPosition;
            transfer.PendingRequestedSize = (uint)Math.Min((ulong)DownloadChunkSize, remaining);
            transfer.PendingStreamId = AllocateFileStreamId();
            return activeStage.RequestClipboardFileRange(transfer.PendingStreamId,
                transfer.CurrentItem.RemoteIndex, transfer.CurrentPosition, transfer.PendingRequestedSize,
                transfer.HasDataId, transfer.DataId);
        }

        private uint AllocateFileStreamId()
        {
            uint id = nextFileStreamId++;
            if (id == 0)
                id = nextFileStreamId++;
            return id;
        }

        private void CompleteDownload(DownloadTransferState transfer)
        {
            if (!transfer.PublishToClipboard)
            {
                downloadTransfer = null;
                SetTransferButtonsEnabled(true, true);
                SetStatus("下载完成：" + transfer.CompletedFiles + " 个文件", Green);
                return;
            }

            SetStatus("正在写入本机文件剪贴板", Orange);
            clipboardPublishTask = PublishDownloadedFilesToClipboardAsync(transfer);
        }

        private async Task PublishDownloadedFilesToClipboardAsync(DownloadTransferState transfer)
        {
            string previousRoot = null;
            bool clipboardRootRegistered = false;
            bool published = false;
            try
            {
                StringCollection paths = new StringCollection();
                foreach (string path in transfer.ClipboardPaths)
                {
                    if (File.Exists(path) || Directory.Exists(path))
                        paths.Add(path);
                }
                if (paths.Count == 0)
                    throw new InvalidOperationException("没有可写入剪贴板的远程文件");

                // Register the in-progress root before SetFileDropList. Windows may
                // notify the native listener before the async continuation resumes.
                lock (fileTransferSync)
                {
                    previousRoot = publishedRemoteClipboardRoot;
                    publishingRemoteClipboardRoot = transfer.ClipboardRoot;
                    clipboardRootRegistered = true;
                }

                await SetClipboardFileDropListWithRetryAsync(paths);
                published = true;
                transfer.ClipboardPublished = true;
                lock (fileTransferSync)
                {
                    publishedRemoteClipboardRoot = transfer.ClipboardRoot;
                    if (string.Equals(publishingRemoteClipboardRoot, transfer.ClipboardRoot,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        publishingRemoteClipboardRoot = null;
                    }
                }

                if (!string.IsNullOrEmpty(previousRoot) &&
                    !string.Equals(previousRoot, transfer.ClipboardRoot, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteClipboardTempDirectory(previousRoot);
                }

                SetStatus("远程文件已复制到本机剪贴板（" + transfer.CompletedFiles + " 个文件）", Green);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!closing)
                    SetStatus("文件复制失败：" + SanitizeError(ex.Message), Red);
            }
            finally
            {
                if (!published)
                {
                    if (clipboardRootRegistered)
                    {
                        lock (fileTransferSync)
                        {
                            if (string.Equals(publishingRemoteClipboardRoot, transfer.ClipboardRoot,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                publishingRemoteClipboardRoot = null;
                            }
                        }
                    }
                    TryDeleteClipboardTempDirectory(transfer.ClipboardRoot);
                }

                if (ReferenceEquals(downloadTransfer, transfer))
                    downloadTransfer = null;
                SetTransferButtonsEnabled(true, true);
            }
        }

        private async Task SetClipboardFileDropListWithRetryAsync(StringCollection paths)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    await SetClipboardFileDropListOnUiThreadAsync(paths);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (attempt < 4)
                    await Task.Delay(60 * (attempt + 1), cancellation.Token);
            }

            throw new InvalidOperationException("Windows 剪贴板正被其他程序占用", lastError);
        }

        private Task SetClipboardFileDropListOnUiThreadAsync(StringCollection paths)
        {
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            System.Action publish = () =>
            {
                try
                {
                    SetClipboardFileDropListNative(paths);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            };

            try
            {
                if (InvokeRequired)
                    BeginInvoke(publish);
                else
                    publish();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            return completion.Task;
        }

        private void SetClipboardFileDropListNative(StringCollection paths)
        {
            if (paths == null || paths.Count == 0)
                throw new ArgumentException("文件剪贴板不能为空", nameof(paths));

            List<string> values = new List<string>(paths.Count);
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
                    continue;
                values.Add(Path.GetFullPath(path));
            }
            if (values.Count == 0)
                throw new InvalidOperationException("没有可写入剪贴板的文件路径");

            byte[] pathBytes = Encoding.Unicode.GetBytes(string.Join("\0", values) + "\0\0");
            byte[] payload = new byte[checked(DropFilesHeaderSize + pathBytes.Length)];
            Buffer.BlockCopy(BitConverter.GetBytes(DropFilesHeaderSize), 0, payload, 0, sizeof(int));
            Buffer.BlockCopy(BitConverter.GetBytes(1), 0, payload, 16, sizeof(int)); // DROPFILES.fWide
            Buffer.BlockCopy(pathBytes, 0, payload, DropFilesHeaderSize, pathBytes.Length);

            IntPtr memory = GlobalAlloc(GlobalMemoryMoveable | GlobalMemoryZeroInit,
                checked((UIntPtr)(uint)payload.Length));
            if (memory == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            bool clipboardOpen = false;
            try
            {
                IntPtr target = GlobalLock(memory);
                if (target == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Marshal.Copy(payload, 0, target, payload.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (!OpenClipboard(IsHandleCreated ? Handle : IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                clipboardOpen = true;

                if (!EmptyClipboard())
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (SetClipboardData(ClipboardFormatFileDrop, memory) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // Ownership transfers to Windows after SetClipboardData succeeds.
                memory = IntPtr.Zero;
            }
            finally
            {
                if (clipboardOpen)
                    CloseClipboard();
                if (memory != IntPtr.Zero)
                    GlobalFree(memory);
                Array.Clear(payload, 0, payload.Length);
                Array.Clear(pathBytes, 0, pathBytes.Length);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr dropHandle, uint fileIndex,
            StringBuilder fileName, uint characterCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr memory);

        private void FailDownload(string message)
        {
            DownloadTransferState transfer = downloadTransfer;
            downloadTransfer = null;
            if (transfer != null)
            {
                try { transfer.Output?.Dispose(); } catch { }
                if (transfer.Output != null && transfer.CurrentItem != null)
                {
                    try { File.Delete(transfer.CurrentItem.TargetPath); } catch { }
                }
                if (transfer.PublishToClipboard && !transfer.ClipboardPublished)
                    TryDeleteClipboardTempDirectory(transfer.ClipboardRoot);
            }
            lock (fileTransferSync)
            {
                pendingDownloadDirectory = null;
                pendingDownloadGeneration++;
            }
            SetTransferButtonsEnabled(true, true);
            SetStatus(message, Red);
        }

        private void CancelFileTransfers()
        {
            uploadTransfer = null;
            DownloadTransferState transfer = downloadTransfer;
            downloadTransfer = null;
            if (transfer != null)
            {
                try { transfer.Output?.Dispose(); } catch { }
                if (transfer.Output != null && transfer.CurrentItem != null)
                {
                    try { File.Delete(transfer.CurrentItem.TargetPath); } catch { }
                }
                if (transfer.PublishToClipboard && !transfer.ClipboardPublished)
                    TryDeleteClipboardTempDirectory(transfer.ClipboardRoot);
            }
            lock (fileTransferSync)
            {
                pendingDownloadDirectory = null;
                pendingDownloadGeneration++;
            }
        }

        private async Task WriteClipboardFrameAsync(VecU8 frame)
        {
            if (frame == null || framed == null)
                return;
            nuint size = frame.GetSize();
            if (size == 0)
                return;
            if (size > int.MaxValue)
                throw new InvalidDataException("RDP 文件通道响应过大");
            byte[] bytes = new byte[(int)size];
            frame.Fill(bytes);
            await framed.GetInner().Item1.WriteAsync(bytes, cancellation.Token);
        }

        private void SetTransferButtonsEnabled(bool uploadEnabled, bool downloadEnabled)
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new System.Action(() => SetTransferButtonsEnabled(uploadEnabled, downloadEnabled)));
                }
                catch (InvalidOperationException)
                {
                }
                return;
            }
            uploadButton.Enabled = uploadEnabled && !closing;
            downloadButton.Enabled = downloadEnabled && !closing;
        }

        private static string SanitizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return string.Empty;

            string[] parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> safeParts = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                if (part == "." || part == "..")
                    continue;
                safeParts.Add(SanitizeLocalPathPart(part));
            }
            return safeParts.Count == 0 ? string.Empty : Path.Combine(safeParts.ToArray());
        }

        private static string SanitizeLocalPathPart(string value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "unnamed_file" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            name = name.TrimEnd(' ', '.');
            if (name.Length == 0)
                name = "unnamed_file";

            string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
            {
                name = "_" + name;
            }
            return name;
        }

        private static string BuildDownloadPath(string root, string relativePath, string name)
        {
            string rootPath = Path.GetFullPath(root);
            string candidate = string.IsNullOrEmpty(relativePath)
                ? Path.Combine(rootPath, name)
                : Path.Combine(rootPath, relativePath, name);
            string fullPath = Path.GetFullPath(candidate);
            string rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("远程文件路径无效");
            return fullPath;
        }

        private bool ArePathsFromPublishedRemoteClipboard(IEnumerable<string> paths)
        {
            if (paths == null)
                return false;

            string publishedRoot;
            string publishingRoot;
            lock (fileTransferSync)
            {
                publishedRoot = publishedRemoteClipboardRoot;
                publishingRoot = publishingRemoteClipboardRoot;
            }
            if (string.IsNullOrEmpty(publishedRoot) && string.IsNullOrEmpty(publishingRoot))
                return false;

            bool foundPath = false;
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                foundPath = true;
                if (!IsPathInsideRoot(path, publishedRoot) &&
                    !IsPathInsideRoot(path, publishingRoot))
                {
                    return false;
                }
            }
            return foundPath;
        }

        private static string GetClipboardTopLevelPath(string root, string relativePath, string targetPath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return targetPath;

            string[] parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? targetPath : BuildDownloadPath(root, string.Empty, parts[0]);
        }

        private static string CreateClipboardTempDirectory()
        {
            string baseRoot = GetClipboardTempBaseRoot();
            Directory.CreateDirectory(baseRoot);
            string root = Path.GetFullPath(Path.Combine(baseRoot, Guid.NewGuid().ToString("N")));
            if (!IsPathInsideRoot(root, baseRoot) ||
                string.Equals(root, baseRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("剪贴板临时目录无效");
            }
            Directory.CreateDirectory(root);
            return root;
        }

        private static string GetClipboardTempBaseRoot()
        {
            return Path.GetFullPath(Path.Combine(
                Path.GetTempPath(), ProtectedText.RdpClientName, ProtectedText.ClipboardTempName));
        }

        private static bool IsPathInsideRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
                return false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullRoot = Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
                    return true;
                string prefix = fullRoot + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryDeleteClipboardTempDirectory(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;
            try
            {
                string fullRoot = Path.GetFullPath(root);
                string baseRoot = GetClipboardTempBaseRoot();
                DirectoryInfo parent = Directory.GetParent(fullRoot);
                string directoryName = Path.GetFileName(fullRoot);
                if (parent == null ||
                    !string.Equals(parent.FullName, baseRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(directoryName, "N", out _))
                {
                    return;
                }
                if (Directory.Exists(fullRoot))
                    Directory.Delete(fullRoot, true);
            }
            catch (Exception)
            {
                // A shell process may still have a short-lived handle. The
                // directory remains in the system temp area rather than risking
                // deletion while an Explorer copy operation is still reading it.
            }
        }

        private static string GetAvailableFilePath(string path, HashSet<string> reservedPaths)
        {
            if (!File.Exists(path) && !reservedPaths.Contains(path))
                return path;

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            for (int number = 1; number < 10000; number++)
            {
                string candidate = Path.Combine(directory, fileName + " (" + number + ")" + extension);
                if (!File.Exists(candidate) && !reservedPaths.Contains(candidate))
                    return candidate;
            }
            throw new IOException("无法为同名文件生成保存路径");
        }

        private sealed class UploadTransferState
        {
            public List<UploadFileItem> Files { get; }
            public HashSet<int> CompletedIndexes { get; } = new HashSet<int>();
            public TaskCompletionSource<bool> FirstRequest { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Completed { get; set; }
            public bool PullStarted => FirstRequest.Task.IsCompleted;
            public string DisplayName
            {
                get
                {
                    return Files.Count == 1
                        ? Files[0].Name
                        : Files.Count + " 个文件";
                }
            }

            public UploadTransferState(List<UploadFileItem> files)
            {
                Files = files;
            }

            public bool MarkPullStarted()
            {
                return FirstRequest.TrySetResult(true);
            }
        }

        private sealed class UploadFileItem
        {
            public string Path { get; }
            public string Name { get; }
            public ulong Length { get; }
            public bool IsDirectory { get; }

            public UploadFileItem(string path, string name, ulong length, bool isDirectory)
            {
                Path = path;
                Name = name;
                Length = length;
                IsDirectory = isDirectory;
            }
        }

        private sealed class RemoteDownloadItem
        {
            public int RemoteIndex { get; }
            public string Name { get; }
            public string TargetPath { get; }
            public bool IsDirectory { get; }

            public RemoteDownloadItem(int remoteIndex, string name, string targetPath, bool isDirectory)
            {
                RemoteIndex = remoteIndex;
                Name = name;
                TargetPath = targetPath;
                IsDirectory = isDirectory;
            }
        }

        private sealed class DownloadTransferState
        {
            public List<RemoteDownloadItem> Items { get; }
            public bool HasDataId { get; }
            public uint DataId { get; }
            public bool PublishToClipboard { get; }
            public string ClipboardRoot { get; }
            public List<string> ClipboardPaths { get; }
            public bool ClipboardPublished { get; set; }
            public int NextItemIndex { get; set; }
            public int CompletedFiles { get; set; }
            public RemoteDownloadItem CurrentItem { get; set; }
            public FileStream Output { get; set; }
            public ulong ExpectedSize { get; set; }
            public ulong CurrentPosition { get; set; }
            public uint PendingStreamId { get; set; }
            public uint PendingRequestedSize { get; set; }
            public bool WaitingForSize { get; set; }

            public DownloadTransferState(List<RemoteDownloadItem> items, bool hasDataId, uint dataId,
                bool publishToClipboard, string clipboardRoot, List<string> clipboardPaths)
            {
                Items = items;
                HasDataId = hasDataId;
                DataId = dataId;
                PublishToClipboard = publishToClipboard;
                ClipboardRoot = clipboardRoot;
                ClipboardPaths = clipboardPaths ?? new List<string>();
            }
        }
    }
}
