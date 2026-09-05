using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MongoLocalTargetForm : Form
    {
        private TextBox hostBox;
        private NumericUpDown portBox;
        private TextBox usernameBox;
        private TextBox passwordBox;
        public MongoLocalTarget Target { get; private set; }

        public MongoLocalTargetForm()
        {
            Text = "本机 MongoDB 目标";
            ClientSize = new Size(500, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White; Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(new Label { AutoSize = true, Text = "配置本机 MongoDB 目标", Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(35,42,49), Location = new Point(24,20) });
            Controls.Add(new Label { AutoEllipsis = true, Size = new Size(440, 30), Text = "本机密码只在本次恢复过程中使用，不会自动保存。", ForeColor = Color.FromArgb(104,114,124), Location = new Point(26,55) });
            AddLabel("地址",26,94); hostBox=Box("127.0.0.1",26,116,260); Controls.Add(hostBox);
            AddLabel("端口",314,94); portBox=new NumericUpDown { Location=new Point(314,116), Width=130, Minimum=1, Maximum=65535, Value=27017 }; Controls.Add(portBox);
            AddLabel("用户名",26,154); usernameBox=Box("",26,176,418); Controls.Add(usernameBox);
            AddLabel("密码",26,214); passwordBox=Box("",26,236,418); passwordBox.UseSystemPasswordChar=true; Controls.Add(passwordBox);
            Button cancel=Button("取消",Color.FromArgb(104,114,124),278,288,82); cancel.Click+=(s,e)=>DialogResult=DialogResult.Cancel; Controls.Add(cancel);
            Button test=Button("测试并开始",Color.FromArgb(26,134,87),368,288,108); test.Click+=async(s,e)=>await TestAsync(); Controls.Add(test); AcceptButton=test; CancelButton=cancel;
        }
        private async Task TestAsync()
        {
            Target=new MongoLocalTarget { Host=hostBox.Text.Trim(), Port=(int)portBox.Value, Username=usernameBox.Text.Trim(), Password=passwordBox.Text, AuthenticationDatabase="admin", RestoreToolPath=MongoBackupService.FindRestoreTool() };
            if(string.IsNullOrWhiteSpace(Target.RestoreToolPath)){MessageBox.Show("未找到本机 mongorestore.exe。","缺少本机客户端",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
            if(string.IsNullOrWhiteSpace(Target.Username)||string.IsNullOrEmpty(Target.Password)){MessageBox.Show("本机 MongoDB 账号和密码不能为空。","信息不完整",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            Cursor old=Cursor;Cursor=Cursors.WaitCursor;try{await Task.Run(()=>{using(System.Net.Sockets.TcpClient c=new System.Net.Sockets.TcpClient()){Task t=c.ConnectAsync(Target.Host,Target.Port);if(!t.Wait(1200)||!c.Connected)throw new InvalidOperationException("本机 MongoDB 端口未监听");}},CancellationToken.None);DialogResult=DialogResult.OK;}catch(Exception ex){MessageBox.Show("本机 MongoDB 连接失败："+ex.Message,"连接失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{Cursor=old;}
        }
        private void AddLabel(string t,int x,int y)=>Controls.Add(new Label{AutoSize=true,Text=t,ForeColor=Color.FromArgb(104,114,124),Location=new Point(x,y)});
        private static TextBox Box(string t,int x,int y,int w)=>new TextBox{Text=t,Location=new Point(x,y),Width=w,Height=27,BorderStyle=BorderStyle.FixedSingle};
        private static Button Button(string t,Color c,int x,int y,int w){Button b=new Button{Text=t,Width=w,Height=34,Location=new Point(x,y),FlatStyle=FlatStyle.Flat,BackColor=Color.White,ForeColor=c,UseVisualStyleBackColor=false,Cursor=Cursors.Hand};b.FlatAppearance.BorderColor=c;return b;}
    }
}
