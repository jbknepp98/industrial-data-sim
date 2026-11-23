using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Files attached to an interview: the reader's type checks and Excel and Word
/// conversion, and the session keeping a reference in the stored history while
/// the model receives the file as a document or image block.
/// </summary>
public class AttachmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T18:00:00Z");
    private static readonly Guid Interview = Guid.NewGuid();
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines");

    private static StoredAttachment Read(string name, byte[] bytes) => AttachmentReader.Read(Interview, name, bytes, Now);

    private static string FailureCode(Action read) => Assert.Throws<RuntimeFailure>(read).Error.Code;

    /// <summary>A minimal Office file: the given parts in a zip.</summary>
    internal static byte[] Zip(params (string Path, string Xml)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, xml) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        return buffer.ToArray();
    }

    internal static byte[] Workbook() => Zip(
        ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Equipment" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
        ("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>"""),
        ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Machine</t></si><si><t>Rate</t></si><si><t>Labeler, Line 1</t></si></sst>"""),
        ("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>2</v></c><c r="C2"><v>420</v></c></row></sheetData></worksheet>"""));

    [Fact]
    public void PdfAndImagesAreKeptAsBytesAndTextIsDecoded()
    {
        var pdf = Read("specs.pdf", "%PDF-1.7\n%test"u8.ToArray());
        Assert.Equal((AttachmentKind.Pdf, "application/pdf"), (pdf.Kind, pdf.MediaType));
        Assert.NotNull(pdf.Data);

        var png = Read("hmi.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);
        Assert.Equal((AttachmentKind.Image, "image/png"), (png.Kind, png.MediaType));

        var csv = Read("orders.csv", Encoding.UTF8.GetBytes("\uFEFForder,qty\nSO-1,40\n"));
        Assert.Equal(AttachmentKind.Text, csv.Kind);
        Assert.Equal("order,qty\nSO-1,40\n", csv.Text);
        Assert.Null(csv.Data);
    }

    [Fact]
    public void ExcelSheetsBecomeCsvAndWordBecomesParagraphs()
    {
        var workbook = Read("lines.xlsx", Workbook());
        Assert.Equal(AttachmentKind.Text, workbook.Kind);
        Assert.Equal("## Sheet: Equipment\nMachine,Rate\n\"Labeler, Line 1\",,420\n", workbook.Text!.Replace("\r\n", "\n").TrimEnd() + "\n");

        var document = Read("notes.docx", Zip(("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:r><w:t>Audience: plant manager</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Line</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>OEE</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """)));
        Assert.Equal("Audience: plant manager\nLine\tOEE", document.Text!.Replace("\r\n", "\n"));
    }

    [Fact]
    public void RenamedOversizedAndUnknownFilesAreRefusedWithAFix()
    {
        Assert.Equal("interview.attachment_unreadable", FailureCode(() => Read("report.pdf", "not a pdf"u8.ToArray())));
        Assert.Equal("interview.attachment_unreadable", FailureCode(() => Read("photo.jpg", "%PDF-1.7"u8.ToArray())));
        Assert.Equal("interview.attachment_unreadable", FailureCode(() => Read("data.csv", [0x41, 0xFF, 0xFE])));
        Assert.Equal("interview.attachment_type", FailureCode(() => Read("setup.exe", [0x4D, 0x5A])));
        Assert.Equal("interview.attachment_type", FailureCode(() => Read("old.xls", [0xD0, 0xCF])));
        Assert.Equal("interview.attachment_empty", FailureCode(() => Read("empty.txt", [])));
        Assert.Equal("interview.attachment_too_large", FailureCode(() => Read("big.txt", new byte[AttachmentReader.MaxFileBytes + 1])));
        var bigImage = new byte[AttachmentReader.MaxImageBytes + 1];
        new byte[] { 0x89, 0x50, 0x4E, 0x47 }.CopyTo(bigImage, 0);
        Assert.Equal("interview.attachment_too_large", FailureCode(() => Read("big.png", bigImage)));
    }

    [Fact]
    public void LongTextIsCutWithANoteAndNamesLoseTheirPath()
    {
        var long_ = Read("log.txt", Encoding.UTF8.GetBytes(new string('x', AttachmentReader.MaxTextCharacters + 10)));
        Assert.True(long_.Truncated);
        Assert.EndsWith("characters were kept.]", long_.Text);
        Assert.Equal("evil.txt", AttachmentReader.CleanName("../../etc/evil.txt"));
        Assert.Equal("a.txt", AttachmentReader.CleanName("C:\\Users\\me\\a.txt"));
    }

    // ---------------- the session ----------------

    private sealed class RecordingModel : IInterviewModel
    {
        public List<GageMessageRequest> Requests { get; } = [];
        public Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop)
        {
            Requests.Add(request);
            return Task.FromResult(JsonSerializer.Deserialize<Message>(new JsonObject
            {
                ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "anthropic/claude",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "I read the files." }),
                ["stop_reason"] = "end_turn", ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
            }.ToJsonString())!);
        }
    }

    [Fact]
    public async Task TheHistoryKeepsAReferenceAndTheModelReceivesTheFile()
    {
        var (library, _) = InterviewLibrary.Load(Folder);
        var store = new InMemoryInterviewStore();
        var model = new RecordingModel();
        var session = InterviewSession.Start(library!, model, store, null, () => Now);
        var pdf = AttachmentReader.Read(session.Id, "layout.pdf", "%PDF-1.7 layout"u8.ToArray(), Now);
        var sheet = AttachmentReader.Read(session.Id, "lines.xlsx", Workbook(), Now);
        var image = AttachmentReader.Read(session.Id, "hmi.png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3], Now);
        foreach (var file in new[] { pdf, sheet, image }) store.SaveAttachment(file);

        await session.Send("", attachments: [pdf.Id, sheet.Id, image.Id]);  // files only, no text
        await session.Send("And one more question.");

        // Stored: references only, never the file content.
        string stored = store.Load(session.Id)!.Messages[0].ContentJson;
        Assert.Contains("\"type\":\"attachment\"", stored);
        Assert.DoesNotContain("JVBERi", stored);            // base64 of "%PDF"
        Assert.DoesNotContain("Labeler, Line 1", stored);
        Assert.Equal(2, session.UserMessageCount);

        // Sent: a PDF document, a text document with the sheet as CSV, an image, on every call.
        foreach (var request in model.Requests)
        {
            var first = JsonNode.Parse(JsonSerializer.Serialize(request.Messages[0]))!["content"]!.AsArray().OfType<JsonObject>().ToList();
            var pdfBlock = first.Single(b => b["type"]!.GetValue<string>() == "document" && b["source"]!["type"]!.GetValue<string>() == "base64");
            Assert.Equal("layout.pdf", pdfBlock["title"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(pdf.Data!), pdfBlock["source"]!["data"]!.GetValue<string>());
            var textBlock = first.Single(b => b["type"]!.GetValue<string>() == "document" && b["source"]!["type"]!.GetValue<string>() == "text");
            Assert.Contains("\"Labeler, Line 1\",,420", textBlock["source"]!["data"]!.GetValue<string>());
            Assert.Equal("image/png", first.Single(b => b["type"]!.GetValue<string>() == "image")["source"]!["media_type"]!.GetValue<string>());
            Assert.DoesNotContain(first, b => b["type"]!.GetValue<string>() == "attachment");
        }
        Assert.Equal(JsonSerializer.Serialize(model.Requests[0].Messages[0]), JsonSerializer.Serialize(model.Requests[1].Messages[0]));
    }

    [Fact]
    public async Task AFileFromAnotherInterviewOrTooManyFilesIsRefused()
    {
        var (library, _) = InterviewLibrary.Load(Folder);
        var store = new InMemoryInterviewStore();
        var session = InterviewSession.Start(library!, new RecordingModel(), store, null, () => Now);
        var other = InterviewSession.Start(library!, new RecordingModel(), store, null, () => Now);
        var theirs = AttachmentReader.Read(other.Id, "a.txt", "text"u8.ToArray(), Now);
        store.SaveAttachment(theirs);
        var error = await Assert.ThrowsAsync<RuntimeFailure>(() => session.Send("Look at this.", attachments: [theirs.Id]));
        Assert.Equal("interview.attachment_not_found", error.Error.Code);

        var six = Enumerable.Range(0, 6).Select(i => AttachmentReader.Read(session.Id, $"{i}.txt", "text"u8.ToArray(), Now)).ToList();
        six.ForEach(store.SaveAttachment);
        error = await Assert.ThrowsAsync<RuntimeFailure>(() => session.Send("All of these.", attachments: six.Select(f => f.Id).ToList()));
        Assert.Equal("interview.too_many_attachments", error.Error.Code);
        Assert.Empty(store.Load(session.Id)!.Messages);
    }
}
