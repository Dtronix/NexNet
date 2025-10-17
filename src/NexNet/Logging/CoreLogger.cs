using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace NexNet.Logging;

/// <summary>
/// The CoreLogger is an abstract class that provides the base functionality for logging in the application. 
/// It implements the INexusLogger interface and provides the basic structure for logging, including managing the minimum log level, 
/// categorizing log events, and enabling or disabling logging. 
/// It also provides the ability to create new instances of the logger with specific categories and session details.
/// </summary>
/// <typeparam name="TLogger">Implemented logger reference</typeparam>
public abstract class CoreLogger<TLogger> : INexusLogger
    where TLogger : CoreLogger<TLogger>
{
    private NexusLogBehaviors _behaviors;

    /// <summary>
    /// Represents a Stopwatch instance used for tracking the elapsed time since the logger's creation.
    /// This elapsed time is used in the log output to indicate when each log event occurred relative to the start of the logger.
    /// </summary>
    protected readonly Stopwatch Sw;

    /// <summary>
    /// Represents the base logger used in the CoreLogger class.
    /// It is used to manage the minimum log level and to provide a reference for the logger's own instance.
    /// </summary>
    protected readonly TLogger ParentLogger;

    /// <summary>
    /// The path node representing this logger's position in the hierarchy.
    /// Updating this node's segment automatically affects all loggers using this node or its descendants.
    /// </summary>
    private readonly PathNode _pathNode;

    /// <summary>
    /// Cached formatted path string to avoid rebuilding on every access.
    /// </summary>
    private string? _cachedFormattedPath;

    /// <summary>
    /// The version sum when the path was last cached.
    /// </summary>
    private int _cachedVersionSum = -1;

    /// <summary>
    /// Represents the minimum log level for the base logger.
    /// Log events with a level lower than this will not be processed by the base logger.
    /// </summary>
    private NexusLogLevel _thisMinLogLevel = NexusLogLevel.Trace;

    /// <inheritdoc />
    public NexusLogBehaviors Behaviors
    {
        get => ParentLogger._behaviors;
        set => ParentLogger._behaviors = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether logging is enabled or disabled.
    /// The default value is true.
    /// </summary>
    public bool LogEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum log level for the logger.
    /// Log events with a level lower than this will not be processed by the logger.
    /// </summary>
    /// <value>
    /// The minimum log level for the logger. The default value is <see cref="NexusLogLevel.Trace"/>.
    /// </value>
    public NexusLogLevel MinLogLevel
    {
        get => ParentLogger._thisMinLogLevel;
        set => ParentLogger._thisMinLogLevel = value;
    }

    /// <inheritdoc />
    public string FormattedPath
    {
        get
        {
            // Fast check: compare version sum to detect if any ancestor's segment has changed
            var currentVersionSum = _pathNode.CalculateVersionSum();

            if (_cachedFormattedPath == null || _cachedVersionSum != currentVersionSum)
            {
                // Cache is invalid, rebuild the path
                var currentPath = _pathNode.BuildPath();
                _cachedFormattedPath = currentPath.Count == 0 ? "" : string.Join("|", currentPath);
                _cachedVersionSum = currentVersionSum;
            }

            return _cachedFormattedPath;
        }
    }

    /// <inheritdoc />
    public string? PathSegment
    {
        get => _pathNode.Segment;
        set => _pathNode.Segment = value;
    }

    /// <summary>
    /// Initializes a new instance of the class with a path segment.
    /// </summary>
    protected CoreLogger(TLogger? parentLogger = null, string? pathSegment = null)
    {
        if (parentLogger == null)
        {
            ParentLogger = (TLogger)this;
            Sw = Stopwatch.StartNew();
        }
        else
        {
            Sw = parentLogger.Sw;
            ParentLogger = parentLogger;
        }
        
        // Create a new path node with the parent's path node as the parent
        _pathNode = new PathNode(parentLogger?._pathNode, pathSegment);
    }

/// <summary>
    /// Formats a log entry into a string representation.
    /// </summary>
    /// <param name="logLevel">The severity level of the log entry.</param>
    /// <param name="category">The category or source of the log entry. Can be null.</param>
    /// <param name="exception">The exception associated with the log entry. Can be null.</param>
    /// <param name="message">The log message content.</param>
    /// <returns>A formatted log string containing timestamp, log level, path, category, message and exception information, or null if logging is disabled or the log level is below the minimum threshold.</returns>
    protected string? GetFormattedLogString(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!ParentLogger.LogEnabled)
            return null;

        if (logLevel < ParentLogger.MinLogLevel)
            return null;

        var logName = logLevel switch
        {
            NexusLogLevel.Trace => "TRA",
            NexusLogLevel.Debug => "DEB",
            NexusLogLevel.Information => "INF",
            NexusLogLevel.Warning => "WAR",
            NexusLogLevel.Error => "ERR",
            NexusLogLevel.Critical => "CRI",
            NexusLogLevel.None => "NON",
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null)
        };
        var time = ParentLogger.Sw.ElapsedTicks / (double)Stopwatch.Frequency;

        // Use pooled StringBuilder for the entire log message
        var sb = StringBuilderPool.Rent();
        try
        {
            sb.Append('[');
            sb.AppendFormat("{0:0.000000}", time);
            sb.Append("][");
            sb.Append(logName);
            sb.Append("] [");

            var path = FormattedPath;
            if (!string.IsNullOrEmpty(path))
            {
                sb.Append(path);
            }

            if (category != null)
            {
                sb.Append('(');
                sb.Append(category);
                sb.Append(')');
            }
            sb.Append("] ");

            sb.Append(message);

            if (exception != null)
            {
                sb.Append(' ');
                sb.Append(exception);
            }

            return sb.ToString();
        }
        finally
        {
            StringBuilderPool.Return(sb);
        }
    }

    /// <inheritdoc />
    public abstract void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message);

    /// <inheritdoc />
    public abstract INexusLogger CreateLogger(string? pathSegment = null);

    /// <summary>
    /// Represents a node in the hierarchical logging path structure.
    /// Each node can have its segment updated, which automatically affects all loggers using this node or its descendants.
    /// </summary>
    private class PathNode
    {
        private string? _segment;
        private int _version = 0;

        /// <summary>
        /// The parent node in the hierarchy, or null if this is the root.
        /// </summary>
        public PathNode? Parent { get; }

        /// <summary>
        /// The path segment for this node.
        /// </summary>
        public string? Segment
        {
            get => _segment;
            set
            {
                _segment = value;
                _version++;
            }
        }

        /// <summary>
        /// Gets the version number for this node. Increments when the segment changes.
        /// </summary>
        public int Version => _version;

        /// <summary>
        /// Initializes a new PathNode with the specified parent and segment.
        /// </summary>
        /// <param name="parent">The parent node, or null for root nodes.</param>
        /// <param name="segment">The path segment for this node.</param>
        public PathNode(PathNode? parent, string? segment)
        {
            Parent = parent;
            _segment = segment;
        }

        /// <summary>
        /// Calculates the sum of all version numbers from this node up to the root.
        /// This provides a fast way to detect if any ancestor's segment has changed.
        /// </summary>
        /// <returns>The sum of version numbers from root to this node.</returns>
        public int CalculateVersionSum()
        {
            var sum = 0;
            var current = this;

            while (current != null)
            {
                sum += current.Version;
                current = current.Parent;
            }

            return sum;
        }

        /// <summary>
        /// Builds the complete path from the root to this node.
        /// </summary>
        /// <returns>A list of non-empty segments from root to this node.</returns>
        public List<string> BuildPath()
        {
            var path = new List<string>();
            var current = this;
            var segments = new List<string?>();

            // Collect segments from this node up to root
            while (current != null)
            {
                segments.Add(current.Segment);
                current = current.Parent;
            }

            // Reverse and filter out empty segments
            segments.Reverse();
            foreach (var segment in segments)
            {
                if (!string.IsNullOrWhiteSpace(segment))
                    path.Add(segment);
            }

            return path;
        }
    }
    /// <summary>
    /// Thread-safe pool of StringBuilder instances to reduce allocations.
    /// </summary>
    private static class StringBuilderPool
    {
        private const int MaxPoolSize = 100;
        private const int MaxBuilderCapacity = 1024;
        private static readonly ConcurrentBag<StringBuilder> _pool = new();
        private static int _poolSize = 0;

        /// <summary>
        /// Rents a StringBuilder from the pool or creates a new one if the pool is empty.
        /// </summary>
        public static StringBuilder Rent()
        {
            if (_pool.TryTake(out var sb))
            {
                Interlocked.Decrement(ref _poolSize);
                return sb;
            }

            return new StringBuilder(256); // Initial capacity for typical log messages
        }

        /// <summary>
        /// Returns a StringBuilder to the pool for reuse.
        /// </summary>
        public static void Return(StringBuilder sb)
        {
            ArgumentNullException.ThrowIfNull(sb);

            // Don't pool builders that have grown too large
            if (sb.Capacity > MaxBuilderCapacity)
                return;

            // Don't exceed max pool size
            if (Volatile.Read(ref _poolSize) >= MaxPoolSize)
                return;

            sb.Clear();
            _pool.Add(sb);
            Interlocked.Increment(ref _poolSize);
        }
    }
}
