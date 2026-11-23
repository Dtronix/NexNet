namespace NexNet.Inspector.Core.Format;

/// <summary>Whole-file maintenance operations.</summary>
public static class NntlFile
{
    /// <summary>
    /// Rebuilds the index footer of a recording in place: scans every block, cuts off a torn tail and any old footer,
    /// then appends a fresh index block and trailer. Backs <c>nexnet-tap repair</c>.
    /// </summary>
    /// <remarks>Damaged regions in the middle of the file stay where they are; the new index just does not point at
    /// them. For a file the writer closed normally the result is byte-identical to the original.</remarks>
    /// <param name="stream">A readable, writable, seekable stream holding the recording from offset 0.</param>
    /// <exception cref="NntlFormatException">The stream does not start with a valid header block.</exception>
    public static NntlRebuildResult RebuildIndex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("Rebuilding the index needs a writable stream.", nameof(stream));

        List<NntlDamage> damage = [];
        NntlIndex index;
        long contentEnd;
        long length;
        using (var source = new NntlBlockSource(stream))
        {
            source.ReadHeader();
            length = source.Length;
            index = source.Scan(damage, out contentEnd);
        }

        stream.SetLength(contentEnd);
        stream.Position = contentEnd;
        using (var buffer = new PooledBufferWriter())
            NntlWriter.WriteFooter(buffer, index, contentEnd, stream);
        stream.Flush();
        return new NntlRebuildResult(index.BlockCount, length - contentEnd, damage);
    }
}
