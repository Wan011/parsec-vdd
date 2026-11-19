namespace ParsecDisplay.Vdd
{
    internal sealed class MissingDisplayPolicy
    {
        int MissingSamples;
        long NextAttemptMs;
        internal bool Observe(int existing, int wanted, bool blocked, long nowMs)
        {
            if (blocked || existing > 0 || wanted <= 0) { MissingSamples = 0; return false; }
            MissingSamples++;
            if (MissingSamples < 2 || nowMs < NextAttemptMs) return false;
            NextAttemptMs = nowMs + 30000;
            return true;
        }
    }
}
