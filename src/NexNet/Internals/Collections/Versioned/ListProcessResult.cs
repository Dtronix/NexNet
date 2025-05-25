namespace NexNet.Internals.Collections.Versioned;

internal enum ListProcessResult
{
    Unset,
    Successful,
    DiscardOperation,
    BadOperation,
    OutOfOperationalRange,
    InvalidVersion
}
