namespace Walis.Core.Jenkins;

/// <summary>The import entry point: decides from the content whether it is a Jenkinsfile or a config.xml.</summary>
public static class JenkinsImporter
{
    public const int MaximumLength = 512 * 1024;

    public static JenkinsImportResult Import(string content, string? preferredName)
    {
        var trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        var name = string.IsNullOrWhiteSpace(preferredName) ? null : preferredName.Trim();

        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<project", StringComparison.Ordinal) ||
            trimmed.StartsWith("<flow-definition", StringComparison.Ordinal) ||
            trimmed.StartsWith("<matrix-project", StringComparison.Ordinal))
            return JenkinsConfigXmlConverter.Convert(content, name);

        return JenkinsfileConverter.Convert(content, name);
    }
}