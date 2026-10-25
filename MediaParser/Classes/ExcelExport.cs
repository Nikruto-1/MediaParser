using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Xml;
using MediaParser.Models;

namespace MediaParser.Classes
{
    public static class ExcelExport
    {
        private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public static byte[] GenerateExcelFile(List<ResultsModel> results)
        {
            ArgumentNullException.ThrowIfNull(results);
            var hasDeclarations = results.Any(result => result.Details?.Any(detail => detail.Declarations?.Count > 0) == true);
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteXml(archive, "[Content_Types].xml", $$"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                      <Default Extension="xml" ContentType="application/xml"/>
                      <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                      <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                      {{(hasDeclarations ? "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" : "")}}
                    </Types>
                    """);
                WriteXml(archive, "_rels/.rels", """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                    </Relationships>
                    """);
                WriteXml(archive, "xl/workbook.xml", $$"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="Results" sheetId="1" r:id="rId1"/>{{(hasDeclarations ? "<sheet name=\"Declarations\" sheetId=\"2\" r:id=\"rId2\"/>" : "")}}</sheets>
                    </workbook>
                    """);
                WriteXml(archive, "xl/_rels/workbook.xml.rels", $$"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                      {{(hasDeclarations ? "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" : "")}}
                    </Relationships>
                    """);

                WriteSheet(archive, "xl/worksheets/sheet1.xml", 5, writer =>
                {
                    var row = 1;
                    WriteRow(writer, row++, new[] { "Нікнейм", "Час пошуку", "Медіа", "Статус", "URL профілю" });
                    foreach (var result in results)
                    {
                        if (result.Details?.Count > 0)
                        {
                            foreach (var detail in result.Details)
                                WriteRow(writer, row++, new[]
                                {
                                    detail.Identifier, detail.CheckedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty, detail.Site,
                                    $"{detail.Label}: {detail.Reason}" + (string.IsNullOrWhiteSpace(detail.Evidence) ? "" : $" ({detail.Evidence})"), detail.ProfileUrl
                                });
                            continue;
                        }
                        foreach (var mediaResult in result.Results ?? new())
                        {
                            WriteRow(writer, row++, new[]
                            {
                                result.Nickname, result.Date?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                                mediaResult.Key, mediaResult.Value.Status, mediaResult.Value.ProfileUrl
                            });
                        }
                    }
                });
                if (hasDeclarations)
                    WriteSheet(archive, "xl/worksheets/sheet2.xml", 9, writer =>
                    {
                        var row = 1;
                        WriteRow(writer, row++, new[] { "Ідентифікатор", "ПІБ", "Звітний рік", "Тип декларації", "Тип документа", "Посада", "Місце роботи", "Подано (UTC)", "URL декларації" });
                        foreach (var result in results)
                            foreach (var detail in result.Details ?? new())
                                foreach (var declaration in detail.Declarations ?? Array.Empty<DeclarationSummary>())
                                    WriteRow(writer, row++, new[]
                                    {
                                        detail.Identifier, declaration.FullName, declaration.DeclarationYear ?? string.Empty,
                                        declaration.DeclarationType ?? string.Empty, declaration.DocumentType ?? string.Empty,
                                        declaration.Position ?? string.Empty, declaration.Workplace ?? string.Empty,
                                        declaration.SubmittedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty,
                                        declaration.Url
                                    });
                    });
            }
            return stream.ToArray();
        }

        private static void WriteSheet(ZipArchive archive, string name, int columns, Action<XmlWriter> writeRows)
        {
            using var sheetStream = archive.CreateEntry(name).Open();
            using var writer = XmlWriter.Create(sheetStream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            writer.WriteStartDocument();
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            writer.WriteStartElement("cols", SpreadsheetNamespace);
            writer.WriteStartElement("col", SpreadsheetNamespace);
            writer.WriteAttributeString("min", "1");
            writer.WriteAttributeString("max", columns.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", "30");
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData", SpreadsheetNamespace);
            writeRows(writer);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        private static void WriteXml(ZipArchive archive, string name, string xml)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        private static void WriteRow(XmlWriter writer, int row, string[] values)
        {
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", row.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (var column = 0; column < values.Length; column++)
            {
                writer.WriteStartElement("c", SpreadsheetNamespace);
                writer.WriteAttributeString("r", $"{(char)('A' + column)}{row}");
                // Inline strings keep identifiers such as =SUM(...) as literal text.
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", SpreadsheetNamespace);
                writer.WriteStartElement("t", SpreadsheetNamespace);
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                writer.WriteString(CellText(values[column]));
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static string CellText(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var text = new StringBuilder(Math.Min(value.Length, 32767));
            for (var index = 0; index < value.Length && text.Length < 32767; index++)
            {
                var character = value[index];
                if (XmlConvert.IsXmlChar(character)) text.Append(character);
                else if (char.IsHighSurrogate(character) && index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]) && text.Length < 32766)
                {
                    text.Append(character).Append(value[++index]);
                }
            }
            return text.ToString();
        }
    }
}
