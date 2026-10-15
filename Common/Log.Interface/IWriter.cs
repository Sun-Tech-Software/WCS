namespace Log.Interface;

public enum LogEntryType : byte
{
    //
    // Riepilogo:
    //     An error event. This indicates a significant problem the user should know about;
    //     usually a loss of functionality or data.
    Error = 1,

    //
    // Riepilogo:
    //     A warning event. This indicates a problem that is not immediately significant,
    //     but that may signify conditions that could cause future problems.
    Warning = 2,

    //
    // Riepilogo:
    //     An information event. This indicates a significant, successful operation.
    Information = 4,

    //
    // Riepilogo:
    //     A success audit event. This indicates a security event that occurs when an audited
    //     access attempt is successful; for example, logging on successfully.
    SuccessAudit = 8,

    //
    // Riepilogo:
    //     A failure audit event. This indicates a security event that occurs when an audited
    //     access attempt fails; for example, a failed attempt to open a file.
    FailureAudit = 0x10
}

public interface IWriter
{
    #region Public Methods

    void WriteLog(string description, LogEntryType logType);

    #endregion Public Methods
}