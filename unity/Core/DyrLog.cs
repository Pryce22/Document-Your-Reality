using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>Central logger for all Document Your Reality scripts.</summary>
public static class DyrLog
{
    /// <summary>Master switch for writing the session log file.</summary>
    public static bool FileLoggingEnabled = true;

    /// <summary>Absolute path of the current session log file ("" until first write).</summary>
    public static string FilePath { get; private set; } = "";

    static StreamWriter _writer;

    /// <summary>Last unhandled exception seen this session ("" if none), for an on-screen status.</summary>
    public static string LastCrash { get; private set; } = "";

    /// <summary>
    /// The recent log, kept in memory so it can be read inside the headset.
    /// </summary>
    /// <remarks>
    /// Scoped storage hides the log file from the headset's own Files app, and reading it off the
    /// device needs a data cable that is not always there. Held here, the same lines can be shown
    /// on the in-headset panel, which nothing can block.
    /// </remarks>
    static readonly System.Collections.Generic.Queue<string> _recent =
        new System.Collections.Generic.Queue<string>();

    const int RecentLineLimit = 60;

    /// <summary>The last lines logged, oldest first.</summary>
    public static string[] RecentLines()
    {
        lock (_recent) { return _recent.ToArray(); }
    }

    static void Remember(string line)
    {
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentLineLimit) _recent.Dequeue();
        }
    }

    static bool _handlerInstalled;

    /// <summary>
    /// Route unhandled exceptions and engine errors into the session log.
    /// </summary>
    /// <remarks>
    /// On a headset an uncaught exception ends the session with no console and no message: the
    /// app simply vanishes back to the launcher. Captured here, the reason survives in the log
    /// file and in <see cref="LastCrash"/>, which the status overlay shows.
    /// </remarks>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void InstallCrashHandler()
    {
        if (_handlerInstalled) return;
        _handlerInstalled = true;

        Application.logMessageReceived += (condition, stackTrace, type) =>
        {
            if (type != LogType.Exception) return;
            LastCrash = condition;
            // Not Write(): Debug.Log from inside a log callback re-enters this handler.
            WriteToFileOnly("FATAL", "crash", condition + "\n" + stackTrace);
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            Exception error = args.ExceptionObject as Exception;
            LastCrash = error != null ? error.Message : "unknown";
            WriteToFileOnly("FATAL", "crash", LastCrash);
        };
    }

    public static void Info(string tag, string msg)  { Write("INFO ", tag, msg, false, false); }
    public static void Warn(string tag, string msg)  { Write("WARN ", tag, msg, true, false); }
    public static void Error(string tag, string msg) { Write("ERROR", tag, msg, false, true); }

    static void Write(string level, string tag, string msg, bool warn, bool error)
    {
        string consoleLine = "[DYR][" + tag + "] " + msg;
        if (error) Debug.LogError(consoleLine);
        else if (warn) Debug.LogWarning(consoleLine);
        else Debug.Log(consoleLine);

        // Kept whatever the filesystem allows: in a headset this is the only readable copy.
        Remember(DateTime.Now.ToString("HH:mm:ss") + " " + level.Trim() + " [" + tag + "] " + msg);

        if (!FileLoggingEnabled) return;
        try
        {
            if (_writer == null) OpenSessionFile();
            _writer.WriteLine(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " | " + level + " | " + tag.PadRight(12) + " | " + msg);
        }
        catch (Exception ex)
        {
            // Logging must never break the app: disable and move on.
            FileLoggingEnabled = false;
            Debug.LogWarning("[DYR][log] File logging disabled (" + ex.Message + ")");
        }
    }

    /// <summary>Write to the session file without touching the Unity console.</summary>
    /// <remarks>Used from the log callback, where a Debug.Log call would recurse.</remarks>
    static void WriteToFileOnly(string level, string tag, string msg)
    {
        if (!FileLoggingEnabled) return;
        try
        {
            if (_writer == null) OpenSessionFile();
            _writer.WriteLine(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " | " + level + " | " + tag.PadRight(12) + " | " + msg);
        }
        catch (Exception)
        {
            FileLoggingEnabled = false;
        }
    }

    /// <summary>
    /// Open this session's log where the headset's own file manager can reach it.
    /// </summary>
    /// <remarks>
    /// persistentDataPath lands under Android/data/&lt;package&gt;, which scoped storage has hidden
    /// from the Files app since Quest v51 (Android 12): written only there, the log needs adb to
    /// read, and adb needs a data cable. /sdcard/Documents is shared storage and stays browsable
    /// on-device, so that is tried first and persistentDataPath is the fallback.
    /// </remarks>
    static void OpenSessionFile()
    {
        string name = "dyr_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".log";
        StringBuilder attempts = new StringBuilder();

        foreach (string dir in CandidateDirectories())
        {
            try
            {
                // CreateDirectory succeeds under scoped storage where writing then fails, which
                // leaves an empty folder and no log: the write is what decides, not the mkdir.
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, name);
                StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine("# Document Your Reality session log - " + DateTime.Now);
                writer.Flush();

                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    writer.Dispose();
                    attempts.Append("\n  ").Append(dir).Append(" -> wrote nothing");
                    continue;
                }

                _writer = writer;
                FilePath = path;
                Debug.Log("[DYR][log] Session log file: " + FilePath);
                return;
            }
            catch (Exception error)
            {
                attempts.Append("\n  ").Append(dir).Append(" -> ").Append(error.Message);
            }
        }

        FileLoggingEnabled = false;
        Debug.LogWarning("[DYR][log] No writable log directory, file logging is off:" + attempts);
    }

    /// <summary>Log directories in order of how easy they are to read back off the device.</summary>
    static System.Collections.Generic.IEnumerable<string> CandidateDirectories()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Shared storage: visible in the headset's Files app under Documents/DocumentYourReality.
        yield return "/sdcard/Documents/DocumentYourReality";
        yield return "/storage/emulated/0/Documents/DocumentYourReality";
#endif
        yield return Path.Combine(Application.persistentDataPath, "dyr_logs");
    }
}
