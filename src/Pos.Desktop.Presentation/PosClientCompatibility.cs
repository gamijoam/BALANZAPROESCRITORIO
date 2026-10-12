namespace Pos.Desktop.Presentation;

/// <summary>Stable API compatibility version reported by every terminal during authentication.</summary>
public static class PosClientCompatibility
{
    public static string Version { get; } = typeof(PosClientCompatibility).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
