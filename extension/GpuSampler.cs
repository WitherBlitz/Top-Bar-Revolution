// Load and memory for every GPU at once, the way Task Manager works them out.
//
// The "GPU Engine" counters are per process and per engine, so each adapter's
// utilization is summed per engine type (3D, Copy, VideoDecode, ...) and its
// busiest engine type is its load. One ReadCategory() call returns all of the
// several hundred instances - every adapter, every process - in a few
// milliseconds. Adapters are keyed by the LUID in the instance names
// ("luid_0x00000000_0x0001BFDB"), which is also what DXGI reports.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopBarExtras
{
    internal sealed class GpuSampler
    {
        private static readonly Regex LuidOf =
            new Regex(@"luid_0x[0-9A-F]{8}_0x[0-9A-F]{8}", RegexOptions.IgnoreCase);
        private static readonly Regex PidOf = new Regex(@"pid_(\d+)_", RegexOptions.IgnoreCase);

        private readonly PerformanceCounterCategory engines;
        private readonly PerformanceCounterCategory memory;
        private Dictionary<string, CounterSample> previous = new Dictionary<string, CounterSample>();

        // Per LUID, each process's load on that GPU (its busiest engine type,
        // as Task Manager's GPU column has it), as of the last Loads() call.
        public Dictionary<string, Dictionary<int, double>> Processes =
            new Dictionary<string, Dictionary<int, double>>(StringComparer.OrdinalIgnoreCase);

        public GpuSampler()
        {
            try { engines = new PerformanceCounterCategory("GPU Engine"); } catch (Exception) { }
            try { memory = new PerformanceCounterCategory("GPU Adapter Memory"); } catch (Exception) { }
        }

        // Load (0-100) per LUID since the previous call. The first call only
        // primes the samples, so it returns nothing.
        public Dictionary<string, double> Loads()
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (engines == null) return result;
            InstanceDataCollection util = engines.ReadCategory()["utilization percentage"];
            if (util == null) return result;

            Dictionary<string, Dictionary<string, double>> perType =
                new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            // luid -> pid -> engine type -> load
            Dictionary<string, Dictionary<int, Dictionary<string, double>>> perProcess =
                new Dictionary<string, Dictionary<int, Dictionary<string, double>>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CounterSample> next = new Dictionary<string, CounterSample>();
            foreach (InstanceData d in util.Values)
            {
                string inst = d.InstanceName;
                Match m = LuidOf.Match(inst);
                if (!m.Success) continue;
                next[inst] = d.Sample;
                CounterSample before;
                if (!previous.TryGetValue(inst, out before)) continue;
                int at = inst.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                string type = at < 0 ? "?" : inst.Substring(at + 8);
                double value = CounterSample.Calculate(before, d.Sample);
                Dictionary<string, double> types;
                if (!perType.TryGetValue(m.Value, out types))
                    perType[m.Value] = types = new Dictionary<string, double>();
                double sum;
                types.TryGetValue(type, out sum);
                types[type] = sum + value;

                Match pm = PidOf.Match(inst);
                int pid;
                if (value <= 0 || !pm.Success || !int.TryParse(pm.Groups[1].Value, out pid)) continue;
                Dictionary<int, Dictionary<string, double>> procs;
                if (!perProcess.TryGetValue(m.Value, out procs))
                    perProcess[m.Value] = procs = new Dictionary<int, Dictionary<string, double>>();
                Dictionary<string, double> ptypes;
                if (!procs.TryGetValue(pid, out ptypes)) procs[pid] = ptypes = new Dictionary<string, double>();
                ptypes.TryGetValue(type, out sum);
                ptypes[type] = sum + value;
            }
            previous = next;
            foreach (KeyValuePair<string, Dictionary<string, double>> kv in perType)
                result[kv.Key] = Math.Max(0, Math.Min(100, kv.Value.Values.DefaultIfEmpty(0).Max()));

            Dictionary<string, Dictionary<int, double>> processes =
                new Dictionary<string, Dictionary<int, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<int, Dictionary<string, double>>> kv in perProcess)
            {
                Dictionary<int, double> loads = new Dictionary<int, double>();
                foreach (KeyValuePair<int, Dictionary<string, double>> p in kv.Value)
                    loads[p.Key] = Math.Min(100, p.Value.Values.Max());
                processes[kv.Key] = loads;
            }
            Processes = processes;
            return result;
        }

        // Dedicated video memory in use per LUID, in bytes.
        public Dictionary<string, double> Vram()
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (memory == null) return result;
            InstanceDataCollection used = memory.ReadCategory()["dedicated usage"];
            if (used == null) return result;
            foreach (InstanceData d in used.Values)
            {
                Match m = LuidOf.Match(d.InstanceName);
                if (!m.Success) continue;
                double sum;
                result.TryGetValue(m.Value, out sum);
                result[m.Value] = sum + d.RawValue;
            }
            return result;
        }
    }
}
