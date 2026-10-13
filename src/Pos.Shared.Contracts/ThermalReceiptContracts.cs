namespace Pos.Shared.Contracts;

/// <summary>Commercial, non-fiscal thermal receipt layout shared by all terminals of one POS installation.</summary>
public sealed record ThermalReceiptTemplate(
    int SchemaVersion,
    int PaperWidthMm,
    IReadOnlyList<string> HeaderLines,
    IReadOnlyList<string> FooterLines,
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> ItemColumns,
    IReadOnlyDictionary<string, string> Labels,
    string Alignment,
    int FontSizePercent);

public sealed record ThermalReceiptTemplateSnapshot(ThermalReceiptTemplate Template, long Revision);
public sealed record SaveThermalReceiptTemplateRequest(ThermalReceiptTemplate? Template);
