using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace SqlLookout.Service;

/// <summary>Runs one of the setup commands (install, upgrade, uninstall) with administrator rights and brings back what it printed.</summary>
public interface ISetupRunner
{
    /// <param name="args">The command line after the program name, for example <c>install --port 8443</c>.</param>
    Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>
/// Starts this program again through "runas" (Windows shows its permission prompt) and reads the output it was told to
/// write to a file. The wizard that calls this does not run with administrator rights itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedSetupRunner(string exePath) : ISetupRunner
{
    /// <summary>The exit code reported when the person declined the Windows permission prompt.</summary>
    public const int Declined = 1223;

    public async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var dir = Directory.CreateTempSubdirectory("SqlLookout-run-");
        try
        {
            var outputFile = Path.Combine(dir.FullName, "output.txt");
            var info = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ', args.Concat(["--output-file", outputFile]).Select(Quote)),
            };
            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == Declined)
            {
                return (Declined, "Windows asked for permission and it was declined, so nothing was changed.");
            }
            if (process is null) return (1, "The setup process could not be started.");

            using (process)
            {
                await process.WaitForExitAsync(ct);
                var output = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile, ct) : "";
                return (process.ExitCode, output);
            }
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>One argument for a Windows command line: quoted when it has a space or quote, with a trailing backslash doubled.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}

/// <summary>Writes everything to two writers, so a command prints to the console and into the file the wizard reads.</summary>
public sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}
