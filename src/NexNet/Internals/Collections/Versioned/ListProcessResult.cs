namespace NexNet.Internals.Collections.Lists;

internal enum ListProcessResult
{
    Unset,
    Successful,
    DiscardOperation,
    BadOperation,
    OutOfOperationalRange,
    InvalidVersion
}
