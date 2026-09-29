using System.Collections.Concurrent;

namespace RdpImeHelper;

// フックのコールバックからも呼ばれるため、キューに積むだけにする。書き込みは専用スレッド。
internal static class Logger
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly BlockingCollection<string> Queue = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RdpImeHelper",
        "RdpImeHelper.log");

    public static void Start()
    {
        var thread = new Thread(WriterLoop) { IsBackground = true, Name = "Logger" };
        thread.Start();
    }

    public static void Log(string message)
    {
        Queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
    }

    public static void Stop()
    {
        Queue.CompleteAdding();
    }

    private static void WriterLoop()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            }

            using var writer = new StreamWriter(
                new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                writer.WriteLine(line);
                if (Queue.Count == 0)
                {
                    writer.Flush();
                }
            }
        }
        catch (Exception)
        {
            // ログが書けなくても本体の動作は継続する
        }
    }
}
