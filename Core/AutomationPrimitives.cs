using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>一次模板探测所需的全部参数，供不同自动化模块复用。</summary>
public readonly record struct TemplateProbe(
    string Key,
    TemplateMatcher Matcher,
    ConfigPoint TopLeft,
    ConfigSize Size,
    double Threshold);

public static class TemplateAssets
{
    public static string DirectoryPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Templates");

    public static TemplateMatcher Load(string fileName) =>
        new(Path.Combine(DirectoryPath, fileName));

    public static TemplateMatcher Load(string directory, string fileName) =>
        new(Path.Combine(directory, fileName));
}

public static class TemplateProbes
{
    public static bool TryGetHit(
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        string key,
        out TemplateProbeResult probe)
    {
        if (probes.TryGetValue(key, out TemplateProbeResult? found) && found.IsMatch)
        {
            probe = found;
            return true;
        }

        probe = Empty;
        return false;
    }

    public static TemplateProbeResult GetOrDefault(
        IReadOnlyDictionary<string, TemplateProbeResult> probes, string key) =>
        probes.TryGetValue(key, out TemplateProbeResult? probe) ? probe : Empty;

    public static TemplateProbeResult Empty { get; } =
        new(false, 0, new Point());
}
