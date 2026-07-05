using System;
using System.Drawing;
using System.Windows.Forms;
using POS.Shared;

namespace POS_SIM
{
    public partial class TerminalForm : Form
    {
        private readonly PosDatabase _db = new PosDatabase();
        private PaymentStateMachine _machine = new PaymentStateMachine();
        private int _currentBusinessDayId = -1;
        private int _currentShiftId = -1;
        private int _currentCashierId = -1;
        private string _currentCashierName = "UNKNOWN";

        public TerminalForm()
        {
            InitializeComponent();
            SetupTerminal();
            _machine.OnStateChanged += OnStateChanged;
            this.Load += async (s, e) => await StartSessionAsync();
        }

        private async Task StartSessionAsync()
        {
            _db.Initialize();

            var login = new LoginForm(_db);
            if (login.ShowDialog() == DialogResult.OK)
            {
                _currentCashierId = login.CashierId;
                _currentCashierName = login.CashierName;
            }
            else
            {
                Application.Exit();
                return;
            }

            _currentBusinessDayId = await _db.OpenBusinessDayAsync();
            _currentShiftId = await _db.StartShiftAsync(_currentCashierId, _currentBusinessDayId);

            PosLogger.Info("SESSION", $"Session started - Cashier: {_currentCashierName}");

            var display = (Label)this.Controls["lblDisplay"];
            display.Text = "READY";
        }

        private async void OnStateChanged(TerminalState state, string message)
        {
            var display = (Label)this.Controls["lblDisplay"];
            string clean = message.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            display.Text = message;

            PosLogger.Info("PAYMENT_PROCESSOR", $"State changed to: {state} | Display: {clean}");

            switch (state)
            {
                case TerminalState.Approved:
                    display.ForeColor = Color.LimeGreen;
                    PosLogger.Info("PAYMENT_PROCESSOR", $"Transaction APPROVED - Amount: {_machine.Amount:C}");
                    await _db.RecordTransactionAsync(_currentShiftId, _machine.Amount, _machine.CardMethod, "APPROVED");
                    break;
                case TerminalState.Declined:
                    display.ForeColor = Color.Red;
                    PosLogger.Warn("PAYMENT_PROCESSOR", $"Transaction DECLINED - Amount: {_machine.Amount:C}");
                    await _db.RecordTransactionAsync(_currentShiftId, _machine.Amount, _machine.CardMethod, "DECLINED");
                    break;
                case TerminalState.Processing:
                    display.ForeColor = Color.Yellow;
                    break;
                case TerminalState.Cancelled:
                    display.ForeColor = Color.Orange;
                    PosLogger.Warn("PAYMENT_PROCESSOR", "Transaction CANCELLED by operator");
                    break;
                default:
                    display.ForeColor = Color.LimeGreen;
                    break;
            }
        }

        private void SetupTerminal()
        {
            PosLogger.Source = "TERMINAL";
            PosLogger.Info("GENERAL", "POS Terminal starting up...");
            this.Text = "POS Terminal Simulator";
            this.Size = new Size(480, 720);
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.StartPosition = FormStartPosition.CenterScreen;

            var display = new Label();
            display.Name = "lblDisplay";
            display.Text = "READY";
            display.Font = new Font("Courier New", 18, FontStyle.Bold);
            display.ForeColor = Color.LimeGreen;
            display.BackColor = Color.Black;
            display.Size = new Size(420, 80);
            display.Location = new Point(20, 20);
            display.TextAlign = ContentAlignment.MiddleCenter;
            display.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(display);

            var amountBox = new TextBox();
            amountBox.Name = "txtAmount";
            amountBox.Font = new Font("Courier New", 14);
            amountBox.ForeColor = Color.White;
            amountBox.BackColor = Color.FromArgb(50, 50, 50);
            amountBox.Size = new Size(420, 40);
            amountBox.Location = new Point(20, 115);
            amountBox.TextAlign = HorizontalAlignment.Right;
            amountBox.PlaceholderText = "0.00";
            this.Controls.Add(amountBox);

            string[] keys = { "7", "8", "9", "4", "5", "6", "1", "2", "3", "00", "0", "." };
            for (int i = 0; i < keys.Length; i++)
            {
                int row = i / 3;
                int col = i % 3;
                var btn = new Button();
                btn.Text = keys[i];
                btn.Tag = keys[i];
                btn.Font = new Font("Segoe UI", 14, FontStyle.Bold);
                btn.ForeColor = Color.White;
                btn.BackColor = Color.FromArgb(60, 60, 60);
                btn.FlatStyle = FlatStyle.Flat;
                btn.Size = new Size(120, 70);
                btn.Location = new Point(20 + col * 130, 170 + row * 80);
                btn.Click += NumpadButton_Click;
                this.Controls.Add(btn);
            }

            AddActionButton("CLEAR", Color.FromArgb(180, 60, 60), new Point(20, 510), "CLEAR");
            AddActionButton("ENTER", Color.FromArgb(60, 140, 60), new Point(160, 510), "ENTER");
            AddActionButton("CANCEL", Color.FromArgb(100, 100, 100), new Point(300, 510), "CANCEL");
            AddActionButton("TAP CARD", Color.FromArgb(30, 100, 180), new Point(20, 600), "TAP");
            AddActionButton("INSERT CARD", Color.FromArgb(30, 100, 180), new Point(160, 600), "INSERT");
            AddActionButton("SWIPE", Color.FromArgb(30, 100, 180), new Point(300, 600), "SWIPE");
        }

        private void AddActionButton(string label, Color color, Point location, string tag)
        {
            var btn = new Button();
            btn.Text = label;
            btn.Tag = tag;
            btn.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btn.ForeColor = Color.White;
            btn.BackColor = color;
            btn.FlatStyle = FlatStyle.Flat;
            btn.Size = new Size(120, 60);
            btn.Location = location;
            btn.Click += ActionButton_Click;
            this.Controls.Add(btn);
        }

        private void NumpadButton_Click(object sender, EventArgs e)
        {
            if (_machine.CurrentState != TerminalState.Idle) return;
            var btn = (Button)sender;
            var txt = (TextBox)this.Controls["txtAmount"];
            txt.Text += btn.Tag.ToString();
            PosLogger.Debug("TOUCH_ACTIVITY", $"Numpad pressed: {btn.Tag}");
        }

        private void ActionButton_Click(object sender, EventArgs e)
        {
            var btn = (Button)sender;
            var txt = (TextBox)this.Controls["txtAmount"];
            PosLogger.Info("TOUCH_ACTIVITY", $"Action button pressed: {btn.Tag}");

            switch (btn.Tag.ToString())
            {
                case "CLEAR":
                    txt.Text = "";
                    _machine.Cancel();
                    break;
                case "ENTER":
                    if (decimal.TryParse(txt.Text, out decimal amount) && amount > 0)
                    {
                        PosLogger.Info("TOUCH_ACTIVITY", $"Amount entered: {amount:C}");
                        _machine.EnterAmount(amount);
                    }
                    else
                    {
                        PosLogger.Warn("TOUCH_ACTIVITY", "Invalid amount entered");
                    }
                    break;
                case "CANCEL":
                    txt.Text = "";
                    _machine.Cancel();
                    break;
                case "TAP":
                case "INSERT":
                case "SWIPE":
                    PosLogger.Info("TOUCH_ACTIVITY", $"Card read method: {btn.Tag}");
                    _machine.ReadCard(btn.Tag.ToString());
                    _machine.Process();
                    break;
            }
        }
    }
}
