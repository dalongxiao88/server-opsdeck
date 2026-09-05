using System;
using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public class PasswordForm : Form
    {
        public string Password { get; private set; }
        private TextBox txtPassword;
        private string promptText;

        public PasswordForm(string prompt = "请输入密码：")
        {
            this.promptText = prompt;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "密码验证";
            this.Size = new Size(350, 150);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            Label label = new Label
            {
                Text = promptText,
                Location = new Point(20, 25),
                Size = new Size(100, 20),
                Font = new Font("Microsoft YaHei", 9F)
            };

            txtPassword = new TextBox
            {
                Location = new Point(120, 22),
                Size = new Size(190, 25),
                PasswordChar = '*',
                Font = new Font("Microsoft YaHei", 9F)
            };
            txtPassword.KeyPress += (s, e) =>
            {
                if (e.KeyChar == (char)Keys.Enter)
                {
                    BtnOK_Click(null, null);
                }
            };

            Button btnOK = new Button
            {
                Text = "确定",
                Location = new Point(120, 65),
                Size = new Size(90, 30),
                BackColor = Color.FromArgb(39, 174, 96),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnOK.Click += BtnOK_Click;

            Button btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(220, 65),
                Size = new Size(90, 30),
                BackColor = Color.FromArgb(149, 165, 166),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.Click += (s, e) => this.DialogResult = DialogResult.Cancel;

            this.Controls.Add(label);
            this.Controls.Add(txtPassword);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            Password = txtPassword.Text;
            this.DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            txtPassword.Focus();
        }
    }
}