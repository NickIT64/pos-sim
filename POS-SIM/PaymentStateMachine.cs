using System;

namespace POS_SIM
{
    public enum TerminalState
    {
        Idle,
        AmountEntered,
        CardRead,
        Processing,
        Approved,
        Declined,
        Cancelled
    }

    public class PaymentStateMachine
    {
        public TerminalState CurrentState { get; private set; } = TerminalState.Idle;
        public decimal Amount { get; private set; } = 0;
        public string CardMethod { get; private set; } = "";

        public event Action<TerminalState, string> OnStateChanged;

        public void EnterAmount(decimal amount)
        {
            if (CurrentState != TerminalState.Idle) return;
            Amount = amount;
            CurrentState = TerminalState.AmountEntered;
            OnStateChanged?.Invoke(CurrentState, $"AMOUNT: {Amount:C}");
        }

        public void ReadCard(string method)
        {
            if (CurrentState != TerminalState.AmountEntered) return;
            CardMethod = method;
            CurrentState = TerminalState.CardRead;
            OnStateChanged?.Invoke(CurrentState, $"{method} DETECTED\nPROCESSING...");
        }

        public void Process()
        {
            if (CurrentState != TerminalState.CardRead) return;
            CurrentState = TerminalState.Processing;
            OnStateChanged?.Invoke(CurrentState, "CONTACTING HOST...");

            // Simulate async processing with a timer
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                // 80% approval rate for simulation
                var rng = new Random();
                if (rng.Next(100) < 80)
                    Approve();
                else
                    Decline();
            };
            timer.Start();
        }

        private void Approve()
        {
            CurrentState = TerminalState.Approved;
            OnStateChanged?.Invoke(CurrentState, $"APPROVED\n{Amount:C}\nTHANK YOU!");
            Reset();
        }

        private void Decline()
        {
            CurrentState = TerminalState.Declined;
            OnStateChanged?.Invoke(CurrentState, "DECLINED\nPLEASE TRY AGAIN");
            Reset();
        }

        public void Cancel()
        {
            CurrentState = TerminalState.Cancelled;
            OnStateChanged?.Invoke(CurrentState, "CANCELLED");
            Reset();
        }

        private void Reset()
        {
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                CurrentState = TerminalState.Idle;
                Amount = 0;
                CardMethod = "";
                OnStateChanged?.Invoke(CurrentState, "READY");
            };
            timer.Start();
        }
    }
}