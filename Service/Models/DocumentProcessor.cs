using DocumentFormat.OpenXml.Packaging;
using NPOI.HWPF;
using NPOI.HWPF.Extractor;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Util;

public class DocumentProcessor : IDocumentProcessor
{
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(ILogger<DocumentProcessor> logger)
    {
        _logger = logger;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<string> ExtractTextAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => await ExtractFromPdfAsync(filePath),
            ".docx" => await ExtractFromDocxAsync(filePath),
            ".doc" => await ExtractFromDocAsync(filePath), // Added .doc support
            _ => await File.ReadAllTextAsync(filePath)
        };
    }

    private async Task<string> ExtractFromPdfAsync(string filePath)
    {
        try
        {
            using var document = PdfDocument.Open(filePath);
            var text = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                text.AppendLine(page.Text);
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from PDF: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process PDF", ex);
        }
    }

    private async Task<string> ExtractFromDocxAsync(string filePath)
    {
        try
        {
            using var document = WordprocessingDocument.Open(filePath, false);
            var mainPart = document.MainDocumentPart;

            if (mainPart?.Document.Body == null)
                return string.Empty;

            var text = new StringBuilder();

            // ✅ FIX: .Elements<Paragraph>() only returns direct children of Body,
            // silently skipping any paragraph nested inside a table (Table -> TableRow ->
            // TableCell -> Paragraph). Policy documents commonly put substantive content
            // (allowance tables, eligibility criteria, checklists) inside tables, so this
            // was extracting only headings/intro text and losing the real content while
            // still indexing the file under its correct name — producing a chunk that
            // looks legitimate but has nothing for the LLM to actually ground on.
            // .Descendants<Paragraph>() walks the whole tree, including table cells.
            foreach (var paragraph in mainPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                var paragraphText = paragraph.InnerText.Trim();
                if (!string.IsNullOrEmpty(paragraphText))
                {
                    text.AppendLine(paragraphText);
                    text.AppendLine();
                }
            }

            // Optional but recommended: also walk tables explicitly so row/column
            // structure isn't flattened into a meaningless run of cell text.
            foreach (var table in mainPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>())
            {
                foreach (var row in table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                {
                    var cells = row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                        .Select(c => c.InnerText.Trim());
                    text.AppendLine(string.Join(" | ", cells));
                }
                text.AppendLine();
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from DOCX: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process DOCX", ex);
        }
    }

    private async Task<string> ExtractFromDocAsync(string filePath)
    {
        try
        {
            // NPOI parsing is primarily synchronous. We wrap it in Task.Run 
            // to prevent blocking the calling thread during heavy file parsing.
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var document = new HWPFDocument(fs);
                var extractor = new WordExtractor(document);

                // WordExtractor automatically handles paragraph boundaries
                return extractor.Text;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from DOC: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process DOC", ex);
        }
    }
}