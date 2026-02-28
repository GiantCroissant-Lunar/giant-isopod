using Godot;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Routes Microsoft.Extensions.Logging output to Godot's GD.Print/GD.PushWarning/GD.PushError.
/// </summary>
public sealed class GodotLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new GodotLogger(categoryName);
    public void Dispose() { }

    private sealed class GodotLogger(string category) : ILogger
    {
        // Strip namespace prefix for cleaner output
        private readonly string _tag = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = $"[{_tag}] {formatter(state, exception)}";
            if (exception != null) message += $"\n{exception}";

            switch (logLevel)
            {
                case LogLevel.Error or LogLevel.Critical:
                    GD.PushError(message);
                    break;
                case LogLevel.Warning:
                    GD.PushWarning(message);
                    break;
                default:
                    GD.Print(message);
                    break;
            }
        }
    }
}
