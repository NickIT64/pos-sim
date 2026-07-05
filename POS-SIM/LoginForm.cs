using System;
using System.Drawing;
using System.Windows.Forms;
using POS.Shared;

namespace POS_SIM
{
    public class LoginForm : Form
    {
        private readonly IPosDatabase _db;
        private string _enteredPin = "";
        public int CashierId { get; private set; } = -1;
        public string CashierName { get; private set; } = "";

        public LoginForm(IPosDatabase db)
        {
            _db = db;
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Cashier Login";
            this.Size = new Size(400, 550);
            this.BackColor = Color.FromArgb(20, 20, 20);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.StartPosition = FormStartPosition.CenterScreen;

            var title = new Label();
            title.Text = "CASHIER LOGIN";
            title.Font = new Font("Courier New", 16, FontStyle.Bold);
            title.ForeColor = Color.Cyan;
            title.Size = new Size(360, 40);
            title.Location = new Point(20, 20);
            title.TextAlign = ContentAlignment.MiddleCenter;
            this.Controls.Add(title);

            var subtitle = new Label();
            subtitle.Text = "Enter PIN";
            subtitle.Font = new Font("Segoe UI", 10);
            subtitle.ForeColor = Color.Gray;
            subtitle.Size = new Size(360, 25);
            subtitle.Location = new Point(20, 65);
            subtitle.TextAlign = ContentAlignment.MiddleCenter;
            this.Controls.Add(subtitle);

            // PIN display
            var pinDisplay = new Label();
            pinDisplay.Name = "pinDisplay";
            pinDisplay.Text = "____";
            pinDisplay.Font = new Font("Courier New", 28, FontStyle.Bold);
            pinDisplay.ForeColor = Color.LimeGreen;
            pinDisplay.BackColor = Color.Black;
            pinDisplay.Size = new Size(360, 70);
            pinDisplay.Location = new Point(20, 100);
            pinDisplay.TextAlign = ContentAlignment.MiddleCenter;
            pinDisplay.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(pinDisplay);

            // Status label
            var lblStatus = new Label();
            lblStatus.Name = "lblStatus";
            lblStatus.Text = "";
            lblStatus.Font = new Font("Segoe UI", 9);
            lblStatus.ForeColor = Color.Red;
            lblStatus.Size = new Size(360, 20);
            lblStatus.Location = new Point(20, 178);
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            this.Controls.Add(lblStatus);

            // Numpad
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "←", "0", "✓" };
            for (int i = 0; i < keys.Length; i++)
            {
                int row = i / 3;
                int col = i % 3;
                var btn = new Button();
                btn.Text = keys[i];
                btn.Tag = keys[i];
                btn.Font = new Font("Segoe UI", 14, FontStyle.Bold);
                btn.ForeColor = Color.White;
                btn.Size = new Size(110, 65);
                btn.Location = new Point(20 + col * 120, 210 + row * 75);
                btn.FlatStyle = FlatStyle.Flat;

                if (keys[i] == "✓")
                    btn.BackColor = Color.FromArgb(60, 140, 60);
                else if (keys[i] == "←")
                    btn.BackColor = Color.FromArgb(180, 60, 60);
                else
                    btn.BackColor = Color.FromArgb(60, 60, 60);

                btn.Click += PinButton_Click;
                this.Controls.Add(btn);
            }
        }

        private void PinButton_Click(object? sender, EventArgs e)
        {
            var btn = (Button)sender;
            var pinDisplay = (Label)this.Controls["pinDisplay"];
            var lblStatus = (Label)this.Controls["lblStatus"];

            switch (btn.Tag.ToString())
            {
                case "←":
                    if (_enteredPin.Length > 0)
                        _enteredPin = _enteredPin.Substring(0, _enteredPin.Length - 1);
                    break;
                case "✓":
                    _ = AttemptLoginAsync();
                    return;
                default:
                    if (_enteredPin.Length < 4)
                        _enteredPin += btn.Tag.ToString();
                    break;
            }

            // Update display with masked PIN
            string masked = new string('●', _enteredPin.Length).PadRight(4, '_');
            pinDisplay.Text = string.Join(" ", masked.ToCharArray());
            lblStatus.Text = "";
        }

        private async Task AttemptLoginAsync()
        {
            var lblStatus = (Label)this.Controls["lblStatus"];

            if (_enteredPin.Length < 4)
            {
                lblStatus.Text = "PIN must be 4 digits";
                return;
            }

            var result = await _db.LoginCashierAsync(_enteredPin);
            if (result is not null)
            {
                CashierId = result.Id;
                CashierName = result.Name;
                PosLogger.Info("AUTH", $"Cashier logged in: {CashierName}");
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                lblStatus.Text = "Invalid PIN. Try again.";
                _enteredPin = "";
                var pinDisplay = (Label)this.Controls["pinDisplay"];
                pinDisplay.Text = "_ _ _ _";
                PosLogger.Warn("AUTH", "Failed login attempt");
            }
        }
    }
}
