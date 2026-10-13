using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Pos.Desktop.Modules.Abstractions;

namespace Pos.Modules.Reporting.Desktop;

public static class XlsxReportWriter
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static byte[] Write(IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            AddText(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            AddText(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Reporte" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            AddText(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/worksheets/sheet1.xml", stream => WriteSheet(stream, columns, rows));
        }
        return output.ToArray();
    }

    private static void WriteSheet(Stream stream, IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false });
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNs);
        writer.WriteStartElement("sheetData", SpreadsheetNs);
        writer.WriteStartElement("row", SpreadsheetNs); writer.WriteAttributeString("r", "1");
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            WriteCell(writer, $"{ColumnName(columnIndex)}1", columns[columnIndex].Header, PosDesktopReportValueType.Text);
        writer.WriteEndElement();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            writer.WriteStartElement("row", SpreadsheetNs); writer.WriteAttributeString("r", (rowIndex + 2).ToString(CultureInfo.InvariantCulture));
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                rows[rowIndex].TryGetValue(column.Id, out var value);
                WriteCell(writer, $"{ColumnName(columnIndex)}{rowIndex + 2}", value, column.ValueType);
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
    }

    private static void WriteCell(XmlWriter writer, string reference, object? value, PosDesktopReportValueType valueType)
    {
        writer.WriteStartElement("c", SpreadsheetNs); writer.WriteAttributeString("r", reference);
        if (value is bool boolean)
        {
            writer.WriteAttributeString("t", "b"); writer.WriteElementString("v", SpreadsheetNs, boolean ? "1" : "0");
        }
        else if (value is not null && valueType is PosDesktopReportValueType.Decimal or PosDesktopReportValueType.Integer && IsNumeric(value))
        {
            writer.WriteElementString("v", SpreadsheetNs, ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr"); writer.WriteStartElement("is", SpreadsheetNs);
            writer.WriteStartElement("t", SpreadsheetNs); writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            writer.WriteString(FormatText(value)); writer.WriteEndElement(); writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static bool IsNumeric(object value) => value is decimal or int or long or short or byte or double or float;

    private static string FormatText(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
        DateTimeOffset date => date.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.CurrentCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static string ColumnName(int index)
    {
        var value = index + 1; var result = new StringBuilder();
        while (value > 0)
        {
            value--;
            result.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return result.ToString();
    }

    private static void AddText(ZipArchive archive, string path, string value)
        => AddEntry(archive, path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
            writer.Write(value.Trim());
        });

    private static void AddEntry(ZipArchive archive, string path, Action<Stream> write)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
        write(stream);
    }
}
