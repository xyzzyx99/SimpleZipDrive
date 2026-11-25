namespace SimpleZipDrive.Core;

/// <summary>
///     The user explicitly aborted mounting an archive, rather than merely
///     cancelling a password prompt. Auto-mount must not try another drive.
/// </summary>
public sealed class MountAbortedException : OperationCanceledException
{
    public MountAbortedException() : base("Mount aborted by user.")
    {
    }
}
