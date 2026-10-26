// Local server manager checks: option guards (local only, never the VPS layout), status and census parsing with the ghost-member
// check, log filter and follower, deploy plan and backup/rollback scripts, config and admin guards, and the MCP tools with a fake
// WSL shell. No WSL, server or game install needed; with Git Bash on PATH the generated scripts are also syntax-checked.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Mcp.LocalServer;

var checks = new List<string>();
string scratch = Path.Combine(Path.GetTempPath(), "solforge-local-server-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    Options(); checks.Add("options: defaults pass; /opt/sol-server (VPS), relative, quoted and backups-inside-run paths are refused");
    Paths(); checks.Add("Windows paths map to /mnt/<drive>/...; UNC paths do not");
    StatusParse(); checks.Add("status: pid, uptime, cwd, BAP UDP/TCP, web and admin ports, players and names parse; stopped server parses");
    CensusParse(); checks.Add("census: only the newest round counts; a member twice in a session is a ghost; members in two sessions are listed");
    Filters(); checks.Add("log filter: all terms, -exclude, re:regex, bad regex falls back to text; follower holds partial lines");
    DeployPlans(); checks.Add("deploy plan: scripts/ and sdk/ (no hidden or .bak), pack and build_data from the newest build, unsafe names refused");
    Scripts(); checks.Add("scripts: backup tar before any copy, rollback only removes relative created files, backup ids validated, no VPS path");
    await Manager(); checks.Add("manager: status via fake shell, admin allow-list (no /stop), form-encoded commands, config JSON validated before writing");
    await Tools(); checks.Add("MCP tools: 15 local_server_* tools with object schemas; declined approval never runs; quick actions map to server commands");
    await DeployReports(); checks.Add("deploy report: copied, scripts loaded/errors (or not checked), restart healthy with pid and ports, failures flip ok");
    WslNoise(); checks.Add("wsl noise: Failed to translate lines (also UTF-16 NUL-padded) removed, real output kept");
    Subsystems(); checks.Add("subsystems: every Demonware/Bungie role mapped to a port check; all down when stopped");
    BashSyntax(checks);
    foreach (string check in checks) Console.WriteLine("ok " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error.StackTrace);
    return 1;
}
finally
{
    if (scratch.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(scratch)) Directory.Delete(scratch, true);
}

static void Expect(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException(what);
}

static void Options()
{
    Expect(new LocalServerOptions().Problem() is null, "defaults");
    Expect(new LocalServerOptions { RunDirectory = "/opt/sol-server/run" }.Problem()?.Contains("VPS") == true, "VPS layout refused");
    Expect(new LocalServerOptions { RunDirectory = "root/run" }.Problem() is not null, "relative refused");
    Expect(new LocalServerOptions { RunDirectory = "/root/it's" }.Problem() is not null, "quote refused");
    Expect(new LocalServerOptions { RunDirectory = "/root/a/../b" }.Problem() is not null, "dotdot refused");
    Expect(new LocalServerOptions { BackupDirectory = "/root/sol-local/run/b" }.Problem() is not null, "backups inside run refused");
    Expect(new LocalServerOptions { Executable = "sol server" }.Problem() is not null, "executable name");
}

static void Paths()
{
    Expect(Shell.WslPath(@"C:\Users\Seth\My Project\scripts") == "/mnt/c/Users/Seth/My Project/scripts", "drive path");
    Expect(Shell.WslPath(@"\\wsl$\Ubuntu\root") is null, "UNC");
    Expect(Shell.Quote("it's") == "'it'\\''s'", "quote");
}

#pragma warning disable CS0162 // the top-level sample constants below sit after the return; they are compile-time only
const string StatusSample = """
    PID=15662
    ETIME=10165
    CWD=/root/sol-local/run
    ---SOCKETS
    udp   UNCONN 0      0             0.0.0.0:27960      0.0.0.0:*    users:(("sol-server",pid=15662,fd=12))
    udp   UNCONN 0      0             0.0.0.0:30974      0.0.0.0:*    users:(("sol-server",pid=15662,fd=9))
    tcp   LISTEN 0      8             0.0.0.0:8098       0.0.0.0:*    users:(("sol-server",pid=15662,fd=16))
    tcp   LISTEN 0      128           0.0.0.0:30974      0.0.0.0:*    users:(("sol-server",pid=15662,fd=8))
    tcp   LISTEN 0      8           127.0.0.1:8099       0.0.0.0:*    users:(("sol-server",pid=15662,fd=15))
    ---ADMIN
    {"ok":true,"uptime_s":10165,"bap_port":30974,"players":1}
    ---PLAYERS
    {"ok":true,"players":[{"name":"Seth","id":1}]}
    ---CENSUS
    server level=info t=1000 [SESSION] ev=dedicated stage=census session=0xOLD dest=8 joined=1 shared=1 members=0x100100,0x100100 owners=
    server level=info t=9000 [SESSION] ev=dedicated stage=census session=0xA dest=8 joined=1 shared=1 members=0x100100 owners=0x100100
    server level=info t=9001 [SESSION] ev=dedicated stage=census session=0xB dest=8 joined=0 shared=0 members= owners=
    ---END
    """;

static void StatusParse()
{
    var status = LocalServerParse.Status(StatusSample, new LocalServerOptions());
    Expect(status.Running && status.Pid == 15662 && status.UptimeSeconds == 10165 && !status.ForeignInstance, "process");
    Expect(status.BapUdp && status.BapTcp && status.Web && status.Admin && status.Sockets.Count == 5, "ports");
    Expect(status.Players == 1 && status.PlayerNames.SequenceEqual(new[] { "Seth" }), "players");
    Expect(status.Census.Ghosts.Count == 0 && status.Census.Sessions == 2, "old census round ignored");
    Expect(status.ToJson()["census"]?["ghost_check"]?.ToString() == "ok", "json ghost check");
    var stopped = LocalServerParse.Status("PID=\n---SOCKETS\n---ADMIN\n---PLAYERS\n---CENSUS\n---END\n", new LocalServerOptions());
    Expect(!stopped.Running && !stopped.BapUdp && stopped.Summary == "stopped", "stopped");
    var foreign = LocalServerParse.Status("PID=7\nCWD=/opt/x\n---END\n", new LocalServerOptions());
    Expect(foreign.ForeignInstance, "foreign instance");
    Expect(LocalServerParse.Socket("udp UNCONN 0 0 [::]:30974 [::]:* users:") is { Port: 30974, Address: "[::]" }, "ipv6 socket");
}

static void CensusParse()
{
    var census = LocalServerParse.Census([
        "server level=info t=5000 [SESSION] ev=dedicated stage=census session=0xS1 dest=8 joined=2 shared=1 members=0x100100,0x100100 owners=",
        "server level=info t=5000 [SESSION] ev=dedicated stage=census session=0xS2 dest=8 joined=1 shared=0 members=0x200200 owners=",
        "server level=info t=5001 [SESSION] ev=dedicated stage=census session=0xS3 dest=9 joined=1 shared=0 members=0x200200 owners=",
        "unrelated line",
    ]);
    Expect(census.Ghosts.Count == 1 && census.Ghosts[0] is { Session: "0xS1", Member: "0x100100", Count: 2 }, "ghost found");
    Expect(census.SplitMembers.SequenceEqual(new[] { "0x200200" }) && census.Members == 2 && census.Sessions == 3, "split members");
    var status = LocalServerParse.Status("PID=1\n---CENSUS\n" + "server level=info t=1 stage=census session=0xS dest=1 members=0x1,0x1 owners=\n---END\n",
        new LocalServerOptions());
    Expect(status.Summary.Contains("GHOST", StringComparison.Ordinal), "summary warns");
    Expect(LocalServerParse.Census([]) == CensusSummary.Empty, "empty");
}

static void Filters()
{
    var lines = new[] { "level=error mission failed", "level=info stage=census session=1", "level=debug mission tick", "100% done" };
    Expect(lines.Where(LocalServerParse.Filter("mission")).Count() == 2, "term");
    Expect(lines.Where(LocalServerParse.Filter("mission -debug")).Single().StartsWith("level=error", StringComparison.Ordinal), "exclude");
    Expect(lines.Where(LocalServerParse.Filter("re:^level=(error|info)")).Count() == 2, "regex");
    Expect(lines.Where(LocalServerParse.Filter("re:([")).Count() == 0, "bad regex is text");
    Expect(lines.Where(LocalServerParse.Filter("")).Count() == 4 && lines.Where(LocalServerParse.Filter("CENSUS")).Count() == 1, "empty and case");
    var follower = new LogFollower();
    Expect(follower.Accept("a\nb", 3, false).SequenceEqual(new[] { "a" }) && follower.Offset == 3, "partial held");
    Expect(follower.Accept("c\r\nd\n", 9, false).SequenceEqual(new[] { "bc", "d" }), "joined");
    Expect(follower.Accept("x", 10, true).Count == 0 && follower.Accept("\n", 11, false).SequenceEqual(new[] { "x" }), "reset");
}

void DeployPlans()
{
    string project = Path.Combine(scratch, "project");
    Write(project, "scripts/edz_freeroam/main.lua", "-- lua");
    Write(project, "scripts/.studio-sources.json", "{}");
    Write(project, "sdk/lua/api.lua", "-- sdk");
    Write(project, "sdk/catalog.bin.bak", "old");
    Write(project, "build/20260101-000000-aaaa/activity_sdk.pack", "old pack");
    Write(project, "build/20260102-000000-bbbb/activity_sdk.pack", "new pack");
    Directory.SetLastWriteTimeUtc(Path.Combine(project, "build/20260101-000000-aaaa"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    Directory.SetLastWriteTimeUtc(Path.Combine(project, "build/20260102-000000-bbbb"), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
    Write(project, "build_data.bin", "data");
    var plan = DeployPlanner.Plan(project);
    var targets = plan.Files.Select(file => file.Target).ToArray();
    Expect(targets.SequenceEqual(new[] { "scripts/edz_freeroam/main.lua", "sdk/lua/api.lua", "activity_sdk.pack", "cache/build_data.bin" }), "targets: " + string.Join(",", targets));
    Expect(plan.Files.Single(file => file.Target == "activity_sdk.pack").Source.Contains("bbbb", StringComparison.Ordinal), "newest build");
    Expect(plan.ToJson()["groups"]?["scripts"]?.GetValue<int>() == 1, "json groups");
    string empty = Path.Combine(scratch, "empty");
    Directory.CreateDirectory(empty);
    var nothing = DeployPlanner.Plan(empty);
    Expect(nothing.Files.Count == 0 && nothing.Notes.Contains("nothing to deploy"), "empty project");
    string bad = Path.Combine(scratch, "bad");
    Write(bad, "scripts/it's.lua", "--");
    try { DeployPlanner.Plan(bad); Expect(false, "quote in name accepted"); } catch (InvalidDataException) { }
}

static void Write(string root, string relative, string text)
{
    string path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}

void Scripts()
{
    var options = new LocalServerOptions();
    var plan = DeployPlanner.Plan(Path.Combine(scratch, "project"));
    string deploy = DeployPlanner.DeployScript(plan, options, "20261015-120000-ab12");
    int tar = deploy.IndexOf("tar -czf", StringComparison.Ordinal), copy = deploy.IndexOf("cp -f", StringComparison.Ordinal);
    Expect(tar > 0 && copy > tar && deploy.Contains("set -eu", StringComparison.Ordinal), "backup before copy");
    Expect(deploy.Contains("/mnt/", StringComparison.Ordinal) && deploy.Contains("\tcache/build_data.bin\n", StringComparison.Ordinal), "plan rows");
    Expect(deploy.Contains("cd '/root/sol-local/run/Sunrise'", StringComparison.Ordinal) && !deploy.Contains("/opt/", StringComparison.Ordinal), "local only");
    Expect(deploy.Contains("\nSOLFORGE_PLAN\n", StringComparison.Ordinal) && !deploy.Contains('\r'), "heredoc terminator on its own line");
    string rollback = DeployPlanner.RollbackScript(options, "20261015-120000-ab12");
    Expect(rollback.Contains("''|/*|*..*) continue", StringComparison.Ordinal) && rollback.Contains("tar -xzf", StringComparison.Ordinal), "rollback guards");
    Expect(DeployPlanner.IsBackupId("20261015-120000") && !DeployPlanner.IsBackupId("../x") && !DeployPlanner.IsBackupId("20261015-120000; rm"), "ids");
    try { DeployPlanner.RollbackScript(options, "x; rm -rf /"); Expect(false, "bad id accepted"); } catch (ArgumentException) { }
    var manager = new LocalServerManager(new FakeShell(), options);
    Expect(manager.StartScript().Contains("setsid nohup ./sol-server config", StringComparison.Ordinal), "start per runbook");
    Expect(manager.StartScript().Contains("ALREADY_RUNNING", StringComparison.Ordinal) && manager.StartScript().Contains("PORT_BUSY", StringComparison.Ordinal), "start guards");
    Expect(manager.StopScript().Contains("pkill -TERM -x 'sol-server'", StringComparison.Ordinal), "stop exact name");
    Expect(manager.SelftestScript().Contains("--selftest", StringComparison.Ordinal) && manager.SelftestScript().Contains("${PIPESTATUS[0]}", StringComparison.Ordinal), "selftest");
    Expect(manager.LogScript(-1, 50).Contains("tail -n 50", StringComparison.Ordinal) && manager.LogScript(100, 50).Contains("tail -c +$((o+1))", StringComparison.Ordinal), "log");
}

static async Task Manager()
{
    var shell = new FakeShell { Reply = _ => new ShellResult(0, StatusSample, "", false) };
    var manager = new LocalServerManager(shell, new LocalServerOptions());
    var status = await manager.StatusAsync();
    Expect(status.Running && shell.Scripts[^1].Contains("pgrep -o -x 'sol-server'", StringComparison.Ordinal), "status through shell");
    shell.Reply = _ => new ShellResult(0, "partial", "", true);
    Expect((await manager.StatusAsync()).Error?.Contains("in time", StringComparison.Ordinal) == true, "timeout reported");
    var vps = new LocalServerManager(shell, new LocalServerOptions { RunDirectory = "/opt/sol-server/run" });
    int before = shell.Scripts.Count;
    Expect(!(await vps.StartAsync()).Ok && shell.Scripts.Count == before, "VPS options never run");

    try { await manager.AdminAsync("stop", null, true); Expect(false, "stop allowed"); } catch (ArgumentException) { }
    try { await manager.AdminAsync("reboot", null, false); Expect(false, "reboot allowed"); } catch (ArgumentException) { }
    shell.Reply = _ => new ShellResult(0, "{\"ok\":true,\"lines\":[\"Gave Seth 50000 Glimmer\"]}", "", false);
    var command = await manager.CommandAsync("/give Seth glimmer 50000");
    Expect(command.Ok && command.Output == "Gave Seth 50000 Glimmer", "command reply");
    string sent = shell.Scripts[^1];
    Expect(sent.Contains("--data-binary 'line=give%20Seth%20glimmer%2050000'", StringComparison.Ordinal)
        && sent.Contains("X-Sol-Panel: 1", StringComparison.Ordinal) && sent.Contains("http://127.0.0.1:8099/sol/command", StringComparison.Ordinal), "form post: " + sent);
    Expect(manager.AdminScript("sol/inventory", new Dictionary<string, string> { ["account"] = "it's me" }, false).Contains("account=it%27s%20me", StringComparison.Ordinal), "get query escaped");

    Expect(LocalServerManager.ConfigPath("settings.json") == "config/settings.json" && LocalServerManager.ConfigPath("Sunrise/crucible.cfg") == "Sunrise/crucible.cfg", "config names");
    Expect(LocalServerManager.ConfigPath("../sol-server") is null && LocalServerManager.ConfigPath("config/x.sh") is null && LocalServerManager.ConfigPath("/etc/passwd") is null, "config refused");
    before = shell.Scripts.Count;
    Expect(!(await manager.WriteConfigAsync("settings.json", "{ broken")).Ok && shell.Scripts.Count == before, "bad JSON never written");
    shell.Reply = _ => new ShellResult(0, "WROTE=config/settings.json", "", false);
    Expect((await manager.WriteConfigAsync("settings.json", "{\n  // comment\n  \"a\": 1,\n}")).Ok, "json with comments saved");
    string write = shell.Scripts[^1];
    Expect(write.Contains("base64 -d", StringComparison.Ordinal) && write.Contains("/root/sol-local/solforge-backups/config", StringComparison.Ordinal)
        && write.IndexOf("cp -p", StringComparison.Ordinal) < write.IndexOf("mv -f", StringComparison.Ordinal), "config backup before replace");
    Expect(LocalServerManager.Redact("{\n \"bot_key\": \"abc\",\n \"name\": \"x\"\n}\npanel_password=hunter2").Contains("hunter2", StringComparison.Ordinal) == false
        && LocalServerManager.Redact("\"name\": \"x\"").Contains("\"x\"", StringComparison.Ordinal), "redaction");

    shell.Reply = _ => new ShellResult(0, "20261015-120000-ab12\t42\tactive\tC:\\proj\n20261014-110000\t3\trolled-back\t\njunk\n", "", false);
    var backups = await manager.BackupsAsync();
    Expect(backups.Count == 2 && backups[0].Files == 42 && !backups[0].RolledBack && backups[1].RolledBack, "backups");
    const string listed = "20261015-120000-ab12\t42\tactive\tC:\\proj\n";
    shell.Reply = script => new ShellResult(0, script.Contains("ROLLED_BACK", StringComparison.Ordinal) ? "ROLLED_BACK=x REMOVED=1" : listed, "", false);
    var rolled = await manager.RollbackAsync(null, false);
    Expect(rolled.Ok && shell.Scripts[^1].Contains("20261015-120000-ab12", StringComparison.Ordinal), "rollback picks newest active backup");
    shell.Reply = _ => new ShellResult(0, "...\nSELFTEST_EXIT=2\n", "", false);
    Expect(!(await manager.SelftestAsync()).Ok, "selftest failure reported");
}

async Task Tools()
{
    var shell = new FakeShell { Reply = _ => new ShellResult(0, StatusSample, "", false) };
    var manager = new LocalServerManager(shell, new LocalServerOptions());
    bool approve = false;
    var approvals = new List<string>();
    string? project = null;
    var tools = LocalServerTools.Create(manager, () => project, (tool, summary, _) => { approvals.Add(summary); return Task.FromResult(approve); });
    Expect(tools.Select(tool => tool.Name).SequenceEqual(LocalServerTools.Names) && tools.Count == 15, "tool names");
    Expect(tools.All(tool => tool.InputSchema["type"]?.ToString() == "object"), "object schemas");
    Expect(tools.Where(tool => !tool.RequiresApproval).Select(tool => tool.Name).All(name =>
        name is "local_server_status" or "local_server_log" or "local_server_subsystems" or "local_server_backups" or "local_server_config_read" or "local_server_deploy_plan"), "only reads skip approval");
    McpTool Tool(string name) => tools.Single(tool => tool.Name == name);

    var status = await Tool("local_server_status").Handler(new JsonObject(), default);
    Expect(!status.IsError && status.Texts[0].Contains("\"pid\": 15662", StringComparison.Ordinal), "status tool");
    int before = shell.Scripts.Count;
    var declined = await Tool("local_server_restart").Handler(new JsonObject(), default);
    Expect(declined.IsError && shell.Scripts.Count == before && approvals[^1] == "Local server: restart", "declined never runs");
    var noProject = await Tool("local_server_deploy").Handler(new JsonObject(), default);
    Expect(noProject.IsError && noProject.Texts[0].Contains("project", StringComparison.Ordinal), "deploy needs a project");
    project = Path.Combine(scratch, "project");
    var planned = await Tool("local_server_deploy_plan").Handler(new JsonObject(), default);
    Expect(!planned.IsError && planned.Texts[0].Contains("cache/build_data.bin", StringComparison.Ordinal), "deploy plan tool");
    approve = true;
    shell.Reply = _ => new ShellResult(0, "DEPLOYED=4 EXISTED=1 CREATED=3 BACKUP=/root/sol-local/solforge-backups/x", "", false);
    var deployed = await Tool("local_server_deploy").Handler(new JsonObject(), default);
    Expect(!deployed.IsError && shell.Scripts.Any(s => s.Contains("tar -czf", StringComparison.Ordinal)), "deploy tool runs after approval");
    var badRollback = await Tool("local_server_rollback").Handler(new JsonObject { ["backup"] = "../../etc" }, default);
    Expect(badRollback.IsError, "rollback id validated");

    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "give", ["player"] = "Seth", ["item"] = "Reverie Dawn", ["amount"] = 2 }).Line == "give Seth \"Reverie Dawn\" 2", "give");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "reset_character", ["player"] = "Seth" }).Line == "reset character Seth confirm", "reset");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "reset_character", ["player"] = "Seth", ["character"] = "Hunter" }).Line == "reset character Seth hunter confirm", "reset one class");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "reset_character", ["player"] = "Seth", ["character"] = "2" }).Line == "reset character Seth 2 confirm", "reset one slot");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "reset_character", ["player"] = "Seth", ["character"] = "confirm" }).Problem is not null, "reset character validated");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "set_activity", ["target"] = "find glimmer" }).Line == "activity find glimmer", "activity");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "give", ["player"] = "Seth" }).Problem is not null, "give needs item");
    Expect(LocalServerTools.QuickLine(new JsonObject { ["action"] = "stop" }).Problem is not null, "unknown action");
}

static void Subsystems()
{
    Expect(ServerSubsystems.All.Count >= 7 && ServerSubsystems.All.All(item => item.Check.Length > 0 && item.Source.Length > 0), "documented");
    Expect(ServerSubsystems.All.Any(item => item.Replaces.Contains("Demonware", StringComparison.Ordinal))
        && ServerSubsystems.All.Any(item => item.Replaces.Contains("Activity Host", StringComparison.Ordinal)), "both halves");
    var stopped = LocalServerParse.Status("PID=\n---END\n", new LocalServerOptions());
    Expect(ServerSubsystems.All.All(item => ServerSubsystems.IsUp(item, stopped) == false), "down when stopped");
    var running = LocalServerParse.Status(StatusSample, new LocalServerOptions());
    Expect(ServerSubsystems.All.All(item => ServerSubsystems.IsUp(item, running) == true), "up when running");
}

void BashSyntax(List<string> done)
{
    string? bash = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
        .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "bash.exe" : "bash")).FirstOrDefault(File.Exists);
    if (bash is null || bash.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("skipped: bash -n syntax check (no Git Bash on PATH)"); return; }
    var options = new LocalServerOptions();
    var manager = new LocalServerManager(new FakeShell(), options);
    var plan = DeployPlanner.Plan(Path.Combine(scratch, "project"));
    string[] scripts = [manager.StatusScript(), manager.StartScript(), manager.StopScript(), manager.LogScript(-1, 10), manager.LogScript(5, 10),
        manager.SelftestScript(), manager.WriteConfigScript("config/settings.json", "{}", "20261015-000000"), manager.AdminScript("sol/command", new Dictionary<string, string> { ["line"] = "who" }, true),
        DeployPlanner.DeployScript(plan, options, "20261015-000000"), DeployPlanner.RollbackScript(options, "20261015-000000"), DeployPlanner.ListBackupsScript(options), DeployPlanner.CheckScriptsScript(["scripts/a b/main.lua", "sdk/x.lua"], options)];
    foreach (string script in scripts)
    {
        var start = new ProcessStartInfo(bash, "-n") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(script.Replace("\r\n", "\n"));
        process.StandardInput.Close();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Expect(process.ExitCode == 0, "bash -n: " + errors + "\n" + script);
    }
    done.Add($"bash -n accepts all {scripts.Length} generated scripts");
}

static void WslNoise()
{
    const string noise = @"wsl: Failed to translate 'E:\solforge-p47b\.dotnet\.dotnet\tools'";
    Expect(Shell.StripWslNoise(noise + "\nSTARTED=12\n") == "STARTED=12\n", "noise line removed");
    Expect(Shell.StripWslNoise(noise + "\r\n" + noise + "\r\nok") == "ok", "CRLF and repeated lines removed");
    Expect(Shell.StripWslNoise(string.Concat((noise + "\n").Select(c => c + "\0"))) == "", "UTF-16 padded line removed");
    Expect(Shell.StripWslNoise("PID=1\nFailed to translate in my log\n") == "PID=1\nFailed to translate in my log\n", "other text untouched");
}

async Task DeployReports()
{
    string project = Path.Combine(scratch, "project");
    var plan = DeployPlanner.Plan(project);
    var options = new LocalServerOptions { HealthWait = TimeSpan.FromMilliseconds(20) };
    const string stopped = "PID=\n---SOCKETS\n---ADMIN\n---PLAYERS\n---CENSUS\n---END\n";
    ShellResult Answer(string script, string check, string status) =>
        script.Contains("SOLFORGE_PLAN", StringComparison.Ordinal) ? new ShellResult(0, "DEPLOYED=4 EXISTED=0 CREATED=4", "", false)
        : script.Contains("LUATOOL", StringComparison.Ordinal) || script.Contains("LUAUNAVAILABLE", StringComparison.Ordinal) ? new ShellResult(0, check, "", false)
        : script.Contains("PID=$pid", StringComparison.Ordinal) ? new ShellResult(0, status, "", false)
        : new ShellResult(0, script.Contains("setsid", StringComparison.Ordinal) ? "STARTED=15662" : "STOPPED=1", "", false);
    const string good = "LUATOOL=lua5.4\nLUAWARN\tscripts/a.lua\trequire 'x' not found in scripts/ or sdk/\nLUASUMMARY\t2\t0\n";

    var shell = new FakeShell { Reply = s => Answer(s, good, StatusSample) };
    var manager = new LocalServerManager(shell, options);
    var ok = await manager.DeployAsync(plan, true);
    Expect(ok.Ok && ok.Message.Contains("scripts OK: 2 loaded, 0 errors", StringComparison.Ordinal), "good deploy: " + ok.Message);
    Expect(ok.Message.Contains("restart OK: server healthy, pid 15662, ports", StringComparison.Ordinal) && ok.Message.Contains("30974", StringComparison.Ordinal), "restart health: " + ok.Message);
    var report = ok.ToJson()["report"]!;
    Expect(report["copied"]!.GetValue<int>() == plan.Files.Count && report["scripts"]!["loaded"]!.GetValue<int>() == 2
        && report["restart"]!["healthy"]!.GetValue<bool>() && report["restart"]!["pid"]!.GetValue<int>() == 15662, "report json");
    Expect(shell.Scripts.Any(s => s.Contains("LUATOOL", StringComparison.Ordinal) && s.Contains("scripts/edz_freeroam/main.lua\n", StringComparison.Ordinal)), "check runs on the deployed lua files");
    int check = shell.Scripts.FindIndex(s => s.Contains("LUATOOL", StringComparison.Ordinal)), start = shell.Scripts.FindIndex(s => s.Contains("setsid", StringComparison.Ordinal));
    Expect(check >= 0 && start > check, "scripts are checked before the restart");

    shell.Scripts.Clear();
    var noRestart = await manager.DeployAsync(plan, false);
    Expect(noRestart.Ok && noRestart.Message.Contains("server not restarted", StringComparison.Ordinal) && !shell.Scripts.Any(s => s.Contains("setsid", StringComparison.Ordinal)), "no restart asked: server untouched");

    var broken = new FakeShell { Reply = s => Answer(s, "LUATOOL=lua5.4\nLUAERR\tscripts/edz_freeroam/main.lua\t[string]:3: unexpected symbol near 'end'\nLUASUMMARY\t1\t1\n", StatusSample) };
    var bad = await new LocalServerManager(broken, options).DeployAsync(plan, false);
    Expect(!bad.Ok && bad.Message.Contains("scripts FAILED", StringComparison.Ordinal) && bad.Message.Contains("main.lua", StringComparison.Ordinal)
        && bad.Output.Contains("unexpected symbol", StringComparison.Ordinal) && bad.ToJson()["report"]!["scripts"]!["error_count"]!.GetValue<int>() == 1, "script error reported now");

    var none = new FakeShell { Reply = s => Answer(s, "LUAUNAVAILABLE\n", StatusSample) };
    var unchecked1 = await new LocalServerManager(none, options).DeployAsync(plan, false);
    Expect(unchecked1.Ok && unchecked1.Message.Contains("scripts NOT CHECKED", StringComparison.Ordinal), "no Lua in WSL is said, not hidden");

    var down = new FakeShell { Reply = s => Answer(s, good, stopped) };
    var failed = await new LocalServerManager(down, options).DeployAsync(plan, true);
    Expect(!failed.Ok && failed.Message.Contains("restart FAILED", StringComparison.Ordinal) && failed.Message.Contains("not running", StringComparison.Ordinal)
        && !failed.ToJson()["report"]!["restart"]!["healthy"]!.GetValue<bool>(), "unhealthy restart fails the deploy: " + failed.Message);

    Expect(DeployPlanner.ParseScriptCheck("garbage", "wsl died").Why.Contains("wsl died", StringComparison.Ordinal), "unparseable check output");
    try { DeployPlanner.CheckScriptsScript(["scripts/../x.lua"], options); Expect(false, "dotdot accepted"); } catch (InvalidDataException) { }
}

sealed class FakeShell : IWslShell
{
    public List<string> Scripts { get; } = [];
    public Func<string, ShellResult> Reply { get; set; } = _ => new ShellResult(0, "", "", false);

    /** Records the script and answers with the configured reply instead of running a shell. */
    public Task<ShellResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        Scripts.Add(script);
        return Task.FromResult(Reply(script));
    }
}
