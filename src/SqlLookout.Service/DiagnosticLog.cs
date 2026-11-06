using System.Globalization;
using System.Text;

namespace SqlLookout.Service;

/// <summary>
/// An optional text log for reporting problems. Off by default; when on, warnings, errors and a few page events are
/// appended to one file in the data folder. The file is cut to its newest half when it passes <see cref="MaxBytes"/>.
/// It never holds passwords, keys or connection strings; it does hold server names, so look at it before sending it on.
/// </summary>
public sealed class DiagnosticLog(string path)
{
    public const long MaxBytes = 1_000_000;
    private readonly object _gate = new();
    private volatile bool _enabled;

    public string Path { get; } = path;
    public bool Enabled { get => _enabled; set => _enabled = value; }

    public void Write(string category, string message)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{category}] {message.ReplaceLineEndings(" | ")}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes) Trim();
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must never break the page or the monitor.
            }
        }
    }

    /// <summary>The newest lines, oldest first. Empty when there is no file yet.</summary>
    public string Tail(int maxLines = 300)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path)) return "";
                return string.Join(Environment.NewLine, File.ReadLines(Path, Encoding.UTF8).TakeLast(maxLines));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }
    }

    public byte[] ReadAll()
    {
        lock (_gate)
        {
            try { return File.Exists(Path) ? File.ReadAllBytes(Path) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Trim()
    {
        var lines = File.ReadAllLines(Path, Encoding.UTF8);
        File.WriteAllLines(Path, lines.Skip(lines.Length / 2), Encoding.UTF8);
    }
}
