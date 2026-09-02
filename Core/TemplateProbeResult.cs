using System.Windows;

namespace BetterMuv.Core;

public sealed record TemplateProbeResult(bool IsMatch, double Score, Point Center);
