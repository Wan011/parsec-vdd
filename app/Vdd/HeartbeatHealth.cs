using System;

namespace ParsecDisplay.Vdd
{
    // Pure policy, tested with simulated driver failures and scheduling gaps.
    internal sealed class HeartbeatHealth
    {
        internal int ConsecutiveFailures { get; private set; }
        internal long Failures { get; private set; }
        internal double LastSuccessMs { get; private set; } = -1;
        internal double MaximumGapMs { get; private set; }
        internal double MaximumIoMs { get; private set; }
        double LastRecoveryMs = double.NegativeInfinity;
        double LastWarningMs = double.NegativeInfinity;

        internal bool Record(bool success, double nowMs, double ioMs)
        {
            MaximumIoMs = Math.Max(MaximumIoMs, ioMs);
            if (LastSuccessMs >= 0) MaximumGapMs = Math.Max(MaximumGapMs, nowMs - LastSuccessMs);
            if (success) { ConsecutiveFailures = 0; LastSuccessMs = nowMs; return false; }
            ConsecutiveFailures++; Failures++;
            if (ConsecutiveFailures < 3 || nowMs - LastRecoveryMs < 30000) return false;
            LastRecoveryMs = nowMs; return true;
        }

        internal bool ShouldWarn(double nowMs, bool success, double ioMs, double gapMs)
        {
            if (success && ioMs < 250 && gapMs < 500) return false;
            if (nowMs - LastWarningMs < 10000) return false;
            LastWarningMs = nowMs; return true;
        }
    }
}
