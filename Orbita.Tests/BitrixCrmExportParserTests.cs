using System.IO.Compression;
using System.Text;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixCrmExportParserTests
{
    [Fact]
    public async Task ParseAsync_HtmlExport_FiltersSelectedStageAndMapsCandidateFields()
    {
        const string html = """
            <meta http-equiv="Content-type" content="text/html;charset=UTF-8" />
            <table><thead><tr>
            <th>ID</th><th>Стадия сделки</th><th>Ответственный</th><th>Название сделки</th>
            <th>Дата создания</th><th>Дата изменения</th><th>Комментарий</th>
            <th>Возраст</th><th>Профессия</th><th>Город</th><th>Контакт</th><th>Контакт: ID</th>
            <th>Контакт: Имя</th><th>Контакт: Фамилия</th><th>Контакт: Отчество</th>
            <th>Контакт: Рабочий телефон</th>
            </tr></thead><tbody>
            <tr><td>10</td><td>ПЕРЕГОВОРЫ</td><td>Менеджер Один</td><td>Не импортировать</td>
            <td>22.08.2026 12:00:00</td><td>22.08.2026 13:00:00</td><td></td>
            <td>30</td><td>Сварщик</td><td>Омск</td><td>Первый Кандидат</td><td>100</td>
            <td>Кандидат</td><td>Первый</td><td></td><td>8 900 000-00-01</td></tr>
            <tr><td>11</td><td>ПЕРЕГОВОРЫ ДОЛГОСРОК</td><td>Менеджер Два</td><td>Импортировать</td>
            <td>22.08.2026 14:00:00</td><td>30.08.2026 10:00:00</td><td>Текст карточки</td>
            <td>44</td><td>Водитель</td><td>Пермь</td><td>Сидоров Сергей Петрович</td><td>101</td>
            <td>Сергей</td><td>Сидоров</td><td>Петрович</td><td>8 900 222-33-44</td></tr>
            </tbody></table>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var snapshot = await new BitrixCrmExportParser().ParseAsync(
            stream,
            "deals.xls",
            [BitrixCrmImportStages.LongTermNegotiations]);

        var deal = Assert.Single(snapshot.Deals);
        Assert.Equal(11, deal.Id);
        Assert.Equal(BitrixCrmImportStages.LongTermNegotiations, deal.StageName, ignoreCase: true);
        Assert.Equal("Сидоров Сергей Петрович", deal.Contact!.FullName);
        Assert.Equal("8 900 222-33-44", deal.Contact.Phone);
        Assert.Equal("44", deal.Fields["FILE_AGE"]);
        Assert.Equal("Водитель", deal.Fields["FILE_PROFESSION"]);
        Assert.Equal("Пермь", deal.Fields["FILE_CITY"]);
        Assert.Equal("Менеджер Два", Assert.Single(snapshot.Users).Value.FullName);
    }

    [Fact]
    public async Task ParseAsync_XlsxExport_ReadsDirectStringCells()
    {
        await using var stream = CreateXlsx();

        var snapshot = await new BitrixCrmExportParser().ParseAsync(
            stream,
            "deals.xlsx",
            [BitrixCrmImportStages.LongTermNegotiations]);

        var deal = Assert.Single(snapshot.Deals);
        Assert.Equal(21, deal.Id);
        Assert.Equal("Петров Пётр Петрович", deal.Contact!.FullName);
        Assert.Equal("+7 999 111-22-33", deal.Contact.Phone);
    }

    private static MemoryStream CreateXlsx()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Сделки" sheetId="1" r:id="rId1" /></sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml" />
                </Relationships>
                """);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1">
                    <c r="A1" t="str"><v>ID</v></c><c r="B1" t="str"><v>Стадия сделки</v></c>
                    <c r="C1" t="str"><v>Ответственный</v></c><c r="D1" t="str"><v>Название сделки</v></c>
                    <c r="E1" t="str"><v>Контакт: Фамилия</v></c><c r="F1" t="str"><v>Контакт: Имя</v></c>
                    <c r="G1" t="str"><v>Контакт: Отчество</v></c><c r="H1" t="str"><v>Контакт: Рабочий телефон</v></c>
                  </row>
                  <row r="2">
                    <c r="A2" t="str"><v>21</v></c><c r="B2" t="str"><v>ПЕРЕГОВОРЫ ДОЛГОСРОК</v></c>
                    <c r="C2" t="str"><v>Менеджер</v></c><c r="D2" t="str"><v>Карточка</v></c>
                    <c r="E2" t="str"><v>Петров</v></c><c r="F2" t="str"><v>Пётр</v></c>
                    <c r="G2" t="str"><v>Петрович</v></c><c r="H2" t="str"><v>+7 999 111-22-33</v></c>
                  </row>
                </sheetData></worksheet>
                """);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
