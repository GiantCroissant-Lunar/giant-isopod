using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Records terminal output in asciicast v2 format (NDJSON).
/// Each agent session produces a .cast file that can be replayed with
/// asciinema play, or loaded into GodotXterm via its asciicast import plugin.
/// </summary>
public sealed class AsciicastRecorder : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly DateTime _startTime;
    private readonly string _filePath;
    private bool _headerWritten;

    public string FilePath => _filePath;

    public AsciicastRecorder(string filePath, int cols = 120, int rows = 24)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(filePath, false, Encoding.UTF8) { AutoFlush = true };
        _startTime = DateTime.UtcNow;

        // Write asciicast v2 header
        var header = new
        {
            version = 2,
            width = cols,
            height = rows,
            timestamp = new DateTimeOffset(_startTime).ToUnixTimeSeconds(),
            env = new { SHELL = "pi", TERM = "xterm-256color" }
        };
        _writer.WriteLine(JsonSerializer.Serialize(header));
        _headerWritten = true;
    }

    /// <summary>
    /// Records an output event with the elapsed time since recording started.
    /// </summary>
    public void WriteOutput(string data)
    {
        if (!_headerWritten || string.IsNullOrEmpty(data)) return;

        var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
        // asciicast v2 event: [time, "o", "data"]
        var escapedData = JsonSerializer.Serialize(data); // handles escaping
        _writer.WriteLine($"[{elapsed.ToString("F6", CultureInfo.InvariantCulture)}, \"o\", {escapedData}]");
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
    }
}
