using Timer = System.Threading.Timer;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace DSQQ.Services;

// 日志级别（按文本关键字粗分，够用且不依赖格式约定）。
public enum LogLevel
{
    // 普通信息。
    Info,

    // 调试/详细。
    Debug,

    // 警告。
    Warn,

    // 错误。
    Error,
}

// 一行日志。
// 参数 Source：来源名称（如"桥接"/"SnowLuma"）。
// 参数 Timestamp：从行首提取到的时间（形如 HH:mm:ss）；提取不到时为 null。
// 参数 Level：推断出的级别。
// 参数 Text：原始行内容。
public sealed record LogLine(string Source, DateTime? Timestamp, LogLevel Level, string Text);

// 单个日志文件的跟随读取。
// 用 FileSystemWatcher 通知变化，但读取靠字节偏移量：
// 文件被追加时只读新增部分，避免每次重读整个文件（桥接日志可达数十 MB）。
// 去抖是必需的 —— Windows 上一次写入可能触发多个 Changed 事件。
public sealed class LogFileTail : IDisposable
{
    private readonly string _path;
    private readonly int _maxBytesPerRead;
    private readonly int _debounceMs;
    private readonly Channel<LogLine> _channel;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private long _position;
    private Timer? _debounce;
    private bool _disposed;

    // 初始化并读取末尾若干行作为初始内容。
    // 参数 source：来源名称，用于界面展示。
    // 参数 path：日志文件绝对路径。
    // 参数 initialLines：初始加载的尾部行数。
    // 参数 maxBytesPerRead：单次读取字节上限，防止大文件一次性读入。
    // 参数 debounceMs：文件变化去抖延迟（毫秒）。
    public LogFileTail(
        string source,
        string path,
        int initialLines = 2000,
        int maxBytesPerRead = 512 * 1024,
        int debounceMs = 300)
    {
        Source = source;
        _path = path;
        _maxBytesPerRead = Math.Max(4096, maxBytesPerRead);
        _debounceMs = Math.Max(50, debounceMs);
        _channel = Channel.CreateUnbounded<LogLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        PositionAtEnd(initialLines);
        StartWatching();
    }

    // 来源名称。
    public string Source { get; }

    // 日志文件路径。
    public string Path => _path;

    // 异步读取新行的流。
    public IAsyncEnumerable<LogLine> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    // 当前读取位置（字节）。
    public long Position
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    // 文件是否存在。
    public bool Exists => File.Exists(_path);

    // 读取文件尾部若干行，并把读取位置定位到文件末尾。
    private void PositionAtEnd(int initialLines)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var info = new FileInfo(_path);
            // 粗估起点：按每行 200 字节回退，再从实际起点补齐到行首。
            var guess = Math.Max(0, info.Length - (initialLines * 200L));
            var start = FindLineStart(_path, guess);

            foreach (var line in ReadRange(start, info.Length))
            {
                _channel.Writer.TryWrite(Parse(line));
            }

            lock (_gate)
            {
                _position = info.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 初始读失败不影响后续跟随：位置保持 0，文件出现后会读到全部内容。
        }
    }

    // 把给定偏移调整到行首（避免从半行开始读）。
    private static long FindLineStart(string path, long offset)
    {
        if (offset <= 0)
        {
            return 0;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1];
        while (stream.Position < stream.Length)
        {
            var read = stream.Read(buffer, 0, 1);
            if (read == 0)
            {
                break;
            }

            if (buffer[0] == (byte)'\n')
            {
                return stream.Position;
            }
        }

        return offset;
    }

    // 读取 [start, end) 范围内的所有行。
    private IEnumerable<string> ReadRange(long start, long end)
    {
        if (end <= start)
        {
            yield break;
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
            if (stream.Position >= end)
            {
                yield break;
            }
        }
    }

    // 启动文件监听。
    private void StartWatching()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            // 目录还不存在（服务尚未启动过）：不做监听，调用方可稍后重建实例。
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 监听不可用时退化为"不跟随"，但初始内容仍然可读。
        }
    }

    // 文件变化：去抖后触发一次增量读取。
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce?.Dispose();
            _debounce = new Timer(_ => ReadNewContent(), null, _debounceMs, Timeout.Infinite);
        }
    }

    // 从上次位置读到文件末尾。
    private void ReadNewContent()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            long from;
            long length;
            lock (_gate)
            {
                from = _position;
                length = new FileInfo(_path).Length;

                // 文件被截断（日志轮转/清空）时从头读。
                if (length < from)
                {
                    from = 0;
                }
            }

            if (length <= from)
            {
                return;
            }

            // 单次读取量设上限：追加大段内容时分多次推进，避免一次性占用过多内存。
            var end = Math.Min(length, from + _maxBytesPerRead);
            foreach (var line in ReadRange(from, end))
            {
                _channel.Writer.TryWrite(Parse(line));
            }

            lock (_gate)
            {
                _position = end;
            }

            // 还没读完则继续推进。
            if (end < length)
            {
                _ = Task.Run(ReadNewContent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取竞争（文件正被写入）时忽略本次，下次变化会再触发。
        }
    }

    // 把原始行解析为 LogLine。
    private LogLine Parse(string line)
    {
        return new LogLine(Source, ExtractTimestamp(line), DetectLevel(line), line);
    }

    // 从行首提取时间。支持 "HH:mm:ss" 与 "[HH:mm:ss]" 两种写法。
    private static DateTime? ExtractTimestamp(string line)
    {
        var span = line.AsSpan().TrimStart();
        if (span.Length > 0 && span[0] == '[')
        {
            span = span[1..];
        }

        if (span.Length < 8)
        {
            return null;
        }

        var candidate = span[..8];
        return TimeOnly.TryParseExact(candidate, "HH:mm:ss", out var time)
            ? DateTime.Today.Add(time.ToTimeSpan())
            : null;
    }

    // 按关键字推断级别。
    private static LogLevel DetectLevel(string line)
    {
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("失败", StringComparison.Ordinal)
            || line.Contains("出错", StringComparison.Ordinal))
        {
            return LogLevel.Error;
        }

        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase)
            || line.Contains("警告", StringComparison.Ordinal)
            || line.Contains("⚠️", StringComparison.Ordinal))
        {
            return LogLevel.Warn;
        }

        if (line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)
            || line.Contains("TRACE", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        return LogLevel.Info;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _channel.Writer.TryComplete();
    }
}
