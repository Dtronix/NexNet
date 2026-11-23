namespace NexNet.Inspector.Core.Format;

/// <summary>Thrown when a stream is not a readable <c>.nntl</c> file or a block cannot be read.</summary>
public sealed class NntlFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NntlFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the error that caused it.</summary>
    public NntlFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
