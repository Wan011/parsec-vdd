using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ParsecDisplay
{
    // Bounded asynchronous logging keeps disk I/O off the keep-alive thread.
    internal static class Log
    {
        static readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
        static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        static readonly Thread Worker;
        static readonly string LogPath;
        static int Queued;
        static volatile bool Stopping;
        internal const int MaximumBytes = 512 * 1024;

        static Log()
        {
            var directory = Environment.GetEnvironmentVariable("PARSEC_DISPLAY_LOG_DIR");
            if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            LogPath = Path.Combine(directory, "debug.log");
            Worker = new Thread(Drain) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Parsec bounded log" };
            Worker.Start();
        }
        public static void Info(string f, params object[] a) { Write("INF", f, a); }
        public static void Debug(string f, params object[] a) { Write("DBG", f, a); }
        public static void Warn(string f, params object[] a) { Write("WRN", f, a); }
        public static void Error(string f, params object[] a) { Write("ERR", f, a); }

        static void Write(string level, string format, object[] args)
        {
            if (Stopping) return;
            if (Interlocked.Increment(ref Queued) > 64) { Interlocked.Decrement(ref Queued); return; }
            string message;
            try { message = args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args); }
            catch { message = format; }
            if (message.Length > 2000) message = message.Substring(0, 2000);
            Queue.Enqueue("[" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + level + " " + message.Replace('\r', ' ').Replace('\n', ' '));
            Wake.Set();
        }
        static void Drain()
        {
            var lastPrune = DateTime.MinValue;
            while (!Stopping || !Queue.IsEmpty)
            {
                Wake.WaitOne(1000);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    string line;
                    while (Queue.TryDequeue(out line))
                    {
                        Interlocked.Decrement(ref Queued);
                        try { Console.WriteLine(line); } catch { }
                        File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
                        if (new FileInfo(LogPath).Length > MaximumBytes) Prune(LogPath, DateTime.Now.AddHours(-1));
                    }
                    if (File.Exists(LogPath) && (DateTime.Now - lastPrune).TotalMinutes >= 1)
                    {
                        Prune(LogPath, DateTime.Now.AddHours(-1)); lastPrune = DateTime.Now;
                    }
                }
                catch { /* A logging failure must not stop driver maintenance. */ }
            }
        }
        internal static void Prune(string path, DateTime cutoff)
        {
            var retained = new System.Collections.Generic.Queue<string>(); int bytes = 0;
            foreach (var line in File.ReadLines(path))
            {
                DateTime time;
                if (line.Length < 25 || !DateTime.TryParseExact(line.Substring(1, 23), "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) || time < cutoff) continue;
                retained.Enqueue(line); bytes += Encoding.UTF8.GetByteCount(line) + 2;
                while (bytes > MaximumBytes && retained.Count > 0) bytes -= Encoding.UTF8.GetByteCount(retained.Dequeue()) + 2;
            }
            File.WriteAllLines(path, retained.ToArray(), new UTF8Encoding(false));
        }
        public static void Shutdown() { Stopping = true; Wake.Set(); Worker.Join(2000); }
    }
}
