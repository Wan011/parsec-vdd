using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ParsecDisplay.Vdd;

namespace ParsecDisplay
{
    internal class Display
    {
        public int DisplayIndex;
        public class State { }
        public State Snapshot() { return new State(); }
    }
    internal static class Device
    {
        internal enum Status { OK, INACCESSIBLE }
        internal static int OpenCount, UnsafeCloses;
        public static Status QueryStatus(string c,string h,out Version v) { v=new Version(0,45);return Status.OK; }
        public static bool OpenHandle(string guid,out IntPtr h) { h=new IntPtr(Interlocked.Increment(ref OpenCount));return true; }
        public static void CloseHandle(IntPtr h) { if(h!=IntPtr.Zero && Volatile.Read(ref Vdd.Core.InFlight)!=0)Interlocked.Increment(ref UnsafeCloses); }
    }
    internal static class HandleExtensions
    {
        public static bool IsValidHandle(this IntPtr h) { return h!=IntPtr.Zero && h!=new IntPtr(-1); }
    }
    internal static class HeartbeatTests
    {
        static void Check(bool condition,string label) { if(!condition)throw new Exception(label);Console.WriteLine("PASS "+label); }
        static bool Wait(Func<bool> condition,int ms) { var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){if(condition())return true;Thread.Sleep(20);}return condition(); }
        static int Main()
        {
            var root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-artifacts");Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("PARSEC_DISPLAY_LOG_DIR",root);
            try
            {
                var h=new HeartbeatHealth();
                Check(!h.Record(false,100,30) && !h.Record(false,200,30),"transient failures do not reopen");
                Check(h.Record(false,300,30),"three failures request recovery");
                Check(!h.Record(false,400,30) && h.Record(false,30300,30),"recovery requests are throttled");
                h.Record(true,30500,20);Check(h.ConsecutiveFailures==0,"success clears failure streak");
                h.Record(true,31500,700);Check(h.MaximumGapMs>=1000 && h.MaximumIoMs>=700,"slow I/O and success gaps retained");
                Check(h.ShouldWarn(32000,false,50,100) && !h.ShouldWarn(32100,false,50,100),"warnings are rate limited");
                var missing=new MissingDisplayPolicy();
                Check(!missing.Observe(0,1,false,0) && missing.Observe(0,1,false,5000),"missing display requires two independent samples");
                Check(!missing.Observe(0,1,false,10000) && missing.Observe(0,1,false,35000),"display recovery is rate limited");
                Check(!missing.Observe(1,1,false,70000),"existing display is never duplicated");
                Check(!missing.Observe(0,0,false,75000) && !missing.Observe(0,1,true,80000),"manual removal or suspend suppresses recovery");
                var path=Path.Combine(root,"prune.log");
                var old="["+DateTime.Now.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss.fff",CultureInfo.InvariantCulture)+"] old";
                var line="["+DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff",CultureInfo.InvariantCulture)+"] "+new string('\u4e2d',400);
                File.WriteAllLines(path,new[]{old}.Concat(Enumerable.Repeat(line,1200)),new UTF8Encoding(false));
                Log.Prune(path,DateTime.Now.AddHours(-1));
                Check(new FileInfo(path).Length<=Log.MaximumBytes && !File.ReadAllText(path).Contains("old"),"one-hour retention and UTF-8 byte cap");
                Controller.Start();
                try
                {
                    Check(Wait(()=>Volatile.Read(ref Device.OpenCount)>=2,6000),"real controller reopens failed mock driver handle");
                    Check(Wait(()=>Volatile.Read(ref Vdd.Core.Successes)>=3,3000),"heartbeats resume after reopen");
                    // A slow add holds a read lease: updates must still run concurrently.
                    var before=Volatile.Read(ref Vdd.Core.Successes);
                    var add=new Thread(()=>Controller.AddDisplay());add.Start();add.Join();
                    Check(Vdd.Core.Successes>before,"heartbeat continues during slow display add");
                    Controller.Suspend();
                    Check(Device.UnsafeCloses==0,"suspend and reopen never close an in-flight handle");
                    Controller.Resume();
                    Check(Controller.WaitForReady(3000),"resume reopens a ready handle");
                }
                finally { Controller.Stop(); }
                Log.Info("test complete");Log.Shutdown();
                Check(File.Exists(Path.Combine(root,"debug.log")),"asynchronous diagnostic log flushed");
                return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        }
    }
}
namespace ParsecDisplay.Vdd
{
    internal static class Core
    {
        internal static int InFlight, Calls, Successes;
        internal static int LastIoError;
        internal const string ADAPTER_GUID="mock",CLASS_GUID="mock",HARDWARE_ID="mock";
        internal const int MAX_DISPLAYS=8;
        public static bool Update(IntPtr h)
        {
            Interlocked.Increment(ref InFlight);
            try {Thread.Sleep(15);var ok=Interlocked.Increment(ref Calls)>3;LastIoError=ok?0:6;if(ok)Interlocked.Increment(ref Successes);return ok;}
            finally{Interlocked.Decrement(ref InFlight);}
        }
        public static List<Display> GetDisplays(){return new List<Display>();}
        public static bool AddDisplay(IntPtr h,out int index){Interlocked.Increment(ref InFlight);try{Thread.Sleep(500);index=0;return true;}finally{Interlocked.Decrement(ref InFlight);}}
        public static bool RemoveDisplay(IntPtr h,int index){return true;}
    }
}
