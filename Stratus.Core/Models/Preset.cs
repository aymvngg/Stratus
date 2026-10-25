namespace Stratus.Core.Models;

/// <summary>A named, curated set of tweak ids. Presets only ever contain Safe and Recommended tweaks.</summary>
public sealed record Preset(string Id, string Name, string Description, string Glyph, IReadOnlyList<string> TweakIds);
