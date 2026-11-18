namespace NexNet.Generator.Targets;

/// <summary>One file emitted by a target.</summary>
/// <param name="Path">Path relative to the target's output directory, '/' separated.</param>
/// <param name="Content">File content, with '\n' line endings.</param>
internal sealed record GeneratedFile(string Path, string Content);
