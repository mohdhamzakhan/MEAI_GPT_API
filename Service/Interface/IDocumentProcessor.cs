public interface IDocumentProcessor
{
    Task<string> ExtractTextAsync(string filePath);
}