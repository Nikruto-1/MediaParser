using System.IO.Compression;
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
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteXml(archive, "[Content_Types].xml", """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                      <Default Extension="xml" ContentType="application/xml"/>
                      <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                      <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                    </Types>
                    """);
                WriteXml(archive, "_rels/.rels", """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                    </Relationships>
                    """);
                WriteXml(archive, "xl/workbook.xml", """
                    <?xml version="1.0" encoding="utf-8"?>
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="Results" sheetId="1" r:id="rId1"/></sheets>
                    </workbook>
                    """);
                WriteXml(archive, "xl/_rels/workbook.xml.rels", """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                    </Relationships>
                    """);

                using var sheetStream = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
                using var writer = XmlWriter.Create(sheetStream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                writer.WriteStartDocument();
                writer.WriteStartElement("worksheet", SpreadsheetNamespace);
                writer.WriteStartElement("cols", SpreadsheetNamespace);
                writer.WriteStartElement("col", SpreadsheetNamespace);
                writer.WriteAttributeString("min", "1");
                writer.WriteAttributeString("max", "5");
                writer.WriteAttributeString("width", "30");
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("sheetData", SpreadsheetNamespace);
                var row = 1;
                WriteRow(writer, row++, new[] { "Нікнейм", "Час пошуку", "Медіа", "Статус", "URL профілю" });
                foreach (var result in results)
                {
                    foreach (var mediaResult in result.Results ?? new())
                    {
                        WriteRow(writer, row++, new[]
                        {
                            result.Nickname, result.Date?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                            mediaResult.Key, mediaResult.Value.Status, mediaResult.Value.ProfileUrl
                        });
                    }
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            return stream.ToArray();
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
