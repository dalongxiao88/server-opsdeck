using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPManager
{
    public enum OperationStepState
    {
        Pending,
        Running,
        Completed,
        Failed,
        Skipped
    }

    public sealed class OperationProgressForm : Form
    {
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(105, 115, 125);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Blue = Color.FromArgb(42, 125, 185);
        private static readonly Color Orange = Color.FromArgb(210, 125, 26);
        private static readonly Color Red = Color.FromArgb(184, 62, 62);

        private readonly List<string> stepNames;
        private readonly List<Label> stepLabels = new List<Label>();
        private readonly Label titleLabel;
        private readonly Label targetLabel;
        private readonly Label detailLabel;
        private readonly Label stateDot;
        private readonly ProgressBar progressBar;
        private readonly Button confirmButton;
        private readonly Button cancelButton;
        private bool running;
        private bool finished;
        private CancellationTokenSource cancellation;

        public Func<OperationProgressForm, CancellationToken, Task> Operation { get; set; }
        public bool Succeeded { get; private set; }
        public string FailureMessage { get; private set; }

        public OperationProgressForm(string title, string target, IEnumerable<string> steps)
        {
            stepNames = new List<string>(steps ?? new string[0]);
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            int bottomY = 151 + stepNames.Count * 25 + 12;
            ClientSize = new Size(560, bottomY + 45);
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            Text = title;

            titleLabel = new Label
            {
                AutoSize = true,
                Text = title,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(24, 18)
            };
            targetLabel = new Label
            {
                AutoEllipsis = true,
                Text = target,
                Size = new Size(500, 24),
                ForeColor = MutedColor,
                Location = new Point(26, 52)
            };
            stateDot = new Label
            {
                AutoSize = true,
                Text = "●",
                Font = new Font("Segoe UI Symbol", 15F, FontStyle.Bold),
                ForeColor = MutedColor,
                Location = new Point(24, 86)
            };
            detailLabel = new Label
            {
                AutoEllipsis = true,
                Text = "请确认后开始执行",
                Size = new Size(490, 24),
                ForeColor = MutedColor,
                Location = new Point(50, 89)
            };
            progressBar = new ProgressBar
            {
                Location = new Point(26, 120),
                Size = new Size(508, 12),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };

            int y = 151;
            foreach (string step in stepNames)
            {
                Label label = new Label
                {
                    AutoEllipsis = true,
                    Text = "○  " + step,
                    Size = new Size(500, 25),
                    Location = new Point(28, y),
                    ForeColor = MutedColor
                };
                stepLabels.Add(label);
                Controls.Add(label);
                y += 25;
            }

            confirmButton = CreateButton(title.IndexOf("重启", StringComparison.OrdinalIgnoreCase) >= 0 ? "确认重启" : "确认执行", Green, 100);
            confirmButton.Location = new Point(318, bottomY);
            confirmButton.Click += async (sender, args) => await StartOperationAsync();
            cancelButton = CreateButton("取消", MutedColor, 80);
            cancelButton.Location = new Point(436, bottomY);
            cancelButton.Click += (sender, args) =>
            {
                if (running)
                    return;
                Close();
            };

            Controls.Add(titleLabel);
            Controls.Add(targetLabel);
            Controls.Add(stateDot);
            Controls.Add(detailLabel);
            Controls.Add(progressBar);
            Controls.Add(confirmButton);
            Controls.Add(cancelButton);
        }

        public void SetStep(int index, OperationStepState state, string detail = null)
        {
            if (index < 0 || index >= stepLabels.Count)
                return;

            string prefix;
            Color color;
            switch (state)
            {
                case OperationStepState.Running:
                    prefix = "●";
                    color = Blue;
                    break;
                case OperationStepState.Completed:
                    prefix = "✓";
                    color = Green;
                    break;
                case OperationStepState.Failed:
                    prefix = "×";
                    color = Red;
                    break;
                case OperationStepState.Skipped:
                    prefix = "—";
                    color = MutedColor;
                    break;
                default:
                    prefix = "○";
                    color = MutedColor;
                    break;
            }

            stepLabels[index].Text = prefix + "  " + stepNames[index] + (string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail);
            stepLabels[index].ForeColor = color;
        }

        public void SetProgress(string title, string detail, int progress, Color color, bool indeterminate = false)
        {
            titleLabel.Text = title;
            detailLabel.Text = detail;
            stateDot.ForeColor = color;
            progressBar.Style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            progressBar.MarqueeAnimationSpeed = indeterminate ? 24 : 0;
            progressBar.Visible = true;
            if (!indeterminate)
                progressBar.Value = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, progress));
        }

        public void MarkSuccess(string detail)
        {
            running = false;
            finished = true;
            Succeeded = true;
            FailureMessage = null;
            titleLabel.Text = "操作完成";
            detailLabel.Text = detail;
            stateDot.ForeColor = Green;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.MarqueeAnimationSpeed = 0;
            progressBar.Value = 100;
            confirmButton.Visible = false;
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;
            cancelButton.ForeColor = Green;
            cancelButton.FlatAppearance.BorderColor = Green;
        }

        public void MarkFailure(string detail)
        {
            running = false;
            finished = true;
            Succeeded = false;
            FailureMessage = detail;
            titleLabel.Text = "操作失败";
            detailLabel.Text = detail;
            stateDot.ForeColor = Red;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.MarqueeAnimationSpeed = 0;
            confirmButton.Visible = false;
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;
            cancelButton.ForeColor = Red;
            cancelButton.FlatAppearance.BorderColor = Red;
        }

        private async Task StartOperationAsync()
        {
            if (running || finished)
                return;
            if (Operation == null)
            {
                MarkFailure("没有配置可执行的远程操作");
                return;
            }

            running = true;
            confirmButton.Enabled = false;
            cancelButton.Text = "执行中";
            cancelButton.Enabled = false;
            progressBar.Visible = true;
            SetProgress("正在准备", "开始执行远程操作...", 0, Blue, true);
            cancellation = new CancellationTokenSource();
            try
            {
                await Operation(this, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                MarkFailure("操作已取消");
            }
            catch (Exception ex)
            {
                MarkFailure(SanitizeError(ex.Message));
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
            }
        }

        private static Button CreateButton(string text, Color color, int width)
        {
            Button button = new Button
            {
                Text = text,
                Size = new Size(width, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = color,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = color;
            return button;
        }

        private static string SanitizeError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "未知错误";
            return RemoteErrorFormatter.Format(message, "");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                e.Cancel = true;
                return;
            }
            cancellation?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
