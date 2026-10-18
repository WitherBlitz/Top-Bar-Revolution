// Network PCs. Every TopBar Extras on the LAN broadcasts its CPU, RAM and GPU
// usage as one small UDP packet a second and listens for everyone else's, so
// PCs find each other just by being on the same network - nothing to set up.
// The first time a PC is heard, its GPUs are added to the left of the bar
// (once: remove them and they stay removed). What has been heard goes to
// peers.json for the Settings window.
//
// Config - "extras" -> "lan", all optional:
//   "share":    true     broadcast this PC's usage
//   "listen":   true     hear the other PCs
//   "auto-add": true     put a newly heard PC's GPUs on the bar
//   "name":     ""       this PC's name on the other bars (default: its computer name)
//   "port":     51736    UDP
//   "interval": 1000     ms between broadcasts
//   "known":    [...]    PCs heard before, so auto-add happens once per PC
//
// Only senders on private (LAN) addresses are heard, packets are size-capped
// and every field is clamped, so a stray or hostile packet can do no more than
// show a made-up number.
//
// Wake-on-LAN: every PC heard is remembered in known-pcs.json - its name, the
// network card addresses (MACs) it reports, the one ARP finds behind the
// address it sends from, and its parts - so it can be woken while it's off,
// even after TopBar restarts. Wake() sends the standard magic packet (6 x FF,
// then the MAC 16 times) as UDP broadcasts to ports 9 and 7.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TopBarExtras
{
    internal sealed class RemoteGpu
    {
        public string Name = "GPU", Short = "GPU";
        public double Load, VramUsed, VramTotal;
        public List<TopRow> Top;                        // null if the PC doesn't share it
    }

    // A PC as last heard. Never changed once published - a newer packet
    // replaces the whole object - so widgets read it without locking.
    internal sealed class Peer
    {
        public string Id = "", Name = "", Address = "", CpuName = "";
        public double CpuLoad, RamUsed, RamTotal;
        public int Interval = 1000;
        public DateTime Seen;                           // UTC
        public List<RemoteGpu> Gpus = new List<RemoteGpu>();
        public List<TopRow> TopCpu, TopRam;             // biggest users (RAM in GB); null if not shared

        public bool Online
        {
            get { return (DateTime.UtcNow - Seen).TotalMilliseconds < Math.Max(5000, Interval * 4); }
        }

        public Peer Gone()
        {
            Peer p = (Peer)MemberwiseClone();
            p.Seen = DateTime.MinValue;
            return p;
        }

        public JObject ToJson()
        {
            JObject o = new JObject();
            o["id"] = Id;
            o["name"] = Name;
            o["address"] = Address;
            o["online"] = Online;
            o["age"] = Seen == DateTime.MinValue ? -1 : Math.Round((DateTime.UtcNow - Seen).TotalSeconds, 1);
            JObject cpu = new JObject();
            cpu["name"] = CpuName;
            cpu["load"] = Math.Round(CpuLoad, 1);
            o["cpu"] = cpu;
            JObject ram = new JObject();
            ram["used"] = Math.Round(RamUsed, 2);
            ram["total"] = Math.Round(RamTotal, 2);
            o["ram"] = ram;
            JArray gpus = new JArray();
            foreach (RemoteGpu g in Gpus)
            {
                JObject j = new JObject();
                j["name"] = g.Name;
                j["short"] = g.Short;
                j["load"] = Math.Round(g.Load, 1);
                j["vram_used"] = Math.Round(g.VramUsed, 2);
                j["vram_total"] = Math.Round(g.VramTotal, 2);
                gpus.Add(j);
            }
            o["gpus"] = gpus;
            return o;
        }
    }

    // What's remembered of a PC between sightings.
    internal sealed class Known
    {
        public string Id = "", Name = "", Address = "", Mask = "", Cpu = "";
        public List<string> Macs = new List<string>();
        public List<string> Gpus = new List<string>();
        public DateTime Seen;                           // UTC

        public Known Copy()
        {
            Known k = (Known)MemberwiseClone();
            k.Macs = new List<string>(Macs);
            k.Gpus = new List<string>(Gpus);
            return k;
        }
    }

    internal static class Lan
    {
        public const int DefaultPort = 51736;
        public const string RemoteType = "TopBarExtras.Widgets.RemoteStat";
        private const string AppTag = "TopBarExtras";
        private const string SepType = "TopBar.Widgets.LineSpacer";
        private const string GroupType = "TopBarExtras.Widgets.Group";
        private const string EngineType = "TopBarExtras.Widgets.Engine";
        private const int MaxPeers = 32, MaxGpus = 8, MaxPacket = 8192;

        private static readonly object gate = new object();
        private static readonly object configGate = new object();
        private static readonly Dictionary<string, Peer> peers =
            new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static readonly string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TopBar");

        private static volatile bool share, listen, autoAdd;
        private static volatile int port = DefaultPort;
        private static volatile int interval = 1000;
        private static volatile string displayName = Environment.MachineName;
        private static volatile string problem = "";
        private static volatile UdpClient inbox;
        private static volatile JObject lastSelf;
        private static Thread worker;
        private static bool exitHooked;
        private static string lastLogged;

        private static readonly Dictionary<string, Known> store =
            new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> wakeSent =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static bool storeLoaded;
        private static volatile bool storeDirty;
        private const int MaxKnown = 64;
        private const double WakeWaitSeconds = 120;

        public static string Id { get { return Environment.MachineName.ToUpperInvariant(); } }
        private static string ConfigPath { get { return Path.Combine(dir, "config.json"); } }
        private static string PeersPath { get { return Path.Combine(dir, "peers.json"); } }
        private static string WantPath { get { return Path.Combine(dir, "peers.want"); } }
        private static string KnownPath { get { return Path.Combine(dir, "known-pcs.json"); } }

        // -- settings (UI thread, on every config apply) ------------------------

        public static void Configure(JObject cfg)
        {
            JObject x = cfg == null ? null : cfg["extras"] as JObject;
            JObject lan = x == null ? null : x["lan"] as JObject;
            int wantPort = (int)Cfg.Num(lan, "port", DefaultPort);
            if (wantPort < 1024 || wantPort > 65535) wantPort = DefaultPort;
            bool wantShare = Cfg.Bool(lan, "share", true);
            bool wantListen = Cfg.Bool(lan, "listen", true);
            string name = Clean(Cfg.Str(lan, "name", ""), 40);

            lock (gate)
            {
                autoAdd = Cfg.Bool(lan, "auto-add", true);
                known.Clear();
                JArray k = lan == null ? null : lan["known"] as JArray;
                if (k != null) foreach (JToken t in k) known.Add(t.ToString());
            }
            displayName = name.Length > 0 ? name : Environment.MachineName;
            interval = (int)Math.Max(500, Math.Min(10000, Cfg.Num(lan, "interval", 1000)));
            if (!storeLoaded)
            {
                storeLoaded = true;
                LoadStore();
            }

            if (share && !wantShare) SayBye();
            if (inbox != null && (!wantListen || wantPort != port)) CloseInbox();
            port = wantPort;
            share = wantShare;
            listen = wantListen;
            if (listen && inbox == null) OpenInbox();
            if (worker == null)
            {
                worker = new Thread(Loop);
                worker.IsBackground = true;
                worker.Name = "TopBarExtras network";
                worker.Start();
            }
            if (!exitHooked)
            {
                exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += delegate { if (share) SayBye(); };
            }
            string state = string.Format("network: {0} as \"{1}\", {2} on UDP {3}{4}",
                share ? "sharing" : "not sharing", displayName,
                listen ? (inbox != null ? "listening" : "can't listen") : "not listening", port,
                problem.Length > 0 ? " (" + problem + ")" : "");
            if (state != lastLogged) { Host.Log(state); lastLogged = state; }
            wake.Set();
        }

        public static Peer Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (gate)
            {
                Peer p;
                return peers.TryGetValue(id, out p) ? p : null;
            }
        }

        // -- receiving -----------------------------------------------------------

        private static void OpenInbox()
        {
            try
            {
                UdpClient u = new UdpClient(AddressFamily.InterNetwork);
                u.ExclusiveAddressUse = false;
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                inbox = u;
                Thread t = new Thread(delegate() { Receive(u); });
                t.IsBackground = true;
                t.Name = "TopBarExtras network inbox";
                t.Start();
                problem = "";
            }
            catch (Exception ex)
            {
                problem = "can't listen on UDP " + port + ": " + ex.Message;
            }
        }

        private static void CloseInbox()
        {
            UdpClient u = inbox;
            inbox = null;
            if (u != null) try { u.Close(); } catch (Exception) { }
        }

        private static void Receive(UdpClient u)
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (inbox == u)
            {
                byte[] data;
                try { data = u.Receive(ref from); }
                catch (ObjectDisposedException) { return; }
                catch (Exception)
                {
                    if (inbox != u) return;
                    Thread.Sleep(100);          // never spin on a socket in trouble
                    continue;
                }
                if (data.Length == 0 || data.Length > MaxPacket || !IsLan(from.Address)) continue;
                try { Take(JObject.Parse(Encoding.UTF8.GetString(data)), from.Address); }
                catch (Exception) { }           // not one of ours, or malformed
            }
        }

        private static void Take(JObject j, IPAddress from)
        {
            if (Cfg.Str(j, "app", "") != AppTag) return;
            string id = Clean(Cfg.Str(j, "id", ""), 64).ToUpperInvariant();
            if (id.Length == 0 || id == Id) return;
            if (Cfg.Bool(j, "bye", false))
            {
                lock (gate)
                {
                    Peer gone;
                    if (peers.TryGetValue(id, out gone)) peers[id] = gone.Gone();
                }
                return;
            }

            Peer p = new Peer();
            p.Id = id;
            p.Name = Clean(Cfg.Str(j, "name", id), 40);
            if (p.Name.Length == 0) p.Name = id;
            p.Address = from.ToString();
            p.Interval = (int)Clamp(Cfg.Num(j, "interval", 1000), 250, 60000);
            JObject cpu = j["cpu"] as JObject;
            p.CpuName = Clean(Cfg.Str(cpu, "name", ""), 48);
            p.CpuLoad = Clamp(Cfg.Num(cpu, "load", 0), 0, 100);
            JObject ram = j["ram"] as JObject;
            p.RamTotal = Clamp(Cfg.Num(ram, "total", 0), 0, 65536);
            p.RamUsed = Clamp(Cfg.Num(ram, "used", 0), 0, p.RamTotal);
            JArray gpus = j["gpus"] as JArray;
            if (gpus != null)
            {
                foreach (JToken t in gpus.Take(MaxGpus))
                {
                    JObject g = t as JObject;
                    if (g == null) continue;
                    RemoteGpu r = new RemoteGpu();
                    r.Name = Clean(Cfg.Str(g, "name", "GPU"), 64);
                    r.Short = Clean(Cfg.Str(g, "short", ""), 40);
                    if (r.Short.Length == 0) r.Short = GpuAdapter.Short(r.Name);
                    r.Load = Clamp(Cfg.Num(g, "load", 0), 0, 100);
                    r.VramTotal = Clamp(Cfg.Num(g, "vram_total", 0), 0, 1024);
                    r.VramUsed = Clamp(Cfg.Num(g, "vram_used", 0), 0, r.VramTotal);
                    r.Top = Tops(g["top"], 100);
                    p.Gpus.Add(r);
                }
            }
            JObject top = j["top"] as JObject;
            if (top != null)
            {
                p.TopCpu = Tops(top["cpu"], 100) ?? new List<TopRow>();
                p.TopRam = Tops(top["ram"], 65536) ?? new List<TopRow>();
            }
            p.Seen = DateTime.UtcNow;
            List<string> macs = new List<string>();
            JArray reported = j["macs"] as JArray;
            if (reported != null)
            {
                foreach (JToken t in reported.Take(8))
                {
                    string mac = Mac(Cfg.Str(t as JObject, "mac", ""));
                    if (mac != null && !macs.Contains(mac)) macs.Add(mac);
                }
            }
            bool lookUp;
            lock (gate)
            {
                if (!peers.ContainsKey(id) && peers.Count >= MaxPeers)
                    peers.Remove(peers.OrderBy(kv => kv.Value.Seen).First().Key);
                peers[id] = p;
                lookUp = Remember(p, macs);
            }
            // The card it actually talks through, found by ARP - so even a PC
            // that doesn't report its MACs can be woken.
            if (lookUp)
            {
                IPAddress at = from;
                ThreadPool.QueueUserWorkItem(delegate { LearnMac(id, at); });
            }
        }

        // -- Wake-on-LAN -----------------------------------------------------------

        // Under gate. True when the PC is new or has moved address, so its MAC
        // and subnet should be looked up.
        private static bool Remember(Peer p, List<string> macs)
        {
            Known k;
            bool fresh = !store.TryGetValue(p.Id, out k);
            if (fresh)
            {
                if (store.Count >= MaxKnown)
                    store.Remove(store.OrderBy(kv => kv.Value.Seen).First().Key);
                k = new Known();
                k.Id = p.Id;
                store[p.Id] = k;
            }
            List<string> gpus = p.Gpus.Select(g => g.Name).ToList();
            bool moved = k.Address != p.Address;
            if (fresh || moved || k.Name != p.Name || k.Cpu != p.CpuName || !k.Gpus.SequenceEqual(gpus))
            {
                k.Name = p.Name;
                k.Address = p.Address;
                k.Cpu = p.CpuName;
                k.Gpus = gpus;
                storeDirty = true;
            }
            foreach (string m in macs)
            {
                if (k.Macs.Contains(m)) continue;
                k.Macs.Add(m);
                storeDirty = true;
            }
            k.Seen = p.Seen;
            return fresh || moved || k.Macs.Count == 0;
        }

        private static void LearnMac(string id, IPAddress at)
        {
            string mac = Arp(at);
            string mask = MaskFor(at);
            lock (gate)
            {
                Known k;
                if (!store.TryGetValue(id, out k)) return;
                if (mac != null && !k.Macs.Contains(mac))
                {
                    k.Macs.Insert(0, mac);              // the one it talks through goes first
                    storeDirty = true;
                }
                if (mask != null && k.Mask != mask)
                {
                    k.Mask = mask;
                    storeDirty = true;
                }
            }
        }

        public static bool CanWake(string id)
        {
            lock (gate)
            {
                Known k;
                return store.TryGetValue(id ?? "", out k) && k.Macs.Count > 0;
            }
        }

        // A wake-up was sent in the last couple of minutes and the PC hasn't
        // come back yet.
        public static bool Waking(string id)
        {
            lock (gate)
            {
                DateTime at;
                Peer p;
                return wakeSent.TryGetValue(id ?? "", out at)
                    && (DateTime.UtcNow - at).TotalSeconds < WakeWaitSeconds
                    && !(peers.TryGetValue(id, out p) && p.Online);
            }
        }

        public static string NameOf(string id)
        {
            lock (gate)
            {
                Peer p;
                if (peers.TryGetValue(id ?? "", out p) && p.Name.Length > 0) return p.Name;
                Known k;
                if (store.TryGetValue(id ?? "", out k) && k.Name.Length > 0) return k.Name;
            }
            return id ?? "";
        }

        public static string CpuOf(string id)
        {
            lock (gate)
            {
                Known k;
                return store.TryGetValue(id ?? "", out k) ? k.Cpu : "";
            }
        }

        public static bool IsOnline(string id)
        {
            lock (gate)
            {
                Peer p;
                return peers.TryGetValue(id ?? "", out p) && p.Online;
            }
        }

        // Every PC remembered, by name - for the bar's Wake up menu.
        public static List<Known> KnownPcs()
        {
            lock (gate)
                return store.Values.Select(k => k.Copy())
                            .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool Wake(string id)
        {
            Known k;
            lock (gate)
            {
                if (!store.TryGetValue(id ?? "", out k) || k.Macs.Count == 0) return false;
                k = k.Copy();
                wakeSent[k.Id] = DateTime.UtcNow;
            }
            Host.Log("network: waking " + k.Name + " (" + string.Join(", ", k.Macs.ToArray()) + ")");
            ThreadPool.QueueUserWorkItem(delegate { SendWake(k.Macs, k.Address, k.Mask); });
            return true;
        }

        // Magic packets for each MAC, three rounds, to the PC's own subnet and
        // every one this PC is on, on ports 9 and 7.
        public static void SendWake(IList<string> macs, string address, string mask)
        {
            List<IPEndPoint> targets = new List<IPEndPoint>();
            HashSet<string> seen = new HashSet<string>();
            Action<IPAddress> add = delegate(IPAddress a)
            {
                foreach (int p in new[] { 9, 7 })
                    if (seen.Add(a + ":" + p)) targets.Add(new IPEndPoint(a, p));
            };
            IPAddress ip, m;
            if (IPAddress.TryParse(address ?? "", out ip) && IPAddress.TryParse(mask ?? "", out m))
                add(BroadcastOf(ip, m));
            foreach (IPEndPoint e in Broadcasts(9)) add(e.Address);
            add(IPAddress.Broadcast);
            try
            {
                using (UdpClient u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.EnableBroadcast = true;
                    for (int round = 0; round < 3; round++)
                    {
                        foreach (string mac in macs)
                        {
                            byte[] packet = MagicPacket(mac);
                            if (packet == null) continue;
                            foreach (IPEndPoint t in targets)
                            {
                                try { u.Send(packet, packet.Length, t); }
                                catch (Exception) { }
                            }
                        }
                        Thread.Sleep(150);
                    }
                }
            }
            catch (Exception ex) { Host.Log("network: wake failed: " + ex.Message); }
        }

        public static byte[] MagicPacket(string mac)
        {
            string hex = Mac(mac);
            if (hex == null) return null;
            hex = hex.Replace("-", "");
            byte[] m = new byte[6];
            for (int i = 0; i < 6; i++) m[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            byte[] packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int r = 0; r < 16; r++) Buffer.BlockCopy(m, 0, packet, 6 + r * 6, 6);
            return packet;
        }

        // "aa:bb:cc:dd:ee:ff" in any common spelling -> "AA-BB-CC-DD-EE-FF";
        // null for anything that isn't a usable unicast MAC.
        private static string Mac(string s)
        {
            string hex = Regex.Replace(s ?? "", "[^0-9A-Fa-f]", "").ToUpperInvariant();
            if (hex.Length != 12 || hex == "000000000000" || hex == "FFFFFFFFFFFF") return null;
            if ((Convert.ToByte(hex.Substring(0, 2), 16) & 1) != 0) return null;   // multicast
            return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)).ToArray());
        }

        private static string Arp(IPAddress ip)
        {
            try
            {
                byte[] mac = new byte[6];
                uint len = 6;
                if (SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) == 0 && len == 6)
                    return Mac(BitConverter.ToString(mac, 0, 6));
            }
            catch (Exception) { }
            return null;
        }

        // The mask of this PC's own subnet that the address is on.
        private static string MaskFor(IPAddress ip)
        {
            try
            {
                byte[] a = ip.GetAddressBytes();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] mine = ua.Address.GetAddressBytes(), m = ua.IPv4Mask.GetAddressBytes();
                        bool same = true;
                        for (int i = 0; i < 4 && same; i++) same = (mine[i] & m[i]) == (a[i] & m[i]);
                        if (same) return ua.IPv4Mask.ToString();
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static IPAddress BroadcastOf(IPAddress ip, IPAddress mask)
        {
            byte[] a = ip.GetAddressBytes(), m = mask.GetAddressBytes(), b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            return new IPAddress(b);
        }

        private static void LoadStore()
        {
            JObject all = ReadJson(KnownPath);
            if (all == null) return;
            lock (gate)
            {
                foreach (KeyValuePair<string, JToken> kv in all)
                {
                    JObject o = kv.Value as JObject;
                    if (o == null || store.Count >= MaxKnown) continue;
                    Known k = new Known();
                    k.Id = kv.Key.ToUpperInvariant();
                    k.Name = Clean(Cfg.Str(o, "name", k.Id), 40);
                    k.Address = Cfg.Str(o, "address", "");
                    k.Mask = Cfg.Str(o, "mask", "");
                    k.Cpu = Clean(Cfg.Str(o, "cpu", ""), 48);
                    JArray macs = o["macs"] as JArray;
                    if (macs != null)
                        foreach (JToken t in macs)
                        {
                            string mac = Mac(t.ToString());
                            if (mac != null && !k.Macs.Contains(mac)) k.Macs.Add(mac);
                        }
                    JArray gpus = o["gpus"] as JArray;
                    if (gpus != null) foreach (JToken t in gpus) k.Gpus.Add(Clean(t.ToString(), 64));
                    // Json.NET has already turned the ISO string into a date
                    // (UTC, from its "Z"); going through text again would read
                    // it back as local time and shift it by the UTC offset.
                    JToken seenToken = o["seen"];
                    DateTime seen;
                    if (seenToken != null && seenToken.Type == JTokenType.Date)
                        k.Seen = ((DateTime)seenToken).ToUniversalTime();
                    else if (seenToken != null && DateTime.TryParse(seenToken.ToString(),
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 System.Globalization.DateTimeStyles.RoundtripKind
                                 | System.Globalization.DateTimeStyles.AdjustToUniversal, out seen))
                        k.Seen = seen;
                    store[k.Id] = k;
                }
            }
        }

        private static void SaveStore()
        {
            JObject all = new JObject();
            lock (gate)
            {
                foreach (Known k in store.Values.OrderBy(q => q.Id))
                {
                    JObject o = new JObject();
                    o["name"] = k.Name;
                    o["address"] = k.Address;
                    o["mask"] = k.Mask;
                    o["macs"] = new JArray(k.Macs.ToArray());
                    o["cpu"] = k.Cpu;
                    o["gpus"] = new JArray(k.Gpus.ToArray());
                    o["seen"] = k.Seen == DateTime.MinValue ? "" : k.Seen.ToString("o");
                    all[k.Id] = o;
                }
                storeDirty = false;
            }
            try { WriteAtomic(KnownPath, all.ToString(Formatting.Indented)); }
            catch (Exception) { storeDirty = true; }
        }

        // [[name, value, processes], ...] - what a PC shares of its biggest users.
        private static List<TopRow> Tops(JToken t, double max)
        {
            JArray list = t as JArray;
            if (list == null) return null;
            List<TopRow> rows = new List<TopRow>();
            foreach (JToken e in list.Take(8))
            {
                JArray r = e as JArray;
                if (r == null || r.Count < 2) continue;
                TopRow row = new TopRow();
                row.Name = Clean(r[0].ToString(), 40);
                row.Value = Clamp(Cfg.Num(r[1], 0), 0, max);
                row.Count = (int)Clamp(r.Count > 2 ? Cfg.Num(r[2], 1) : 1, 1, 9999);
                if (row.Name.Length > 0) rows.Add(row);
            }
            return rows;
        }

        // Private ranges only: 10/8, 172.16/12, 192.168/16, link-local, loopback.
        private static bool IsLan(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        // -- sending, and the rest of the housekeeping ----------------------------

        private static void Loop()
        {
            LocalStats local = null;
            UdpClient outbox = null;
            List<IPEndPoint> targets = null;
            int targetsPort = 0;
            DateTime targetsAt = DateTime.MinValue, fileAt = DateTime.MinValue;
            string lastState = null;
            while (true)
            {
                try
                {
                    if (share)
                    {
                        if (local == null) local = new LocalStats();
                        JObject me = local.Sample();
                        me["app"] = AppTag;
                        me["v"] = 1;
                        me["id"] = Id;
                        me["name"] = displayName;
                        me["interval"] = interval;
                        lastSelf = me;
                        if (outbox == null)
                        {
                            outbox = new UdpClient(AddressFamily.InterNetwork);
                            outbox.EnableBroadcast = true;
                        }
                        // Adapters come and go (VPNs, docks); look again now and then.
                        if (targets == null || targetsPort != port || (DateTime.UtcNow - targetsAt).TotalSeconds > 30)
                        {
                            targets = Broadcasts(port);
                            targetsPort = port;
                            targetsAt = DateTime.UtcNow;
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(me.ToString(Formatting.None));
                        foreach (IPEndPoint t in targets)
                        {
                            try { outbox.Send(bytes, bytes.Length, t); }
                            catch (Exception) { }
                        }
                    }
                    if (listen) AutoAdd();

                    // peers.json: every 2s while Settings is showing it (Settings
                    // touches peers.want), otherwise when a PC comes or goes, and
                    // once a minute - no point rewriting a file nobody reads.
                    string state = State();
                    double since = (DateTime.UtcNow - fileAt).TotalSeconds;
                    if (state != lastState || since >= 60 || (since >= 2 && Wanted()))
                    {
                        // A PC coming or going also updates when it was last seen.
                        if (state != lastState || storeDirty) SaveStore();
                        WritePeers();
                        lastState = state;
                        fileAt = DateTime.UtcNow;
                    }
                    else if (storeDirty) SaveStore();
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                }
                wake.WaitOne(interval);
            }
        }

        // One directed broadcast per LAN the PC is on - e.g. 192.168.1.255.
        // /31 and /32 links (Tailscale and other VPNs) have no broadcast.
        private static List<IPEndPoint> Broadcasts(int toPort)
        {
            List<IPEndPoint> list = new List<IPEndPoint>();
            HashSet<string> seen = new HashSet<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        if (!IsLan(ua.Address)) continue;
                        byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask[0] == 255 && mask[1] == 255 && mask[2] == 255 && mask[3] >= 254) continue;
                        byte[] b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        IPAddress bc = new IPAddress(b);
                        if (seen.Add(bc.ToString())) list.Add(new IPEndPoint(bc, toPort));
                    }
                }
            }
            catch (Exception) { }
            if (list.Count == 0) list.Add(new IPEndPoint(IPAddress.Broadcast, toPort));
            return list;
        }

        private static void SayBye()
        {
            try
            {
                JObject bye = new JObject();
                bye["app"] = AppTag;
                bye["v"] = 1;
                bye["id"] = Id;
                bye["bye"] = true;
                byte[] bytes = Encoding.UTF8.GetBytes(bye.ToString(Formatting.None));
                using (UdpClient u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.EnableBroadcast = true;
                    foreach (IPEndPoint t in Broadcasts(port))
                    {
                        try { u.Send(bytes, bytes.Length, t); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        private static string State()
        {
            StringBuilder b = new StringBuilder();
            b.Append(share).Append(listen).Append(inbox != null).Append(displayName).Append(problem);
            lock (gate)
                foreach (Peer p in peers.Values.OrderBy(q => q.Id))
                    b.Append('|').Append(p.Id).Append(p.Name).Append(p.Online).Append(p.Gpus.Count);
            return b.ToString();
        }

        private static bool Wanted()
        {
            try
            {
                FileInfo f = new FileInfo(WantPath);
                return f.Exists && (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 10;
            }
            catch (Exception) { return false; }
        }

        private static void WritePeers()
        {
            JObject o = new JObject();
            o["updated"] = DateTime.UtcNow.ToString("o");
            o["id"] = Id;
            o["name"] = displayName;
            o["port"] = port;
            o["sharing"] = share;
            o["listening"] = listen && inbox != null;
            o["problem"] = problem;
            JObject me = lastSelf;
            if (share && me != null) o["self"] = me;
            JArray list = new JArray();
            lock (gate)
                foreach (Peer p in peers.Values.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase))
                    list.Add(p.ToJson());
            o["peers"] = list;
            try { WriteAtomic(PeersPath, o.ToString(Formatting.Indented)); }
            catch (Exception) { }
        }

        // -- first sight: put the new PC's GPUs on the bar ----------------------------

        private static void AutoAdd()
        {
            List<Peer> fresh = new List<Peer>();
            lock (gate)
            {
                if (!autoAdd) return;
                foreach (Peer p in peers.Values)
                    if (p.Online && !known.Contains(p.Id)) fresh.Add(p);
                // Once per PC, even if the write below fails.
                foreach (Peer p in fresh) known.Add(p.Id);
            }
            if (fresh.Count > 0) AddToBar(fresh);
        }

        // Each GPU gets its own pill at the end of the left side, styled like
        // this bar's own GPU readout. The PC is remembered in "known" either
        // way, so taking the readings off again sticks.
        private static void AddToBar(List<Peer> newcomers)
        {
            lock (configGate)
            {
                JObject cfg = null;
                for (int i = 0; i < 5 && cfg == null; i++)
                {
                    cfg = ReadJson(ConfigPath);
                    if (cfg == null) Thread.Sleep(150);
                }
                JArray ws = cfg == null ? null : cfg["widgets"] as JArray;
                if (ws == null) return;
                JObject x = cfg["extras"] as JObject;
                if (x == null) { x = new JObject(); cfg["extras"] = x; }
                JObject lan = x["lan"] as JObject;
                if (lan == null) { lan = new JObject(); x["lan"] = lan; }
                JArray knownList = lan["known"] as JArray;
                if (knownList == null) { knownList = new JArray(); lan["known"] = knownList; }

                List<JObject> add = new List<JObject>();
                foreach (Peer p in newcomers)
                {
                    if (!knownList.Any(t => string.Equals(t.ToString(), p.Id, StringComparison.OrdinalIgnoreCase)))
                        knownList.Add(p.Id);
                    if (Shows(ws, p.Id)) continue;
                    for (int g = 0; g < p.Gpus.Count; g++) add.Add(RemoteWidget(ws, p, g));
                }
                if (add.Count > 0) Insert(x, ws, add);
                WriteAtomic(ConfigPath, cfg.ToString(Formatting.Indented));
                Host.Log("network: first heard " + string.Join(", ", newcomers.Select(p => p.Name + " (" + p.Address + ")").ToArray())
                         + (add.Count > 0 ? " - put " + add.Count + " GPU reading(s) on the bar" : ""));
            }
        }

        private static bool Shows(JArray ws, string id)
        {
            foreach (JToken t in ws)
            {
                JObject w = t as JObject;
                if (w != null && Cfg.Str(w, "type", "") == RemoteType
                    && string.Equals(Cfg.Str(w["settings"] as JObject, "pc", ""), id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static JObject SettingsOf(JArray ws, string type)
        {
            foreach (JToken t in ws)
            {
                JObject w = t as JObject;
                if (w != null && Cfg.Str(w, "type", "") == type) return w["settings"] as JObject;
            }
            return null;
        }

        private static JObject RemoteWidget(JArray ws, Peer p, int gpu)
        {
            JObject s = new JObject();
            s["pc"] = p.Id;
            s["metric"] = "gpu";
            s["gpu"] = gpu;
            s["gpu-name"] = p.Gpus[gpu].Name;
            s["label"] = "auto";
            s["format"] = "{percent:0}%";
            s["value-position"] = "before";
            s["offline"] = "wake";
            JObject local = SettingsOf(ws, "TopBarExtras.Widgets.GPUUsage");
            JObject ram = SettingsOf(ws, "TopBar.Widgets.RAMUsageBar");
            JObject text = SettingsOf(ws, "TopBar.Widgets.CustomText");
            if (local != null)
            {
                foreach (string k in new[] { "color", "bar-color", "background-color", "width", "height", "radius", "value-position" })
                    if (local[k] != null) s[k] = local[k].DeepClone();
            }
            else
            {
                if (ram != null)
                {
                    if (ram["color"] != null) s["bar-color"] = ram["color"].DeepClone();
                    foreach (string k in new[] { "background-color", "width", "height", "radius" })
                        if (ram[k] != null) s[k] = ram[k].DeepClone();
                }
                if (text != null && text["color"] != null) s["color"] = text["color"].DeepClone();
            }
            JObject w = new JObject();
            w["type"] = RemoteType;
            w["settings"] = s;
            return w;
        }

        private static void Insert(JObject x, JArray ws, List<JObject> add)
        {
            JObject sepLike = null;
            foreach (JToken t in ws)
            {
                JObject w = t as JObject;
                if (w != null && Cfg.Str(w, "type", "") == SepType) { sepLike = w; break; }
            }
            Func<JObject> sep = delegate
            {
                if (sepLike != null) return (JObject)sepLike.DeepClone();
                JObject s = new JObject();
                s["color"] = "#4DFFFFFF";
                s["width"] = 1;
                s["height"] = 10;
                JObject w = new JObject();
                w["type"] = SepType;
                w["settings"] = s;
                return w;
            };
            List<JObject> run = new List<JObject>();
            for (int i = 0; i < add.Count; i++)
            {
                if (i > 0) run.Add(sep());
                run.Add(add[i]);
            }

            int engine = IndexOf(ws, w => Cfg.Str(w, "type", "") == EngineType);
            if (Cfg.Str(x, "layout", "classic") == "split")
            {
                int left = IndexOf(ws, w => Cfg.Str(w, "type", "") == GroupType
                                         && Cfg.Str(w["settings"] as JObject, "align", "right") == "left");
                if (left < 0)
                {
                    JObject gs = new JObject();
                    gs["align"] = "left";
                    JObject g = new JObject();
                    g["type"] = GroupType;
                    g["settings"] = gs;
                    run.Insert(0, g);
                    InsertAt(ws, engine + 1, run);
                    return;
                }
                int end = left + 1;
                bool content = false, endsWithSep = false;
                while (end < ws.Count && Cfg.Str(ws[end] as JObject, "type", "") != GroupType)
                {
                    endsWithSep = Cfg.Str(ws[end] as JObject, "type", "") == SepType;
                    if (!endsWithSep) content = true;
                    end++;
                }
                if (content && !endsWithSep) run.Insert(0, sep());
                InsertAt(ws, end, run);
            }
            else
            {
                // Classic layouts line everything up on the right; the far
                // left of that run is as near to "top left" as they get.
                int at = engine + 1;
                if (at < ws.Count) run.Add(sep());
                InsertAt(ws, at, run);
            }
        }

        private static int IndexOf(JArray ws, Func<JObject, bool> match)
        {
            for (int i = 0; i < ws.Count; i++)
            {
                JObject w = ws[i] as JObject;
                if (w != null && match(w)) return i;
            }
            return -1;
        }

        private static void InsertAt(JArray ws, int at, List<JObject> run)
        {
            for (int i = 0; i < run.Count; i++) ws.Insert(at + i, run[i]);
        }

        // -- files -----------------------------------------------------------------

        private static JObject ReadJson(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs))
                    return JObject.Parse(r.ReadToEnd());
            }
            catch (Exception) { return null; }
        }

        // Written whole and swapped in, so a reader never sees half a file -
        // the engine's own watcher included.
        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text + Environment.NewLine, new UTF8Encoding(false));
            for (int i = 0; i < 5; i++)
            {
                if (MoveFileEx(tmp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return;
                Thread.Sleep(60);               // a reader had it open without delete sharing
            }
            try { File.Delete(tmp); } catch (Exception) { }
        }

        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c)) b.Append(c);
            string t = b.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return lo;
            return Math.Max(lo, Math.Min(hi, v));
        }

        private const int MOVEFILE_REPLACE_EXISTING = 1;
        private const int MOVEFILE_WRITE_THROUGH = 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string from, string to, int flags);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref uint macLength);
    }

    // This PC's side of it: what goes into each broadcast.
    internal sealed class LocalStats
    {
        private const double GB = 1024.0 * 1024.0 * 1024.0;
        private readonly GpuSampler sampler = new GpuSampler();
        private readonly ProcessSampler procs = new ProcessSampler();
        private readonly PerformanceCounter cpu = CpuCounter();
        private readonly string cpuName = CpuName();
        private List<GpuAdapter> gpus;
        private JArray macs;
        private DateTime gpusAt;

        public JObject Sample()
        {
            if (gpus == null || (DateTime.UtcNow - gpusAt).TotalMinutes >= 1)
            {
                gpus = GpuAdapter.Real();
                macs = Macs();
                gpusAt = DateTime.UtcNow;
            }
            Dictionary<string, double> loads = sampler.Loads();
            Dictionary<string, double> vram = sampler.Vram();

            JObject o = new JObject();
            JObject c = new JObject();
            double load = 0;
            try { if (cpu != null) load = cpu.NextValue(); } catch (Exception) { }
            c["name"] = cpuName;
            c["load"] = Math.Round(Math.Max(0, Math.Min(100, load)), 1);
            o["cpu"] = c;

            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref m))
            {
                JObject r = new JObject();
                r["total"] = Math.Round(m.ullTotalPhys / GB, 2);
                r["used"] = Math.Round((m.ullTotalPhys - m.ullAvailPhys) / GB, 2);
                o["ram"] = r;
            }

            // What's using it all, for a pill's drop-down on the other PCs.
            procs.Sample();
            JObject top = new JObject();
            if (procs.Ready) top["cpu"] = Rows(ProcessSampler.Top(procs.Cpu, procs.Names, 6, 0.05), 1);
            top["ram"] = Rows(ProcessSampler.Top(
                procs.Ram.ToDictionary(kv => kv.Key, kv => kv.Value / GB), procs.Names, 6), 2);
            o["top"] = top;

            JArray list = new JArray();
            foreach (GpuAdapter a in gpus)
            {
                double l, v;
                loads.TryGetValue(a.Luid, out l);
                vram.TryGetValue(a.Luid, out v);
                JObject g = new JObject();
                g["name"] = a.Name;
                g["short"] = GpuAdapter.Short(a.Name);
                g["load"] = Math.Round(l, 1);
                g["vram_used"] = Math.Round(v / GB, 2);
                g["vram_total"] = Math.Round(a.DedicatedBytes / GB, 2);
                Dictionary<int, double> users;
                if (!sampler.Processes.TryGetValue(a.Luid, out users)) users = new Dictionary<int, double>();
                g["top"] = Rows(ProcessSampler.Top(users, procs.Names, 5, 0.05), 1);
                list.Add(g);
            }
            o["gpus"] = list;
            o["macs"] = macs.DeepClone();
            return o;
        }

        private static JArray Rows(List<TopRow> rows, int decimals)
        {
            JArray list = new JArray();
            foreach (TopRow r in rows)
            {
                string name = r.Name.Length > 32 ? r.Name.Substring(0, 32) : r.Name;
                list.Add(new JArray(name, Math.Round(r.Value, decimals), r.Count));
            }
            return list;
        }

        // The network cards on this PC's LANs, for the others to wake it by.
        private static JArray Macs()
        {
            JArray list = new JArray();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    byte[] mac = ni.GetPhysicalAddress().GetAddressBytes();
                    if (mac.Length != 6) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] b = ua.Address.GetAddressBytes();
                        bool lan = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                                || (b[0] == 192 && b[1] == 168);
                        if (!lan) continue;
                        JObject m = new JObject();
                        m["mac"] = BitConverter.ToString(mac);
                        m["ip"] = ua.Address.ToString();
                        m["mask"] = ua.IPv4Mask.ToString();
                        list.Add(m);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        // Task Manager's CPU figure; older Windows only has the plain one.
        private static PerformanceCounter CpuCounter()
        {
            try
            {
                PerformanceCounter p = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                p.NextValue();
                return p;
            }
            catch (Exception) { }
            try
            {
                PerformanceCounter p = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                p.NextValue();
                return p;
            }
            catch (Exception) { return null; }
        }

        // "AMD Ryzen 7 5700X3D 8-Core Processor" -> "Ryzen 7 5700X3D", the way
        // the bar's own CPU label drops the brand.
        public static string CpuName()
        {
            string full = "";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) full = (k.GetValue("ProcessorNameString") as string ?? "").Trim();
            }
            catch (Exception) { }
            return ShortCpu(full);
        }

        public static string ShortCpu(string full)
        {
            string s = Regex.Replace(full, @"\((R|TM)\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+(w/|with)\s+Radeon.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"@.*$", "");
            s = Regex.Replace(s, @"\b\d+(st|nd|rd|th)\s+Gen\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(AMD|Intel|Core|Processor|CPU|\d+-Core)\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 0 ? s : full;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
