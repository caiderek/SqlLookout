using SqlLookout.Service;

namespace SqlLookout.Tests;

public sealed class ServiceSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sqllookout-setup-{Guid.NewGuid():N}");

    public ServiceSetupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal sealed class FakeHost(string root) : ISetupHost
    {
        public bool IsAdministrator { get; set; } = true;
        public string ProgramFilesDir { get; } = Path.Combine(root, "pf");
        public string ProgramDataDir { get; } = Path.Combine(root, "pd");
        public bool Exists { get; set; }
        public string? ImagePath { get; set; }
        public bool WebAnswers { get; set; } = true;
        public bool StartThrows { get; set; }
        public Func<string, Version?> Versions { get; set; } = _ => new Version(1, 0);
        public List<string> Calls { get; } = [];
        public Func<string, int> ExitCode { get; set; } = _ => 0;
        public Action? OnStart { get; set; }

        public ISecretProtector Protector { get; } = new FakeSecretProtector();
        public string? StoreProblem { get; set; }
        public List<string> StoreChecked { get; } = [];
        public bool StoreMissing { get; set; }
        public List<bool> StoreCreate { get; } = [];
        public Task<StoreCheck> CheckStoreAsync(string connectionString, bool create)
        {
            StoreChecked.Add(connectionString);
            StoreCreate.Add(create);
            return Task.FromResult(new StoreCheck(StoreProblem, StoreProblem is null && StoreMissing));
        }

        public bool ServiceExists() => Exists;
        public string? ServiceImagePath() => ImagePath;
        public void StopService(TimeSpan timeout) => Calls.Add("stop");
        public void StartService(TimeSpan timeout)
        {
            Calls.Add("start");
            OnStart?.Invoke();
            if (StartThrows) throw new InvalidOperationException("cannot start");
        }
        public Task<bool> WaitForWebAsync(string address, int port, TimeSpan timeout)
        {
            Calls.Add($"web {address}:{port}");
            return Task.FromResult(WebAnswers);
        }
        public Version? ProgramVersion(string folder) => Versions(folder);
        public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args)
        {
            Calls.Add(file + " " + string.Join(' ', args));
            return (ExitCode(file), "");
        }
    }

    private string MakeSource(string name, string version = "1")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sqllookout.exe"), "exe-" + version);
        File.WriteAllText(Path.Combine(dir, "lib.dll"), "lib-" + version);
        return dir;
    }

    private static (SetupOptions, string) Parse(string command, params string[] rest)
    {
        var (o, e) = SetupOptionsParser.Parse(command, [command, .. rest]);
        Assert.Null(e);
        return (o!, e ?? "");
    }

    // ---- option parsing ----

    [Fact]
    public void Parser_reads_values_and_flags()
    {
        var (o, _) = Parse("install", "--port", "9443", "--listen", "127.0.0.1", "--no-firewall", "--data", "d");
        Assert.Equal(9443, o.Port);
        Assert.Equal("127.0.0.1", o.Listen);
        Assert.True(o.NoFirewall);
        Assert.Equal(Path.GetFullPath("d"), o.Data);
    }

    [Theory]
    [InlineData("install", "--bogus", "x")]
    [InlineData("install", "--port")]
    [InlineData("install", "--port", "0")]
    [InlineData("install", "--port", "70000")]
    [InlineData("install", "--listen", "not-an-ip")]
    [InlineData("install", "--account", "DOM\\svc")]
    [InlineData("install", "--password-file", "p.txt")]
    [InlineData("install", "--cert-password-file", "p.txt")]
    [InlineData("upgrade", "--dir", "x")]
    [InlineData("uninstall", "--delete-data", "--keep-data")]
    public void Parser_rejects_bad_input(string command, params string[] rest)
    {
        var (o, e) = SetupOptionsParser.Parse(command, [command, .. rest]);
        Assert.Null(o);
        Assert.False(string.IsNullOrEmpty(e));
    }

    [Fact]
    public void Managed_service_account_needs_no_password_file()
    {
        var (o, _) = Parse("install", "--account", "DOM\\svc$");
        Assert.Equal("DOM\\svc$", o.Account);
    }

    // ---- command line build/read ----

    [Theory]
    [InlineData(@"C:\Program Files\SqlLookout", @"C:\ProgramData\SqlLookout")]
    [InlineData(@"C:\a b\c", @"D:\data with space\")]
    public void Command_line_round_trips(string dir, string data)
    {
        var o = new SetupOptions { Port = 9001, Listen = "10.0.0.5", Cert = @"C:\c e\x.pfx", CertPasswordFile = @"C:\c e\pw.txt" };
        var line = ServiceCommandLine.Build(Path.Combine(dir, "sqllookout.exe"), o, data);
        var read = ServiceCommandLine.Read(line)!.Value;
        Assert.Equal(Path.Combine(dir, "sqllookout.exe"), read.Exe);
        Assert.Equal(data, read.DataDir);
        Assert.Equal(9001, read.Port);
        Assert.Equal("10.0.0.5", read.Listen);
        var parts = ServiceCommandLine.Split(line);
        Assert.Contains(@"C:\c e\x.pfx", parts);
        Assert.Contains(@"C:\c e\pw.txt", parts);
    }

    [Fact]
    public void Old_style_command_line_without_data_means_data_next_to_the_program()
    {
        var read = ServiceCommandLine.Read(@"""C:\SqlLookout\sqllookout.exe"" web")!.Value;
        Assert.Equal(@"C:\SqlLookout\data", read.DataDir);
        Assert.Equal(8443, read.Port);
    }

    [Fact]
    public void Acl_uses_well_known_ids_and_adds_the_service_account()
    {
        var plain = ServiceSetup.AclArgs(@"C:\d", null);
        Assert.Contains("*S-1-5-32-544:(OI)(CI)F", plain);
        Assert.Contains("*S-1-5-18:(OI)(CI)F", plain);
        Assert.Contains("/inheritance:r", plain);
        Assert.Contains(@"DOM\svc:(OI)(CI)M", ServiceSetup.AclArgs(@"C:\d", @"DOM\svc"));
    }

    [Fact]
    public void Create_args_pass_the_password_only_with_an_account()
    {
        var o = new SetupOptions { Account = @"DOM\svc" };
        var args = ServiceSetup.CreateArgs("img", o, "secret");
        Assert.Equal("secret", args[args.ToList().IndexOf("password=") + 1]);
        Assert.DoesNotContain("password=", ServiceSetup.CreateArgs("img", new SetupOptions(), null));
    }

    // ---- install ----

    [Fact]
    public async Task Install_needs_administrator()
    {
        var host = new FakeHost(_root) { IsAdministrator = false };
        var err = new StringWriter();
        Assert.Equal(1, await ServiceSetup.InstallAsync(new SetupOptions(), MakeSource("src"), host, new StringWriter(), err));
        Assert.Contains("administrator", err.ToString());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Install_refuses_when_the_service_exists()
    {
        var host = new FakeHost(_root) { Exists = true };
        Assert.Equal(1, await ServiceSetup.InstallAsync(new SetupOptions(), MakeSource("src"), host, new StringWriter(), new StringWriter()));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Install_copies_files_locks_data_creates_service_opens_firewall_and_starts_in_order()
    {
        var host = new FakeHost(_root);
        var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat"), Port = 9443 };
        var src = MakeSource("src");
        Directory.CreateDirectory(Path.Combine(src, "data"));
        File.WriteAllText(Path.Combine(src, "data", "sqllookout.db"), "must-not-be-copied");

        Assert.Equal(0, await ServiceSetup.InstallAsync(o, src, host, new StringWriter(), new StringWriter()));

        Assert.Equal("exe-1", File.ReadAllText(Path.Combine(o.Dir!, "sqllookout.exe")));
        Assert.False(Directory.Exists(Path.Combine(o.Dir!, "data")));
        Assert.True(Directory.Exists(o.Data));
        var order = host.Calls.Select(c => c.Split(' ')[0] + (c.StartsWith("sc.exe") ? " " + c.Split(' ')[1] : "")).ToList();
        Assert.Equal(["icacls", "sc.exe create", "sc.exe failure", "sc.exe description", "netsh", "start", "web"], order);
        Assert.Contains(host.Calls, c => c.StartsWith("sc.exe create SqlLookout binPath= ") && c.Contains("start= delayed-auto") && c.Contains("--port 9443"));
        Assert.Contains(host.Calls, c => c.Contains("localport=9443"));
        Assert.Contains("web 127.0.0.1:9443", host.Calls);
    }

    [Fact]
    public async Task Install_on_loopback_or_with_no_firewall_adds_no_rule()
    {
        foreach (var o in new[] { new SetupOptions { Listen = "127.0.0.1" }, new SetupOptions { NoFirewall = true } })
        {
            var host = new FakeHost(_root);
            var opts = o with { Dir = Path.Combine(_root, "app" + Guid.NewGuid().ToString("N")), Data = Path.Combine(_root, "d" + Guid.NewGuid().ToString("N")) };
            Assert.Equal(0, await ServiceSetup.InstallAsync(opts, MakeSource("s" + Guid.NewGuid().ToString("N")), host, new StringWriter(), new StringWriter()));
            Assert.DoesNotContain(host.Calls, c => c.StartsWith("netsh"));
        }
    }

    [Fact]
    public async Task Install_stops_when_creating_the_service_fails()
    {
        var host = new FakeHost(_root) { ExitCode = f => f == "sc.exe" ? 1073 : 0 };
        var err = new StringWriter();
        var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat") };
        Assert.Equal(1, await ServiceSetup.InstallAsync(o, MakeSource("src"), host, new StringWriter(), err));
        Assert.Contains("create the service", err.ToString());
        Assert.DoesNotContain("start", host.Calls);
    }

    [Fact]
    public async Task Install_reports_a_service_that_will_not_start()
    {
        var host = new FakeHost(_root) { StartThrows = true };
        var err = new StringWriter();
        var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat") };
        Assert.Equal(1, await ServiceSetup.InstallAsync(o, MakeSource("src"), host, new StringWriter(), err));
        Assert.Contains("did not start", err.ToString());
    }

    [Fact]
    public async Task Install_refuses_a_folder_that_already_holds_a_program()
    {
        var existing = MakeSource("existing");
        var host = new FakeHost(_root);
        Assert.Equal(2, await ServiceSetup.InstallAsync(new SetupOptions { Dir = existing }, MakeSource("src"), host, new StringWriter(), new StringWriter()));
        Assert.Empty(host.Calls);
    }

    // ---- upgrade ----

    private (FakeHost Host, string Installed, string Data) InstalledService(string dataRelative = "data")
    {
        var installed = MakeSource("installed", "old");
        var data = Path.Combine(installed, dataRelative);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "sqllookout.db"), "precious");
        File.WriteAllText(Path.Combine(installed, "stale-from-old-version.dll"), "stale");
        var image = ServiceCommandLine.Build(Path.Combine(installed, "sqllookout.exe"), new SetupOptions(), data);
        var host = new FakeHost(_root)
        {
            Exists = true,
            ImagePath = image,
            Versions = f => f.EndsWith("installed", StringComparison.OrdinalIgnoreCase) ? new Version(0, 9, 16) : new Version(0, 9, 17),
        };
        return (host, installed, data);
    }

    [Fact]
    public async Task Upgrade_swaps_the_program_removes_stale_files_and_keeps_the_data()
    {
        var (host, installed, data) = InstalledService();
        var src = MakeSource("new", "new");
        var output = new StringWriter();

        Assert.Equal(0, await ServiceSetup.UpgradeAsync(src, host, output, new StringWriter()));

        Assert.Equal("exe-new", File.ReadAllText(Path.Combine(installed, "sqllookout.exe")));
        Assert.False(File.Exists(Path.Combine(installed, "stale-from-old-version.dll")));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(data, "sqllookout.db")));
        Assert.Equal("exe-old", File.ReadAllText(Path.Combine(installed + ".prev", "sqllookout.exe")));
        Assert.False(Directory.Exists(Path.Combine(installed + ".prev", "data")));
        Assert.Equal(["stop", "start", "web 127.0.0.1:8443"], host.Calls);
        Assert.Contains("0.9.16 -> 0.9.17", output.ToString());
    }

    [Fact]
    public async Task Upgrade_keeps_data_that_sits_deeper_inside_the_program_folder()
    {
        var (host, installed, data) = InstalledService(Path.Combine("state", "data"));
        Assert.Equal(0, await ServiceSetup.UpgradeAsync(MakeSource("new", "new"), host, new StringWriter(), new StringWriter()));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(data, "sqllookout.db")));
    }

    [Fact]
    public async Task Upgrade_rolls_back_when_the_new_version_does_not_answer()
    {
        var (host, installed, data) = InstalledService();
        host.WebAnswers = false;
        var err = new StringWriter();

        Assert.Equal(1, await ServiceSetup.UpgradeAsync(MakeSource("new", "new"), host, new StringWriter(), err));

        Assert.Equal("exe-old", File.ReadAllText(Path.Combine(installed, "sqllookout.exe")));
        Assert.Equal("stale", File.ReadAllText(Path.Combine(installed, "stale-from-old-version.dll")));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(data, "sqllookout.db")));
        Assert.Equal(2, host.Calls.Count(c => c == "start"));
        Assert.Contains("previous version is running again", err.ToString());
    }

    [Fact]
    public async Task Upgrade_rolls_back_when_the_new_version_cannot_start()
    {
        var (host, installed, data) = InstalledService();
        var starts = 0;
        host.OnStart = () => host.StartThrows = ++starts == 1;

        Assert.Equal(1, await ServiceSetup.UpgradeAsync(MakeSource("new", "new"), host, new StringWriter(), new StringWriter()));

        Assert.Equal("exe-old", File.ReadAllText(Path.Combine(installed, "sqllookout.exe")));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(data, "sqllookout.db")));
    }

    [Fact]
    public async Task Upgrade_refuses_an_older_build_and_changes_nothing()
    {
        var (host, installed, _) = InstalledService();
        host.Versions = f => f.EndsWith("installed", StringComparison.OrdinalIgnoreCase) ? new Version(0, 9, 17) : new Version(0, 9, 16);
        Assert.Equal(2, await ServiceSetup.UpgradeAsync(MakeSource("new", "new"), host, new StringWriter(), new StringWriter()));
        Assert.Equal("exe-old", File.ReadAllText(Path.Combine(installed, "sqllookout.exe")));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Upgrade_refuses_to_run_from_the_installed_folder_or_when_not_installed()
    {
        var (host, installed, _) = InstalledService();
        Assert.Equal(2, await ServiceSetup.UpgradeAsync(installed, host, new StringWriter(), new StringWriter()));
        host.Exists = false;
        Assert.Equal(1, await ServiceSetup.UpgradeAsync(MakeSource("new"), host, new StringWriter(), new StringWriter()));
    }

    // ---- uninstall ----

    [Fact]
    public void Uninstall_keeps_the_data_unless_asked()
    {
        var (host, installed, data) = InstalledService();
        Assert.Equal(0, ServiceSetup.Uninstall(new SetupOptions(), host, new StringWriter(), new StringWriter()));
        Assert.True(File.Exists(Path.Combine(data, "sqllookout.db")));
        Assert.Contains(host.Calls, c => c == "sc.exe delete SqlLookout");
        Assert.Contains(host.Calls, c => c.StartsWith("netsh advfirewall firewall delete rule"));

        Assert.Equal(0, ServiceSetup.Uninstall(new SetupOptions { DeleteData = true }, host, new StringWriter(), new StringWriter()));
        Assert.False(Directory.Exists(data));
        Assert.True(File.Exists(Path.Combine(installed, "sqllookout.exe")));
    }

    [Fact]
    public void Uninstall_when_not_installed_is_an_error()
    {
        var host = new FakeHost(_root);
        Assert.Equal(1, ServiceSetup.Uninstall(new SetupOptions(), host, new StringWriter(), new StringWriter()));
    }

    // ---- SQL Server store ----

    private string StoreFile(string text)
    {
        var path = Path.Combine(_root, "store-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, text + Environment.NewLine);
        return path;
    }

    private const string GoodStore = "Server=db01;Database=SqlLookoutStore;User Id=slk;Password=pw-secret-1;TrustServerCertificate=true";

    [Fact]
    public async Task Install_with_a_store_checks_it_saves_it_protected_and_points_the_service_at_that_file()
    {
        var host = new FakeHost(_root);
        var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat"), StoreConnectionFile = StoreFile(GoodStore) };
        var output = new StringWriter();

        Assert.Equal(0, await ServiceSetup.InstallAsync(o, MakeSource("src"), host, output, new StringWriter()));

        Assert.Equal([GoodStore], host.StoreChecked);
        Assert.Equal([true], host.StoreCreate);
        var saved = Path.Combine(o.Data!, "store.connection");
        var text = File.ReadAllText(saved);
        Assert.StartsWith("dpapi:", text);
        Assert.DoesNotContain("pw-secret-1", text);
        Assert.Equal(GoodStore, StoreConnectionFile.Read(saved, host.Protector));
        var create = host.Calls.Single(c => c.StartsWith("sc.exe create"));
        Assert.Contains("--store-connection-file", create);
        Assert.Contains("store.connection", create);
        Assert.DoesNotContain("pw-secret-1", create);
        Assert.DoesNotContain("pw-secret-1", output.ToString());
        Assert.Contains("SqlLookoutStore", output.ToString());
    }

    [Fact]
    public async Task Install_with_an_unusable_store_changes_nothing()
    {
        var host = new FakeHost(_root) { StoreProblem = "Cannot use the store database: login failed" };
        var err = new StringWriter();
        var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat"), StoreConnectionFile = StoreFile(GoodStore) };

        Assert.Equal(1, await ServiceSetup.InstallAsync(o, MakeSource("src"), host, new StringWriter(), err));

        Assert.Contains("login failed", err.ToString());
        Assert.Empty(host.Calls);
        Assert.False(Directory.Exists(o.Data));
        Assert.False(Directory.Exists(o.Dir));
    }

    [Theory]
    [InlineData("Server=db01;Database=master;Integrated Security=true", "system database")]
    [InlineData("Server=db01;Database=TempDB;Integrated Security=true", "system database")]
    [InlineData("Server=db01;Integrated Security=true", "no database")]
    [InlineData("Database=X;Integrated Security=true", "no server")]
    [InlineData("this is not a connection string", "not valid")]
    public async Task Install_refuses_a_store_that_would_not_be_a_database_of_its_own(string text, string expected)
    {
        var host = new FakeHost(_root);
        var err = new StringWriter();
        var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat"), StoreConnectionFile = StoreFile(text) };

        Assert.Equal(2, await ServiceSetup.InstallAsync(o, MakeSource("src"), host, new StringWriter(), err));

        Assert.Contains(expected, err.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.Calls);
        Assert.Empty(host.StoreChecked);
    }

    [Fact]
    public async Task Install_refuses_a_missing_or_empty_store_file()
    {
        var host = new FakeHost(_root);
        foreach (var file in new[] { Path.Combine(_root, "missing.txt"), StoreFile("") })
        {
            var o = new SetupOptions { Dir = Path.Combine(_root, "app"), Data = Path.Combine(_root, "dat"), StoreConnectionFile = file };
            Assert.Equal(2, await ServiceSetup.InstallAsync(o, MakeSource("s" + Guid.NewGuid().ToString("N")), host, new StringWriter(), new StringWriter()));
        }
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void A_store_file_protected_on_another_machine_gets_a_clear_message()
    {
        var path = StoreFile("dpapi:AAAA");
        var ex = Assert.Throws<InvalidDataException>(() => StoreConnectionFile.Read(path, new RejectingProtector()));
        Assert.Contains("another machine", ex.Message);
    }

    private sealed class RejectingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => throw new System.Security.Cryptography.CryptographicException("wrong machine");
    }

    [Fact]
    public void Parser_accepts_the_store_option_only_for_install()
    {
        var (o, e) = SetupOptionsParser.Parse("install", ["install", "--store-connection-file", "s.txt"]);
        Assert.Null(e);
        Assert.Equal(Path.GetFullPath("s.txt"), o!.StoreConnectionFile);
        Assert.Null(SetupOptionsParser.Parse("uninstall", ["uninstall", "--store-connection-file", "s.txt"]).Options);
    }

    [Fact]
    public void Command_line_with_a_store_reads_back_and_uninstall_says_the_tables_stay()
    {
        var image = ServiceCommandLine.Build(@"C:\app\sqllookout.exe", new SetupOptions(), @"C:\dat", @"C:\dat\store.connection");
        Assert.Equal(@"C:\dat\store.connection", ServiceCommandLine.Read(image)!.Value.StoreFile);

        var host = new FakeHost(_root) { Exists = true, ImagePath = image };
        var output = new StringWriter();
        Assert.Equal(0, ServiceSetup.Uninstall(new SetupOptions(), host, output, new StringWriter()));
        Assert.Contains("SQL Server", output.ToString());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void The_saved_store_file_is_protected_with_the_real_windows_dpapi_and_reads_back()
    {
        var path = Path.Combine(_root, "real.connection");
        var protector = new DpapiSecretProtector();

        StoreConnectionFile.WriteProtected(path, GoodStore, protector);

        Assert.DoesNotContain("pw-secret-1", File.ReadAllText(path));
        Assert.DoesNotContain("SqlLookoutStore", File.ReadAllText(path));
        Assert.Equal(GoodStore, StoreConnectionFile.Read(path, protector));
    }
}

public class DescribeTests
{
    [Fact]
    public void An_empty_message_still_names_the_exception_and_the_inner_chain()
    {
        var ex = new IOException("", new UnauthorizedAccessException("Access is denied."));
        var text = ServiceSetup.Describe(ex);
        Assert.Contains("IOException", text);
        Assert.Contains("Access is denied. (UnauthorizedAccessException)", text);
    }
}
