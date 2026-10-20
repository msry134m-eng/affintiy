using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Affinity;

// Order of this enum = assignment priority (best cores go to the first kinds)
enum Kind { Gpu, Usb, Audio, Storage, Network }

record Dev(Kind Kind, string Name, string Id, int Irq, bool LimitMsg);
record Saved(string Key, string Name, string? Type, string? Data);
record CoreScore(int Cpu, double StolenUsPerSec, double MaxGapUs)
{
    // lower = quieter core
    public double Score => StolenUsPerSec + MaxGapUs;
}

static class Program
{
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr mask);

    static readonly string BackupFile = Path.Combine(AppContext.BaseDirectory, "affinity-backup.json");
    static readonly List<Saved> Backup = new();
    static bool DryRun;

    static int Main(string[] args)
    {
        bool pause = !args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase);

        // also catches crashes on background threads, which try/catch below can't
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine("\nERROR: " + e.ExceptionObject);
            if (pause) WaitForExit();
            Environment.Exit(1);
        };

        int code;
        try { code = Run(args); }
        catch (Exception ex)
        {
            Console.WriteLine("\nERROR: " + ex);
            code = 1;
        }

        if (pause) WaitForExit();
        return code;
    }

    static void WaitForExit()
    {
        Console.WriteLine("\nPress Enter to close this window...");
        try
        {
            while (Console.KeyAvailable) Console.ReadKey(true); // drop keys pressed during the run
            Console.ReadLine();
        }
        catch { Thread.Sleep(Timeout.Infinite); }
    }

    static int Run(string[] args)
    {
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        DryRun = Has("--dry-run");
        bool allowCore0 = Has("--allow-core0");
        bool noBench = Has("--no-bench");
        int benchMs = 1500;
        var ms = args.FirstOrDefault(a => a.StartsWith("--bench-ms=", StringComparison.OrdinalIgnoreCase));
        if (ms != null && int.TryParse(ms.Split('=')[1], out var v) && v >= 200) benchMs = v;

        Console.WriteLine("Auto Affinity - interrupt affinity + MSI optimizer");
        Console.WriteLine("==================================================");

        if (Has("--restore")) return Restore();

        // ---- CPU info ----
        var (logical, physical, ht) = GetCpu();
        int perCore = ht ? Math.Max(1, logical / physical) : 1;
        Console.WriteLine($"CPU Info: Logical={logical}, Physical={physical}, HT/SMT={ht}");

        // ---- Devices ----
        Console.WriteLine("Calculating IRQ counts...");
        var devices = FindDevices();
        foreach (var g in devices.GroupBy(d => d.Kind))
            Console.WriteLine($"Found {g.Count()} {g.Key} device(s)");
        if (devices.Count == 0) { Console.WriteLine("Nothing to optimize."); return 0; }

        // ---- Benchmark ----
        var scores = new CoreScore[logical];
        if (noBench)
        {
            for (int i = 0; i < logical; i++) scores[i] = new CoreScore(i, 0, 0);
        }
        else
        {
            Console.WriteLine($"\nBenchmarking cores ({benchMs} ms x 3 rounds each). Close other apps and don't touch the PC...");
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            for (int cpu = 0; cpu < logical; cpu++)
            {
                var rounds = new List<CoreScore>();
                for (int r = 0; r < 3; r++) rounds.Add(Probe(cpu, benchMs));
                var med = rounds.OrderBy(x => x.Score).ElementAt(1); // median of 3
                scores[cpu] = med;
                Console.WriteLine($"  CPU {cpu,2}: stolen={med.StolenUsPerSec,9:F1} us/s  maxGap={med.MaxGapUs,8:F1} us  score={med.Score,9:F1}");
            }
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
        }

        // ---- Pick one representative logical CPU per physical core (the quieter sibling) ----
        var groups = Enumerable.Range(0, logical).GroupBy(c => c / perCore).ToList();
        // Default: use the FIRST thread of each physical core (0,2,4,...) and leave the SMT sibling alone.
        // --allow-smt-threads lets it pick whichever sibling benchmarked quieter instead.
        bool allowSmt = Has("--allow-smt-threads");
        var reps = groups
            .Select(g => allowSmt
                ? g.Select(c => scores[c]).OrderBy(s => s.Score).First()
                : scores[g.First()])
            .OrderBy(s => s.Score).ToList();

        // Core 0 handles a lot of system interrupts/DPCs; keep it free when we have enough cores
        if (!allowCore0 && reps.Count >= 4)
            reps = reps.Where(s => s.Cpu / perCore != 0).ToList();

        Console.WriteLine("\nBest cores (quietest first): " + string.Join(", ", reps.Select(r => r.Cpu)));

        // ---- Assign + apply ----
        var plan = new List<(Dev dev, int cpu)>();
        int idx = 0;
        // one core per device TYPE (GPU, USB, Audio, Storage, Network); devices of the same type share it
        foreach (var grp in devices.GroupBy(d => d.Kind).OrderBy(g => g.Key))
        {
            int cpu = reps[idx++ % reps.Count].Cpu;
            foreach (var d in grp.OrderBy(d => d.Name))
                plan.Add((d, cpu));
        }

        Console.WriteLine();
        foreach (var (dev, cpu) in plan)
        {
            Console.WriteLine($"Optimizing device: {dev.Kind} ({dev.Name})");
            try
            {
                Apply(dev, 1UL << cpu);
                Console.WriteLine($"Set {dev.Name}: IRQs={dev.Irq}, Affinity={cpu}, MSI=Enabled, Priority=High");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAILED: {ex.Message}");
            }
        }

        if (!DryRun) SaveBackup();

        Console.WriteLine("\nOptimization complete. Affinity assignments:");
        foreach (var (dev, cpu) in plan) Console.WriteLine($"{dev.Kind} ({dev.Name}) -> {cpu}");
        Console.WriteLine(DryRun
            ? "\n[dry run] nothing was written."
            : "\nA system restart is required for changes to take effect.\nUndo anytime with:  affinity.exe --restore");
        return 0;
    }

    // ------------------------------------------------------------------ CPU
    static (int logical, int physical, bool ht) GetCpu()
    {
        int logical = Math.Min(Environment.ProcessorCount, 64); // single processor group only
        int physical = 0;
        foreach (var o in Wmi("select NumberOfCores from Win32_Processor"))
            physical += Convert.ToInt32(o["NumberOfCores"]);
        if (physical <= 0 || physical > logical) physical = logical;
        return (logical, physical, logical > physical);
    }

    // ------------------------------------------------------------------ Devices
    static List<Dev> FindDevices()
    {
        var inputControllers = InputUsbControllers();
        var list = new List<Dev>();

        foreach (var e in Wmi("select Name,PNPDeviceID,HardwareID from Win32_PnPEntity"))
        {
            var id = e["PNPDeviceID"] as string;
            if (id == null || !id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;

            var cls = Classify(e["HardwareID"] as string[]);
            if (cls == null) continue;
            var (kind, limit) = cls.Value;

            // only USB controllers that actually have your mouse/keyboard on them
            if (kind == Kind.Usb && !inputControllers.Contains(id)) continue;

            var name = (e["Name"] as string) ?? id;
            list.Add(new Dev(kind, name, id, CountIrq(id), limit));
        }
        return list;
    }

    // Uses the PCI class code that Windows puts in the hardware IDs (&CC_xxxx)
    static (Kind, bool)? Classify(string[]? hw)
    {
        if (hw == null) return null;
        foreach (var h in hw)
        {
            int i = h.IndexOf("&CC_", StringComparison.OrdinalIgnoreCase);
            if (i < 0 || h.Length < i + 8) continue;
            switch (h.Substring(i + 4, 4).ToUpperInvariant())
            {
                case "0300": case "0302": return (Kind.Gpu, true);
                case "0403": return (Kind.Audio, true);
                case "0200": case "0280": return (Kind.Network, false); // leave RSS queues alone
                case "0C03": return (Kind.Usb, true);
                case "0106": return (Kind.Storage, true);               // SATA AHCI
                case "0108": return (Kind.Storage, false);              // NVMe (multi-queue, don't cap)
            }
        }
        return null;
    }

    static HashSet<string> InputUsbControllers()
    {
        var vidpid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase);

        foreach (var e in Wmi("select PNPDeviceID from Win32_PnPEntity where PNPClass='Mouse' or PNPClass='Keyboard'"))
        {
            var m = rx.Match(e["PNPDeviceID"] as string ?? "");
            if (m.Success) vidpid.Add(m.Value);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in Wmi("select Antecedent,Dependent from Win32_USBControllerDevice"))
        {
            var dep = Unquote((string)l["Dependent"]);
            var m = rx.Match(dep);
            if (m.Success && vidpid.Contains(m.Value))
                result.Add(Unquote((string)l["Antecedent"]));
        }
        return result;
    }

    static int CountIrq(string id)
    {
        try
        {
            string escaped = id.Replace(@"\", @"\\");
            string q = "associators of {Win32_PnPEntity.DeviceID='" + escaped + "'} where ResultClass=Win32_IRQResource";
            return Math.Max(1, Wmi(q).Count);
        }
        catch { return 1; }
    }

    static string Unquote(string wmiPath)
    {
        int a = wmiPath.IndexOf('"'), b = wmiPath.LastIndexOf('"');
        return wmiPath.Substring(a + 1, b - a - 1).Replace(@"\\", @"\");
    }

    static List<ManagementObject> Wmi(string query)
    {
        using var s = new ManagementObjectSearcher(query);
        return s.Get().Cast<ManagementObject>().ToList();
    }

    // ------------------------------------------------------------------ Benchmark
    // Pins a spinning thread to one logical CPU and measures how often/how long it gets
    // interrupted (ISRs, DPCs, other kernel work). Quieter core = lower score.
    static CoreScore Probe(int cpu, int ms)
    {
        CoreScore? result = null;
        var t = new Thread(() =>
        {
            SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
            long freq = Stopwatch.Frequency;
            long threshold = freq / 200_000;           // gaps > 5 us count as "stolen"
            long start = Stopwatch.GetTimestamp();
            long end = start + freq * ms / 1000;
            long prev = start, stolen = 0, max = 0;

            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                long gap = now - prev;
                prev = now;
                if (gap > threshold) { stolen += gap; if (gap > max) max = gap; }
                if (now >= end) break;
            }

            double seconds = (prev - start) / (double)freq;
            result = new CoreScore(cpu, stolen * 1e6 / freq / seconds, max * 1e6 / freq);
        });
        t.Priority = ThreadPriority.Highest;
        t.Start();
        t.Join();
        return result!;
    }

    // ------------------------------------------------------------------ Registry
    static void Apply(Dev d, ulong mask)
    {
        string root = $@"SYSTEM\CurrentControlSet\Enum\{d.Id}\Device Parameters\Interrupt Management";
        string msi = root + @"\MessageSignaledInterruptProperties";
        string aff = root + @"\Affinity Policy";

        Set(msi, "MSISupported", 1, RegistryValueKind.DWord);
        if (d.LimitMsg) Set(msi, "MessageNumberLimit", 1, RegistryValueKind.DWord);

        Set(aff, "DevicePolicy", 4, RegistryValueKind.DWord);               // IrqPolicySpecifiedProcessors
        Set(aff, "AssignmentSetOverride", BitConverter.GetBytes(mask), RegistryValueKind.Binary);
        Set(aff, "DevicePriority", 3, RegistryValueKind.DWord);             // High
    }

    static void Set(string subKey, string name, object value, RegistryValueKind kind)
    {
        if (DryRun) return;
        using var k = Registry.LocalMachine.CreateSubKey(subKey, true)
                      ?? throw new Exception("cannot open " + subKey);

        if (!Backup.Any(b => b.Key == subKey && b.Name == name))
        {
            object? old = k.GetValue(name, null);
            Backup.Add(new Saved(subKey, name,
                old == null ? null : k.GetValueKind(name).ToString(),
                old == null ? null : old is byte[] bytes ? Convert.ToBase64String(bytes) : old.ToString()));
        }
        k.SetValue(name, value, kind);
    }

    static void SaveBackup()
    {
        // merge with an existing backup so the ORIGINAL values are never overwritten by a re-run
        var all = new List<Saved>();
        if (File.Exists(BackupFile))
            all = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(BackupFile)) ?? new();
        foreach (var b in Backup)
            if (!all.Any(a => a.Key == b.Key && a.Name == b.Name)) all.Add(b);
        File.WriteAllText(BackupFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nBackup of original values saved to: {BackupFile}");
    }

    static int Restore()
    {
        if (!File.Exists(BackupFile)) { Console.WriteLine("No backup file found."); return 1; }
        var all = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(BackupFile)) ?? new();
        foreach (var s in all)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(s.Key, true);
                if (k == null) continue;
                if (s.Type == null) k.DeleteValue(s.Name, false);
                else if (s.Type == "Binary") k.SetValue(s.Name, Convert.FromBase64String(s.Data!), RegistryValueKind.Binary);
                else k.SetValue(s.Name, int.Parse(s.Data!), RegistryValueKind.DWord);
            }
            catch (Exception ex) { Console.WriteLine($"  could not restore {s.Key}\\{s.Name}: {ex.Message}"); }
        }
        Console.WriteLine("Original values restored. Restart your PC.");
        return 0;
    }
}
