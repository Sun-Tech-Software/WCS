using System.Diagnostics;
using Log.Interface;

namespace Log.Windows;

public class Writer : IWriter
{
    #region Private Fields

    private readonly EventLog? _eventLogWriter;

    #endregion Private Fields

    #region Public Constructors

    public Writer(string appName, string logName)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (!EventLog.SourceExists(appName)) EventLog.CreateEventSource(appName, logName);

        _eventLogWriter = new EventLog();
        _eventLogWriter.Log = logName;
        _eventLogWriter.Source = appName;
    }

    #endregion Public Constructors

    #region Public Methods

    public void WriteLog(string description, LogEntryType logType)
    {
        try
        {
            Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                    _eventLogWriter?.WriteEntry(description, (EventLogEntryType)logType);
            });
        }
        finally
        {
            Debug.Print(description);
        }
    }

    #endregion Public Methods
}
