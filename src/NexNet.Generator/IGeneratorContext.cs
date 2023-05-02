﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NexNet.Generator;

// share context for SourceGenerator and IncrementalGenerator
public interface IGeneratorContext
{
    CancellationToken CancellationToken { get; }
    void ReportDiagnostic(Diagnostic diagnostic);
    void AddSource(string hintName, string source);
    LanguageVersion LanguageVersion { get; }
}

public static class GeneratorContextExtensions
{
    public static bool IsCSharp9OrGreater(this IGeneratorContext context)
    {
        return (int)context.LanguageVersion >= 900; // C# 9 == 900
    }

    public static bool IsCSharp10OrGreater(this IGeneratorContext context)
    {
        return (int)context.LanguageVersion >= 1000; // C# 10 == 1000
    }

    public static bool IsCSharp11OrGreater(this IGeneratorContext context)
    {
        return (int)context.LanguageVersion >= 1100; // C# 11 == 1100
    }
}
