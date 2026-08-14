namespace FileSentry.Client;

/// <summary>Represents the persisted FileSentry workflow status.</summary>
public enum FileStatus
{
    /// <summary>The accepted file is quarantined and waiting to be claimed.</summary>
    PendingScan = 0,

    /// <summary>The scanner worker has claimed the quarantined file.</summary>
    Scanning = 1,

    /// <summary>ClamAV returned an exact clean result and content may be downloaded.</summary>
    Clean = 2,

    /// <summary>Malware was detected and stored bytes are unavailable.</summary>
    Infected = 3,

    /// <summary>Scanning did not complete conclusively and content is unavailable.</summary>
    ScanFailed = 4,

    /// <summary>The file was deleted and content is unavailable.</summary>
    Deleted = 5
}
