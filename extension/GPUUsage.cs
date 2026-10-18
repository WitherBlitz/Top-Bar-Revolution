// GPU readout: the adapter's name, the percentage, and a load bar filling left
// to right (the percentage can go after the bar instead).
//
// Load is worked out the way Task Manager does it - see GpuSampler.
//
// The adapter is found through DXGI, not the registry: the display-adapter
// class key keeps an entry for every card ever installed, while DXGI lists what
// is actually present - and hands back the LUID the counters are keyed by.
//
// Settings (all optional):
//   "label"             "auto" = the adapter's name without its brand
//                       ("RTX 5070 Ti"); any other text is shown as-is; "" hides it
//   "format"            "{percent:0}%" - also {vram_used} and {vram_total}, in GB
//   "vram"              add memory in use / total after the load (false)
//   "bar"               show the load bar                       (true)
//   "value-position"    "before" or "after" the bar             (before)
//   "color"             text color                              (#BBBBBB)
//   "bar-color"         fill: hex, or "from-percentage"         (from-percentage)
//   "background-color"  track                                   (#33BBBBBB)
//   "width", "height", "radius"   bar size                      (60, 10, 2)
//   "interval"          milliseconds between samples            (1000)
//   "adapter"           part of a name, to pick one GPU of several

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using System.Windows;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using TopBar;

namespace TopBarExtras.Widgets
{
    public class GPUUsage : iWidgetBase
    {
        private const double GB = 1024.0 * 1024.0 * 1024.0;

        private readonly Meter meter;
        private readonly GpuSampler sampler = new GpuSampler();
        private readonly Timer timer;
        private readonly string format;
        private readonly string widest;
        private readonly bool wantVram;
        private readonly string luid;
        private readonly double vramTotal;
        private readonly string title;
        private volatile Dictionary<int, double> users;
        private bool primed, disposed;

        // For the pill's drop-down: each process's load on this GPU as of the
        // last sample (null until there have been two).
        public Dictionary<int, double> Users { get { return users; } }
        public string Title { get { return title; } }

        public GPUUsage(object settings)
        {
            JObject s = settings as JObject;
            string fmt = Cfg.Str(s, "format", "{percent:0}%");
            if (Cfg.Bool(s, "vram", false) && fmt.IndexOf("vram", StringComparison.OrdinalIgnoreCase) < 0)
                fmt += "  {vram_used:0.0}/{vram_total:0} GB";
            format = fmt;
            wantVram = format.IndexOf("vram", StringComparison.OrdinalIgnoreCase) >= 0;

            GpuAdapter a = GpuAdapter.Pick(Cfg.Str(s, "adapter", ""));
            luid = a == null ? null : a.Luid;
            vramTotal = a == null ? 0 : a.DedicatedBytes / GB;
            title = "GPU" + (a == null ? "" : TopSource.Dot + GpuAdapter.Short(a.Name));

            meter = new Meter(s);
            string label = s != null && s["label"] != null ? s["label"].ToString() : "auto";
            meter.Name = label == "auto" ? (a == null ? "GPU" : GpuAdapter.Short(a.Name)) : label;

            double unused;
            Sample(out unused);                 // prime the previous samples
            widest = Meter.Widest(Compose(100, vramTotal));
            meter.Show(Compose(0, 0), 0, widest);
            Publish(a);

            timer = new Timer(Math.Max(250, Cfg.Num(s, "interval", 1000)));
            timer.AutoReset = false;            // never let a slow tick overlap the next
            timer.Elapsed += Tick;
            timer.Start();
        }

        public FrameworkElement GetComponent()
        {
            return meter.Panel;
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); }
        }

        private void Tick(object sender, ElapsedEventArgs e)
        {
            if (disposed) return;
            try
            {
                double vram;
                double load = Sample(out vram);
                string text = Compose(load, vram);
                meter.Panel.Dispatcher.BeginInvoke(new Action(delegate { meter.Show(text, load, widest); }));
            }
            catch (Exception)
            {
                // A counter hiccup must never take the bar down.
            }
            finally
            {
                if (!disposed) timer.Start();
            }
        }

        private double Sample(out double vramUsed)
        {
            vramUsed = 0;
            if (luid == null) return 0;
            double load;
            sampler.Loads().TryGetValue(luid, out load);
            Dictionary<int, double> mine;
            if (primed)
                users = sampler.Processes.TryGetValue(luid, out mine) ? mine : new Dictionary<int, double>();
            primed = true;
            if (wantVram)
            {
                double bytes;
                if (sampler.Vram().TryGetValue(luid, out bytes)) vramUsed = bytes / GB;
            }
            return load;
        }

        private string Compose(double load, double vram)
        {
            Dictionary<string, double> v = new Dictionary<string, double>();
            v["percent"] = load;
            v["vram_used"] = vram;
            v["vram_total"] = vramTotal;
            return Cfg.Compose(format, v);
        }

        // The settings app's preview can't run DXGI; leave it the detected name.
        private static void Publish(GpuAdapter a)
        {
            if (a == null) return;
            try
            {
                JObject j = new JObject();
                j["name"] = a.Name;
                j["short"] = GpuAdapter.Short(a.Name);
                j["vram_gb"] = Math.Round(a.DedicatedBytes / GB, 1);
                File.WriteAllText(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData), @"TopBar\gpu.json"), j.ToString());
            }
            catch (Exception) { }
        }
    }
}

namespace TopBarExtras
{
    // The GPUs DXGI can see, with the LUID the performance counters use.
    internal sealed class GpuAdapter
    {
        public string Name;
        public string Luid;
        public double DedicatedBytes;

        // A named GPU if asked for; otherwise the one with the most dedicated
        // memory - the real card, not a virtual display or basic adapter.
        //
        // DXGI can list the same card twice (an indirect display driver
        // exposes it again under a second LUID), and only one of those carries
        // performance counters. So adapters the counters actually report win;
        // picking by memory alone would be a coin flip between the two and
        // could leave the bar reading 0% forever.
        public static GpuAdapter Pick(string wanted)
        {
            List<GpuAdapter> all = Enumerate();
            HashSet<string> counted = CounterLuids();
            GpuAdapter best = null;
            foreach (GpuAdapter a in all)
            {
                if (wanted.Length > 0 && a.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                bool live = counted.Contains(a.Luid.ToUpperInvariant());
                bool bestLive = best != null && counted.Contains(best.Luid.ToUpperInvariant());
                if (best == null || (live && !bestLive)
                    || (live == bestLive && a.DedicatedBytes > best.DedicatedBytes))
                    best = a;
            }
            return best;
        }

        // Every real GPU, once each, in DXGI's order (the primary first): the
        // adapters the counters report, or - if the counters are missing -
        // those with video memory of their own, one per name.
        public static List<GpuAdapter> Real()
        {
            List<GpuAdapter> all = Enumerate();
            HashSet<string> counted = CounterLuids();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<GpuAdapter> real = new List<GpuAdapter>();
            foreach (GpuAdapter a in all)
            {
                bool ok = counted.Count > 0 ? counted.Contains(a.Luid.ToUpperInvariant())
                                            : a.DedicatedBytes >= 256.0 * 1024 * 1024;
                string key = counted.Count > 0 ? a.Luid : a.Name + "|" + a.DedicatedBytes;
                if (ok && seen.Add(key)) real.Add(a);
            }
            return real;
        }

        public static HashSet<string> CounterLuids()
        {
            HashSet<string> luids = new HashSet<string>();
            try
            {
                Regex luid = new Regex(@"luid_0x[0-9A-F]{8}_0x[0-9A-F]{8}", RegexOptions.IgnoreCase);
                foreach (string n in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                {
                    Match m = luid.Match(n);
                    if (m.Success) luids.Add(m.Value.ToUpperInvariant());
                }
            }
            catch (Exception) { }
            return luids;
        }

        // "NVIDIA GeForce RTX 5070 Ti" -> "RTX 5070 Ti": the brand goes, as it
        // did for the CPU label.
        public static string Short(string full)
        {
            string s = Regex.Replace(full,
                @"\b(NVIDIA|GeForce|AMD|ATI|Radeon|Intel)\b|\((R|TM)\)", "",
                RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 0 ? s : full;
        }

        public static List<GpuAdapter> Enumerate()
        {
            List<GpuAdapter> list = new List<GpuAdapter>();
            IDXGIFactory1 factory = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == null) return list;
                for (uint i = 0; ; i++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(i, out adapter) != 0) break;   // DXGI_ERROR_NOT_FOUND
                    DXGI_ADAPTER_DESC1 d;
                    if (adapter.GetDesc1(out d) == 0 && (d.Flags & 2) == 0)  // 2 = software adapter
                    {
                        GpuAdapter a = new GpuAdapter();
                        a.Name = d.Description;
                        a.Luid = string.Format("luid_0x{0:X8}_0x{1:X8}", d.LuidHigh, d.LuidLow);
                        a.DedicatedBytes = d.DedicatedVideoMemory.ToUInt64();
                        // TopBar runs as a 32-bit process, where DXGI's SIZE_T
                        // sizes come back cut short (a 16 GB card reads 3 GB).
                        // The driver's registry entry has the real size.
                        double real = RegistryVram(d.VendorId, d.DeviceId);
                        if (real > 0) a.DedicatedBytes = real;
                        list.Add(a);
                    }
                    Marshal.ReleaseComObject(adapter);
                }
            }
            catch (Exception) { }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            return list;
        }

        // Video memory from the display driver's class key, matched on PCI
        // vendor and device. Stale keys of a card that was swapped out carry
        // the same device id and size, so any match will do.
        private static double RegistryVram(uint vendor, uint device)
        {
            string needle = string.Format("ven_{0:x4}&dev_{1:x4}", vendor, device);
            double best = 0;
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;          // 0000, 0001, ... - not "Properties"
                        try
                        {
                            using (RegistryKey k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string id = k.GetValue("MatchingDeviceId") as string ?? "";
                                if (id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                object v = k.GetValue("HardwareInformation.qwMemorySize");
                                if (v is long) best = Math.Max(best, (long)v);
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return best;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Only the slots up to the methods we call matter: COM dispatches by vtable
    // position, so the unused ones are placeholders that are never invoked.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        void SetPrivateData_(); void SetPrivateDataInterface_(); void GetPrivateData_(); void GetParent_();
        void EnumAdapters_(); void MakeWindowAssociation_(); void GetWindowAssociation_();
        void CreateSwapChain_(); void CreateSoftwareAdapter_();
        [PreserveSig]
        int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        void SetPrivateData_(); void SetPrivateDataInterface_(); void GetPrivateData_(); void GetParent_();
        void EnumOutputs_(); void GetDesc_(); void CheckInterfaceSupport_();
        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
