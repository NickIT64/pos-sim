using System;
using System.Drawing;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using POS.Shared;

namespace POS.Management
{
    public partial class ManagementForm : Form
    {
        private TcpClient _client;
        private bool _connected = false;

        public ManagementForm()
        {
            InitializeComponent();
            PosLogger.Source = "MANAGEMENT";
            PosLogger.Info("POS Management Console starting up...");
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "POS Management Console";
            this.Size = new Size(600, 500);
            this.BackColor = Color.FromArgb(20, 20, 20);
            this.StartPosition = FormStartPosition.CenterScreen;

            // Title
            var title = new Label();
            title.Text = "POS MANAGEMENT CONSOLE";
            title.Font = new Font("Courier New", 14, FontStyle.Bold);
            title.ForeColor = Color.Cyan;
            title.Size = new Size(560, 40);
            title.Location = new Point(20, 15);
            title.TextAlign = ContentAlignment.MiddleCenter;
            this.Controls.Add(title);

            // Status label
            var lblStatus = new Label();
            lblStatus.Name = "lblStatus";
            lblStatus.Text = "● DISCONNECTED";
            lblStatus.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            lblStatus.ForeColor = Color.Red;
            lblStatus.Size = new Size(560, 25);
            lblStatus.Location = new Point(20, 55);
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            this.Controls.Add(lblStatus);

            // Connect button
            var btnConnect = new Button();
            btnConnect.Text = "CONNECT TO ENGINE";
            btnConnect.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            btnConnect.ForeColor = Color.White;
            btnConnect.BackColor = Color.FromArgb(30, 100, 180);
            btnConnect.FlatStyle = FlatStyle.Flat;
            btnConnect.Size = new Size(200, 40);
            btnConnect.Location = new Point(190, 88);
            btnConnect.Click += BtnConnect_Click;
            this.Controls.Add(btnConnect);

            // Log box
            var log = new RichTextBox();
            log.Name = "rtbLog";
            log.Font = new Font("Courier New", 9);
            log.ForeColor = Color.LimeGreen;
            log.BackColor = Color.Black;
            log.Size = new Size(560, 180);
            log.Location = new Point(20, 140);
            log.ReadOnly = true;
            this.Controls.Add(log);

            // Command buttons
            AddCmdButton("PING", Color.FromArgb(30, 100, 180), new Point(20, 340), "PING");
            AddCmdButton("STATUS", Color.FromArgb(30, 100, 180), new Point(130, 340), "STATUS");
            AddCmdButton("REBOOT", Color.FromArgb(180, 120, 0), new Point(240, 340), "REBOOT");
            AddCmdButton("RESTART", Color.FromArgb(180, 120, 0), new Point(350, 340), "RESTART");
            AddCmdButton("SHUTDOWN", Color.FromArgb(180, 40, 40), new Point(460, 340), "SHUTDOWN");

            // Custom command
            var txtCmd = new TextBox();
            txtCmd.Name = "txtCmd";
            txtCmd.Font = new Font("Courier New", 11);
            txtCmd.ForeColor = Color.White;
            txtCmd.BackColor = Color.FromArgb(40, 40, 40);
            txtCmd.Size = new Size(440, 35);
            txtCmd.Location = new Point(20, 410);
            txtCmd.PlaceholderText = "Custom command...";
            this.Controls.Add(txtCmd);

            var btnSend = new Button();
            btnSend.Text = "SEND";
            btnSend.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            btnSend.ForeColor = Color.White;
            btnSend.BackColor = Color.FromArgb(60, 140, 60);
            btnSend.FlatStyle = FlatStyle.Flat;
            btnSend.Size = new Size(100, 35);
            btnSend.Location = new Point(470, 410);
            btnSend.Click += (s, e) => SendCommand(txtCmd.Text);
            this.Controls.Add(btnSend);
        }

        private void AddCmdButton(string label, Color color, Point location, string cmd)
        {
            var btn = new Button();
            btn.Text = label;
            btn.Font = new Font("Segoe UI", 8, FontStyle.Bold);
            btn.ForeColor = Color.White;
            btn.BackColor = color;
            btn.FlatStyle = FlatStyle.Flat;
            btn.Size = new Size(100, 40);
            btn.Location = location;
            btn.Click += (s, e) => SendCommand(cmd);
            this.Controls.Add(btn);
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                _client = new TcpClient("127.0.0.1", 9000);
                _connected = true;

                var lblStatus = (Label)this.Controls["lblStatus"];
                lblStatus.Text = "● CONNECTED TO ENGINE";
                lblStatus.ForeColor = Color.LimeGreen;

                // Disable connect button once connected
                var btn = (Button)sender;
                btn.Enabled = false;
                btn.BackColor = Color.FromArgb(50, 50, 50);

                Log("Connected to POS Engine on port 9000");
            }
            catch (Exception ex)
            {
                Log($"Connection failed: {ex.Message}");
            }
        }

        private void SendCommand(string cmd)
        {
            if (!_connected) { Log("Not connected to engine!"); return; }
            if (string.IsNullOrWhiteSpace(cmd)) return;

            if (cmd.ToUpper() == "SHUTDOWN")
            {
                var confirm = MessageBox.Show(
                    "Are you sure you want to shut down the POS Engine?",
                    "Confirm Shutdown",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
            }

            try
            {
                var stream = _client.GetStream();
                var data = Encoding.UTF8.GetBytes(cmd.Trim());
                stream.Write(data, 0, data.Length);
                Log($">> SENT: {cmd.ToUpper()}");
                PosLogger.Info($"Command sent to engine: {cmd.ToUpper()}");

                var buffer = new byte[1024];
                int bytes = stream.Read(buffer, 0, buffer.Length);
                string response = Encoding.UTF8.GetString(buffer, 0, bytes);
                Log($"<< RECV: {response}");

                if (cmd.ToUpper() == "SHUTDOWN")
                {
                    _connected = false;
                    var lblStatus = (Label)this.Controls["lblStatus"];
                    lblStatus.Text = "● DISCONNECTED";
                    lblStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                _connected = false;
            }
        }


        private void Log(string message)
        {
            var log = (RichTextBox)this.Controls["rtbLog"];
            log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            log.ScrollToCaret();
        }
    }
}