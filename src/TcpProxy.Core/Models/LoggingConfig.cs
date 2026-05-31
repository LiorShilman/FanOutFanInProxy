namespace TcpProxy.Core.Models
{
    public sealed class LoggingConfig
    {
        public string MinimumLevel { get; set; } = "Information";
        public ConsoleLogConfig Console { get; set; } = new();
        public FileLogConfig File { get; set; } = new();
    }

    public sealed class ConsoleLogConfig
    {
        public bool Enabled { get; set; } = true;
    }

    public sealed class FileLogConfig
    {
        public bool Enabled { get; set; } = true;
        public string Path { get; set; } = "logs/proxy-.log";
        public string RollingInterval { get; set; } = "Day";
        public int RetainedFileCount { get; set; } = 30;
    }
}
