using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;

namespace ParsecDisplay.Vdd
{
    internal static class Controller
    {
        static readonly ReaderWriterLockSlim HandleLifetime = new ReaderWriterLockSlim();
        static int ReopenRequested;
        static Thread UpdateThread;
        static Thread StatusThread;
        static CancellationTokenSource Cancellation;
        static IntPtr VddHandle = IntPtr.Zero;

        // Stored as int so Interlocked can be used safely; cast on read
        static int LastStatusValue;
        static Device.Status LastStatus => (Device.Status)Volatile.Read(ref LastStatusValue);

        // Wakes StatusLoop on demand (e.g. after Resume) instead of waiting up to 2s
        static ManualResetEventSlim StatusKick;
        // Signals that the device handle is open and ready to receive IOCTLs
        static ManualResetEventSlim HandleReady;
        // True while the controller is suspended (sleep / hibernation)
        static volatile bool Suspended;

        public static bool IsSuspended => Suspended;
        public static bool IsHandleReady => VddHandle.IsValidHandle();

        public static void Start()
        {
            Cancellation = new CancellationTokenSource();
            StatusKick = new ManualResetEventSlim(false);
            HandleReady = new ManualResetEventSlim(false);
            Suspended = false;

            UpdateThread = new Thread(() => UpdateLoop(Cancellation.Token));
            UpdateThread.IsBackground = true;
            UpdateThread.Priority = ThreadPriority.Highest;

            StatusThread = new Thread(() => StatusLoop(Cancellation.Token));
            StatusThread.IsBackground = true;
            StatusThread.Priority = ThreadPriority.BelowNormal;

            UpdateThread.Start();
            StatusThread.Start();
            Log.Info("Controller started");
        }

        public static void Stop()
        {
            Log.Info("Controller stopping");
            Cancellation?.Cancel();
            StatusKick?.Set();
            UpdateThread?.Join();
            StatusThread?.Join();

            var handle = Interlocked.Exchange(ref VddHandle, IntPtr.Zero);
            Device.CloseHandle(handle);

            StatusKick?.Dispose();
            HandleReady?.Dispose();
            Log.Info("Controller stopped");
        }

        /// <summary>
        /// Block until the device handle is open or the timeout elapses.
        /// Returns true if the handle is ready.
        /// </summary>
        public static bool WaitForReady(int timeoutMs)
        {
            return HandleReady != null && HandleReady.Wait(timeoutMs);
        }

        /// <summary>
        /// Force StatusLoop to re-check the driver status now instead of
        /// waiting up to 2 seconds for the next tick. Cheap, no allocation.
        /// </summary>
        public static void KickStatusCheck()
        {
            StatusKick?.Set();
        }

        /// <summary>
        /// Suspend driver activity: snapshot current displays, unplug them in
        /// reverse order (preserves Windows 10 Connectivity registry config),
        /// then close the device handle so no IOCTLs are sent while the system
        /// sleeps. The keep-alive thread becomes a no-op while suspended.
        /// </summary>
        public static List<Display.State> Suspend()
        {
            if (Suspended)
                return new List<Display.State>();

            var displays = Core.GetDisplays();
            var snapshot = displays.ConvertAll(d => d.Snapshot());
            Log.Info("Suspend: snapshot {0} display(s)", snapshot.Count);

            Suspended = true;

            HandleLifetime.EnterWriteLock();
            try {
            // Unplug in reverse order to keep Windows 10 from inventing a
            // new Connectivity registry entry for the remaining subset.
            if (VddHandle.IsValidHandle())
            {
                for (int i = displays.Count - 1; i >= 0; i--)
                {
                    try { Core.RemoveDisplay(VddHandle, displays[i].DisplayIndex); }
                    catch (Exception ex) { Log.Warn("Suspend: remove index {0} failed: {1}", displays[i].DisplayIndex, ex.Message); }
                }
            }

            var handle = Interlocked.Exchange(ref VddHandle, IntPtr.Zero);
            HandleReady.Reset();
            Device.CloseHandle(handle);

            } finally { HandleLifetime.ExitWriteLock(); }

            return snapshot;
        }

        /// <summary>
        /// Mark the controller as resumed and wake StatusLoop so the handle
        /// is reopened as soon as the driver reports OK. Callers should then
        /// WaitForReady before adding displays.
        /// </summary>
        public static void Resume()
        {
            Log.Info("Resume");
            Suspended = false;
            StatusKick?.Set();
        }

        static void UpdateLoop(CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            var health = new HeartbeatHealth();
            double previousStart = 0, summaryAt = 0;
            while (!cancellation.IsCancellationRequested)
            {
                var started = clock.Elapsed.TotalMilliseconds;
                var gap = previousStart == 0 ? 0 : started - previousStart;
                previousStart = started;
                bool attempted = false, success = false; int error = 0;
                try
                {
                    HandleLifetime.EnterReadLock();
                    try
                    {
                        if (!Suspended && VddHandle.IsValidHandle() && LastStatus == Device.Status.OK)
                        { attempted = true; success = Core.Update(VddHandle); error = Core.LastIoError; }
                    }
                    finally { HandleLifetime.ExitReadLock(); }
                    if (attempted)
                    {
                        var now = clock.Elapsed.TotalMilliseconds; var ioMs = now - started;
                        var reopen = health.Record(success, now, ioMs);
                        if (health.ShouldWarn(now, success, ioMs, gap))
                            Log.Warn("Heartbeat ok={0} error={1} ioMs={2:F1} startGapMs={3:F1} failures={4}", success, error, ioMs, gap, health.ConsecutiveFailures);
                        if (reopen)
                        { Interlocked.Exchange(ref ReopenRequested, 1); StatusKick.Set(); Log.Warn("Heartbeat requested handle reopen after repeated failures"); }
                        if (now - summaryAt >= 60000)
                        {
                            Log.Info("Heartbeat summary failures={0} maxSuccessGapMs={1:F1} maxIoMs={2:F1}", health.Failures, health.MaximumGapMs, health.MaximumIoMs);
                            summaryAt = now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var now = clock.Elapsed.TotalMilliseconds;
                    var reopen = health.Record(false, now, now - started);
                    if (health.ShouldWarn(now, false, now - started, gap)) Log.Error("Heartbeat loop exception: {0}", ex);
                    if (reopen) { Interlocked.Exchange(ref ReopenRequested, 1); StatusKick.Set(); }
                }
                if (cancellation.WaitHandle.WaitOne(100)) break;
            }
        }

        static void StatusLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (Suspended)
                {
                    // Sleep until Resume() (or Stop) signals us
                    try { StatusKick.Wait(Timeout.Infinite, cancellation); }
                    catch (OperationCanceledException) { break; }
                    StatusKick.Reset();
                    continue;
                }

                var prev = LastStatus;
                var status = QueryStatus(out var _);
                Volatile.Write(ref LastStatusValue, (int)status);
                if (status != prev)
                    Log.Info("Driver status: {0} -> {1}", prev, status);

                HandleLifetime.EnterWriteLock();
                try {
                if (Interlocked.Exchange(ref ReopenRequested, 0) != 0) {
                    var stale = Interlocked.Exchange(ref VddHandle, IntPtr.Zero);
                    HandleReady.Reset(); Device.CloseHandle(stale);
                    Log.Warn("Reopening driver handle following heartbeat failure");
                }
                if (status == Device.Status.OK)
                {
                    if (!VddHandle.IsValidHandle())
                    {
                        Device.OpenHandle(Core.ADAPTER_GUID, out var handle);
                        Interlocked.Exchange(ref VddHandle, handle);
                        if (handle.IsValidHandle())
                        {
                            HandleReady.Set();
                            Log.Info("Handle opened");
                        }
                        else
                        {
                            Log.Warn("Failed to open device handle while status is OK");
                        }
                    }
                }
                else
                {
                    var handle = Interlocked.Exchange(ref VddHandle, IntPtr.Zero);
                    if (handle != IntPtr.Zero)
                    {
                        HandleReady.Reset();
                        Device.CloseHandle(handle);
                        Log.Info("Handle closed (status={0})", status);
                    }
                }

                } finally { HandleLifetime.ExitWriteLock(); }

                // Wait up to 2s — KickStatusCheck() / Resume() shortcut this.
                try { StatusKick.Wait(2000, cancellation); }
                catch (OperationCanceledException) { break; }
                StatusKick.Reset();
            }
        }

        public static Device.Status QueryStatus(out Version version)
        {
            return Device.QueryStatus(Core.CLASS_GUID, Core.HARDWARE_ID, out version);
        }

        public static Device.Status QueryStatus()
        {
            return QueryStatus(out var _);
        }

        public static void AddDisplay()
        {
            AddDisplay(out var _);
        }

        public static void AddDisplay(out int driverIndex)
        {
            driverIndex = -1;

            var status = QueryStatus();
            if (status != Device.Status.OK)
            {
                Log.Warn("AddDisplay refused: driver status = {0}", status);
                throw new ErrorDriverStatus(status);
            }

            int limit = Core.MAX_DISPLAYS;
            var displays = Core.GetDisplays();

            if (displays.Count >= limit)
            {
                Log.Warn("AddDisplay refused: limit {0} reached", limit);
                throw new ErrorExceededLimit(limit);
            }

            HandleLifetime.EnterReadLock();
            try {
                var handle = VddHandle;
                if (!handle.IsValidHandle()) throw new ErrorDeviceHandle();
                if (!Core.AddDisplay(handle, out driverIndex))
                    throw new ErrorOperationFailed(ErrorOperationFailed.Operation.AddDisplay);
            } finally { HandleLifetime.ExitReadLock(); }

            Log.Info("AddDisplay: index={0}", driverIndex);
        }

        public static void RemoveDisplay(int index)
        {
            var status = QueryStatus();
            if (status != Device.Status.OK)
            {
                Log.Warn("RemoveDisplay({0}) refused: driver status = {1}", index, status);
                throw new ErrorDriverStatus(status);
            }

            if (index < 0)
                return;

            HandleLifetime.EnterReadLock();
            try {
                var handle = VddHandle;
                if (!handle.IsValidHandle()) throw new ErrorDeviceHandle();
                if (!Core.RemoveDisplay(handle, index))
                    throw new ErrorOperationFailed(ErrorOperationFailed.Operation.RemoveDisplay);
            } finally { HandleLifetime.ExitReadLock(); }

            Log.Info("RemoveDisplay: index={0}", index);
        }

        public static void RemoveLastDisplay()
        {
            var displays = Core.GetDisplays();
            if (displays.Count > 0)
            {
                var last = displays[displays.Count - 1];
                RemoveDisplay(last.DisplayIndex);
            }
        }
    }
}
