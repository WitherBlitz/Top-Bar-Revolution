// What's using the CPU, the memory and the GPU, process by process - for the
// drop-down a usage pill opens, and for what a PC shares on the network.
//
// CPU time, private memory and names come from one NtQuerySystemInformation
// call (Task Manager's own source), so a sample costs a millisecond or two
// rather than a handle per process. TopBar runs as a 32-bit process, where the
// SIZE_T sizes in that structure are cut short; the private working set used
// here is a 64-bit field, so it isn't.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TopBarExtras
{
    internal sealed class TopRow
    {
        public string Name;
        public double Value;
        public int Count;           // processes folded into this row
    }

    internal sealed class ProcessSampler
    {
        private Dictionary<int, long> lastCpu = new Dictionary<int, long>();
        private long lastStamp;

        public Dictionary<int, string> Names = new Dictionary<int, string>();
        public Dictionary<int, double> Cpu = new Dictionary<int, double>();    // % of the whole CPU
        public Dictionary<int, double> Ram = new Dictionary<int, double>();    // bytes, private working set
        public bool Ready;          // CPU figures need two samples

        public void Sample()
        {
            long stamp = Stopwatch.GetTimestamp();
            Dictionary<int, long> cpuNow = new Dictionary<int, long>();
            Dictionary<int, string> names = new Dictionary<int, string>();
            Dictionary<int, double> ram = new Dictionary<int, double>();
            if (!Read(cpuNow, names, ram)) return;

            Dictionary<int, double> cpu = new Dictionary<int, double>();
            double wall = (stamp - lastStamp) * 1e7 / Stopwatch.Frequency;      // 100ns units
            if (lastStamp != 0 && wall > 0)
            {
                double capacity = wall * Environment.ProcessorCount;
                foreach (KeyValuePair<int, long> kv in cpuNow)
                {
                    long before;
                    if (lastCpu.TryGetValue(kv.Key, out before) && kv.Value >= before)
                        cpu[kv.Key] = Math.Min(100, 100.0 * (kv.Value - before) / capacity);
                }
                Ready = true;
            }
            lastCpu = cpuNow;
            lastStamp = stamp;
            Names = names;
            Ram = ram;
            Cpu = cpu;
        }

        // The biggest users, with every process of one name folded into one
        // row ("chrome" rather than twenty chromes).
        public static List<TopRow> Top(Dictionary<int, double> values, Dictionary<int, string> names, int count)
        {
            return Top(values, names, count, 0);
        }

        // least: rows below it are left out - a "0.0%" row is just noise.
        public static List<TopRow> Top(Dictionary<int, double> values, Dictionary<int, string> names,
                                       int count, double least)
        {
            Dictionary<string, TopRow> rows = new Dictionary<string, TopRow>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<int, double> kv in values)
            {
                if (kv.Value <= 0) continue;
                string name;
                if (!names.TryGetValue(kv.Key, out name)) name = "pid " + kv.Key;
                TopRow r;
                if (!rows.TryGetValue(name, out r))
                {
                    r = new TopRow();
                    r.Name = name;
                    rows[name] = r;
                }
                r.Value += kv.Value;
                r.Count++;
            }
            return rows.Values.Where(r => r.Value >= least).OrderByDescending(r => r.Value).Take(count).ToList();
        }

        private static bool Read(Dictionary<int, long> cpu, Dictionary<int, string> names, Dictionary<int, double> ram)
        {
            int size = 512 * 1024;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int needed;
                    int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        size = Math.Max(size * 2, needed + 64 * 1024);
                        continue;
                    }
                    if (status != 0) return false;
                    long offset = 0;
                    while (true)
                    {
                        IntPtr at = new IntPtr(buffer.ToInt64() + offset);
                        SYSTEM_PROCESS_INFORMATION p = (SYSTEM_PROCESS_INFORMATION)Marshal.PtrToStructure(
                            at, typeof(SYSTEM_PROCESS_INFORMATION));
                        int pid = (int)p.UniqueProcessId.ToInt64();
                        if (pid != 0)                       // not the Idle "process"
                        {
                            string name = p.NameBuffer == IntPtr.Zero || p.NameLength == 0
                                ? "System" : Marshal.PtrToStringUni(p.NameBuffer, p.NameLength / 2);
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                name = name.Substring(0, name.Length - 4);
                            names[pid] = name;
                            cpu[pid] = p.UserTime + p.KernelTime;
                            ram[pid] = p.WorkingSetPrivateSize;
                        }
                        if (p.NextEntryOffset == 0) break;
                        offset += p.NextEntryOffset;
                    }
                    return true;
                }
                catch (Exception) { return false; }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return false;
        }

        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        // The start of SYSTEM_PROCESS_INFORMATION - all that's read. With
        // sequential layout the IntPtr fields land where they do natively,
        // in a 32-bit process and a 64-bit one alike.
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_PROCESS_INFORMATION
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long WorkingSetPrivateSize;
            public uint HardFaultCount;
            public uint NumberOfThreadsHighWatermark;
            public ulong CycleTime;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public ushort NameLength;
            public ushort NameMaximumLength;
            public IntPtr NameBuffer;
            public int BasePriority;
            public IntPtr UniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);
    }
}
