using DocumentFormat.OpenXml.Packaging;
using System.Text;
using UglyToad.PdfPig;


public class DocumentProcessor : IDocumentProcessor
{
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(ILogger<DocumentProcessor> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExtractTextAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => await ExtractFromPdfAsync(filePath),
            ".docx" => await ExtractFromDocxAsync(filePath),
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
            return mainPart?.Document.Body?.InnerText ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from DOCX: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process DOCX", ex);
        }
    }
}