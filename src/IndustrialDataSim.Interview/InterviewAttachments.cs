using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Interview;

/// <summary>What an attachment becomes for the model.</summary>
public static class AttachmentKind
{
    /// <summary>A PDF, sent to the model as a document block (base64).</summary>
    public const string Pdf = "pdf";
    /// <summary>A PNG, JPEG, WebP or GIF image, sent as an image block (base64).</summary>
    public const string Image = "image";
    /// <summary>Text, CSV, Markdown or JSON as typed, or an Excel or Word file converted to text; sent as a plain-text document block.</summary>
    public const string Text = "text";
}

/// <summary>
/// A file the user attached to an interview. Stored once, in
/// flowsim_interview_attachments; the conversation history only holds a small
/// reference block (<c>{"type":"attachment","id":…}</c>) that the session
/// expands into the model's document or image block on every call. PDFs and
/// images keep their bytes in <see cref="Data"/>; everything else keeps the
/// extracted text in <see cref="Text"/>.
/// </summary>
public sealed record StoredAttachment(Guid Id, Guid InterviewId, DateTimeOffset CreatedUtc, string Name, string Kind, string MediaType,
    long SizeBytes, byte[]? Data, string? Text, bool Truncated);

/// <summary>
/// Checks an uploaded file and turns it into what the model can read. The file
/// type is decided by its extension and confirmed by its first bytes, so a
/// renamed file is refused rather than sent as something it is not. Excel and
/// Word files are read with the framework's zip and XML readers (no extra
/// package): every sheet becomes CSV text, a document becomes its paragraphs.
/// Every read is bounded, so a hostile or huge file cannot exhaust memory.
/// </summary>
public static class AttachmentReader
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    /// <summary>The provider's limit for one image.</summary>
    public const long MaxImageBytes = 5 * 1024 * 1024;
    /// <summary>Text kept per attachment (about 50,000 tokens); longer text is cut with a note.</summary>
    public const int MaxTextCharacters = 200_000;
    public const int MaxFilesPerMessage = 5;
    public const int MaxFilesPerInterview = 20;
    /// <summary>Uncompressed bytes read from one Excel or Word file, to refuse zip bombs.</summary>
    private const long MaxUnzippedBytes = 64 * 1024 * 1024;
    private const int MaxNameCharacters = 120;

    public const string SupportedList = "PDF, PNG, JPEG, WebP, GIF, Excel (.xlsx), Word (.docx), and text files (.txt, .csv, .tsv, .md, .json)";
    /// <summary>For the page's file picker.</summary>
    public const string AcceptAttribute = ".pdf,.png,.jpg,.jpeg,.webp,.gif,.xlsx,.docx,.txt,.csv,.tsv,.md,.json";

    private static readonly string[] TextExtensions = [".txt", ".csv", ".tsv", ".md", ".json"];

    /// <summary>Reads one upload. Failures are <see cref="RuntimeFailure"/>s with interview.attachment_* codes that say what to do.</summary>
    public static StoredAttachment Read(Guid interviewId, string fileName, byte[] bytes, DateTimeOffset nowUtc)
    {
        string name = CleanName(fileName);
        if (bytes.Length == 0)
            throw new RuntimeFailure("interview.attachment_empty", $"{name} is empty. Attach a file that has content.");
        if (bytes.LongLength > MaxFileBytes)
            throw new RuntimeFailure("interview.attachment_too_large", $"{name} is {Megabytes(bytes.LongLength)} MB. Attach files up to {MaxFileBytes / 1024 / 1024} MB; split or export a smaller part.");
        string extension = Path.GetExtension(name).ToLowerInvariant();
        var id = Guid.NewGuid();
        StoredAttachment Binary(string kind, string mediaType) => new(id, interviewId, nowUtc, name, kind, mediaType, bytes.LongLength, bytes, null, false);
        StoredAttachment Textual(string text, string mediaType)
        {
            bool truncated = text.Length > MaxTextCharacters;
            if (truncated) text = text[..MaxTextCharacters] + $"\n[The file continues; only the first {MaxTextCharacters:N0} characters were kept.]";
            return new(id, interviewId, nowUtc, name, AttachmentKind.Text, mediaType, bytes.LongLength, null, text, truncated);
        }

        switch (extension)
        {
            case ".pdf":
                Require(bytes, "%PDF-"u8, name, "PDF");
                return Binary(AttachmentKind.Pdf, "application/pdf");
            case ".png":
                Require(bytes, [0x89, 0x50, 0x4E, 0x47], name, "PNG image");
                return Image(bytes, name, "image/png", Binary);
            case ".jpg" or ".jpeg":
                Require(bytes, [0xFF, 0xD8, 0xFF], name, "JPEG image");
                return Image(bytes, name, "image/jpeg", Binary);
            case ".gif":
                Require(bytes, "GIF8"u8, name, "GIF image");
                return Image(bytes, name, "image/gif", Binary);
            case ".webp":
                if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
                    throw NotWhatItSays(name, "WebP image");
                return Image(bytes, name, "image/webp", Binary);
            case ".xlsx":
                Require(bytes, "PK"u8, name, "Excel workbook");
                return Textual(ReadWorkbook(bytes, name), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            case ".docx":
                Require(bytes, "PK"u8, name, "Word document");
                return Textual(ReadDocument(bytes, name), "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            case var ext when TextExtensions.Contains(ext):
                return Textual(ReadUtf8(bytes, name), "text/plain");
            case ".xls" or ".doc":
                throw new RuntimeFailure("interview.attachment_type", $"{name} uses the old Office format. Save it as {(extension == ".xls" ? ".xlsx" : ".docx")} (File → Save As) and attach it again.");
            default:
                throw new RuntimeFailure("interview.attachment_type", $"{name} is not a file type the interviewer can read. Attach {SupportedList}.");
        }
    }

    /// <summary>The block the model receives for an attachment.</summary>
    public static JsonArray ModelBlocks(StoredAttachment attachment) => attachment.Kind switch
    {
        AttachmentKind.Pdf => [new JsonObject
        {
            ["type"] = "document",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(attachment.Data!) },
            ["title"] = attachment.Name
        }],
        AttachmentKind.Image =>
        [
            new JsonObject { ["type"] = "text", ["text"] = $"[Attached image: {attachment.Name}]" },
            new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = attachment.MediaType, ["data"] = Convert.ToBase64String(attachment.Data!) }
            }
        ],
        _ => [new JsonObject
        {
            ["type"] = "document",
            ["source"] = new JsonObject { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = attachment.Text ?? "" },
            ["title"] = attachment.Name
        }]
    };

    /// <summary>The reference kept in the stored history in place of the file.</summary>
    public static JsonObject Reference(StoredAttachment attachment) => new()
    {
        ["type"] = "attachment",
        ["id"] = attachment.Id.ToString("D"),
        ["name"] = attachment.Name,
        ["kind"] = attachment.Kind
    };

    private static StoredAttachment Image(byte[] bytes, string name, string mediaType, Func<string, string, StoredAttachment> make)
    {
        if (bytes.LongLength > MaxImageBytes)
            throw new RuntimeFailure("interview.attachment_too_large", $"{name} is {Megabytes(bytes.LongLength)} MB. Images can be up to {MaxImageBytes / 1024 / 1024} MB; export it smaller or as a PDF.");
        return make(AttachmentKind.Image, mediaType);
    }

    private static void Require(byte[] bytes, ReadOnlySpan<byte> start, string name, string what)
    {
        if (bytes.Length < start.Length || !bytes.AsSpan(0, start.Length).SequenceEqual(start)) throw NotWhatItSays(name, what);
    }

    private static RuntimeFailure NotWhatItSays(string name, string what) =>
        new("interview.attachment_unreadable", $"{name} is named like a {what} but its content is not one. Export it again from the program that made it.");

    private static string ReadUtf8(byte[] bytes, string name)
    {
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new RuntimeFailure("interview.attachment_unreadable", $"{name} is not UTF-8 text. Save it as UTF-8 (in Excel: Save As → CSV UTF-8) and attach it again.");
        }
        if (text.Contains('\0'))
            throw new RuntimeFailure("interview.attachment_unreadable", $"{name} contains binary data, not text. Attach the original document instead.");
        return text.TrimStart('﻿');
    }

    // ---------------- Excel and Word ----------------

    private static ZipArchive OpenZip(byte[] bytes, string name, string what)
    {
        try { return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw NotWhatItSays(name, what); }
    }

    private static XDocument? LoadPart(ZipArchive zip, string path, ref long budget, string name)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        if (entry.Length > budget)
            throw new RuntimeFailure("interview.attachment_too_large", $"{name} expands to more than {MaxUnzippedBytes / 1024 / 1024} MB. Attach a smaller file or only the sheets that matter.");
        budget -= entry.Length;
        try
        {
            using var stream = entry.Open();
            using var limited = new LimitedStream(stream, entry.Length);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(limited, settings);
            return XDocument.Load(reader);
        }
        catch (Exception error) when (error is XmlException or InvalidDataException or IOException)
        {
            throw new RuntimeFailure("interview.attachment_unreadable", $"{name} could not be read ({path} is damaged). Open it in its program, save it again and attach it.");
        }
    }

    /// <summary>Each sheet as "## Sheet: name" followed by CSV rows, in workbook order.</summary>
    private static string ReadWorkbook(byte[] bytes, string name)
    {
        using var zip = OpenZip(bytes, name, "Excel workbook");
        long budget = MaxUnzippedBytes;
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        var workbook = LoadPart(zip, "xl/workbook.xml", ref budget, name) ?? throw NotWhatItSays(name, "Excel workbook");
        var rels = LoadPart(zip, "xl/_rels/workbook.xml.rels", ref budget, name);
        var targets = rels?.Root?.Elements(pkg + "Relationship").ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "") ?? [];
        var shared = LoadPart(zip, "xl/sharedStrings.xml", ref budget, name)?.Root?.Elements(main + "si")
            .Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToList() ?? [];

        var text = new StringBuilder();
        foreach (var sheet in workbook.Descendants(main + "sheet"))
        {
            string sheetName = (string?)sheet.Attribute("name") ?? "Sheet";
            string target = targets.GetValueOrDefault((string?)sheet.Attribute(rel + "id") ?? "") ?? "";
            string path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var part = LoadPart(zip, path, ref budget, name);
            if (part is null) continue;
            text.Append("## Sheet: ").AppendLine(sheetName);
            foreach (var row in part.Descendants(main + "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(main + "c"))
                {
                    int column = ColumnIndex((string?)cell.Attribute("r"));
                    while (column > 0 && cells.Count < column) cells.Add("");
                    cells.Add(CellText(cell, main, shared));
                }
                while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                if (cells.Count == 0) continue;
                text.AppendLine(string.Join(',', cells.Select(Csv)));
                if (text.Length > MaxTextCharacters) return text.ToString();
            }
            text.AppendLine();
        }
        if (text.Length == 0) throw new RuntimeFailure("interview.attachment_empty", $"{name} has no cell values. Attach a workbook with data in it.");
        return text.ToString();
    }

    private static string CellText(XElement cell, XNamespace main, List<string> shared)
    {
        string type = (string?)cell.Attribute("t") ?? "";
        if (type == "inlineStr") return string.Concat(cell.Descendants(main + "t").Select(t => t.Value));
        string value = cell.Element(main + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(value, out int index)) return index >= 0 && index < shared.Count ? shared[index] : "";
        if (type == "b") return value == "1" ? "TRUE" : "FALSE";
        return value;
    }

    /// <summary>"C7" → 2 (zero-based); 0 when the reference is missing.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        int column = 0;
        foreach (char c in reference)
        {
            if (c is < 'A' or > 'Z') break;
            column = column * 26 + (c - 'A' + 1);
            if (column > 16_384) return 0;
        }
        return Math.Max(0, column - 1);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>The document's paragraphs in order; table cells separated by tabs, one row per line.</summary>
    private static string ReadDocument(byte[] bytes, string name)
    {
        using var zip = OpenZip(bytes, name, "Word document");
        long budget = MaxUnzippedBytes;
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = LoadPart(zip, "word/document.xml", ref budget, name) ?? throw NotWhatItSays(name, "Word document");
        var body = document.Root?.Element(w + "body");
        if (body is null) throw NotWhatItSays(name, "Word document");
        var text = new StringBuilder();
        foreach (var block in body.Elements())
        {
            if (block.Name == w + "p") text.AppendLine(Paragraph(block, w));
            else if (block.Name == w + "tbl")
                foreach (var row in block.Descendants(w + "tr"))
                    text.AppendLine(string.Join('\t', row.Elements(w + "tc").Select(tc => string.Join(' ', tc.Elements(w + "p").Select(p => Paragraph(p, w))).Trim())));
            if (text.Length > MaxTextCharacters) break;
        }
        string result = text.ToString().Trim();
        if (result.Length == 0) throw new RuntimeFailure("interview.attachment_empty", $"{name} has no text. Attach a document with text in it, or a PDF of it.");
        return result;
    }

    private static string Paragraph(XElement paragraph, XNamespace w)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == w + "t") text.Append(node.Value);
            else if (node.Name == w + "tab") text.Append('\t');
            else if (node.Name == w + "br" || node.Name == w + "cr") text.Append('\n');
        }
        return text.ToString();
    }

    // ---------------- names ----------------

    /// <summary>The file name without any path, control characters or quotes, at most 120 characters.</summary>
    public static string CleanName(string? fileName)
    {
        string name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && c is not '"' and not '<' and not '>').ToArray()).Trim();
        if (name.Length == 0) name = "attachment";
        if (name.Length > MaxNameCharacters)
        {
            string extension = Path.GetExtension(name);
            name = name[..(MaxNameCharacters - extension.Length)] + extension;
        }
        return name;
    }

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stops a zip entry from producing more bytes than its header declared.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long remaining = limit;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0) return 0;
            int read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
