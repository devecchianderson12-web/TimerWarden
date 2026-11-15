using System;
using System.Windows.Forms;

namespace CandadoApp
{
    internal sealed class Countdown : IDisposable
    {
        private readonly Timer timer = new Timer { Interval = 200 };
        private DateTime endUtc;
        private TimeSpan pausedRemaining;

        public bool Active { get; private set; }
        public bool Paused { get; private set; }

        public event EventHandler Changed;
        public event EventHandler Finished;

        public Countdown()
        {
            timer.Tick += OnTick;
        }

        public TimeSpan Remaining
        {
            get
            {
                if (!Active)
                    return TimeSpan.Zero;
                if (Paused)
                    return pausedRemaining;
                TimeSpan remaining = endUtc - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public string Text
        {
            get
            {
                long seconds = (long)Math.Ceiling(Remaining.TotalSeconds);
                return string.Format("{0:00}:{1:00}:{2:00}", seconds / 3600, seconds % 3600 / 60, seconds % 60);
            }
        }

        public void Start(int seconds)
        {
            endUtc = DateTime.UtcNow.AddSeconds(seconds);
            Active = true;
            Paused = false;
            timer.Start();
            RaiseChanged();
        }

        public void TogglePause()
        {
            if (!Active)
                return;
            if (Paused)
                endUtc = DateTime.UtcNow + pausedRemaining;
            else
                pausedRemaining = Remaining;
            Paused = !Paused;
            RaiseChanged();
        }

        public void Stop()
        {
            Active = false;
            Paused = false;
            timer.Stop();
            RaiseChanged();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (Paused)
                return;
            if (Remaining > TimeSpan.Zero)
            {
                RaiseChanged();
                return;
            }
            Stop();
            if (Finished != null)
                Finished(this, EventArgs.Empty);
        }

        private void RaiseChanged()
        {
            if (Changed != null)
                Changed(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }
}
