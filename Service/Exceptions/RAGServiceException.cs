public class RAGServiceException : Exception
{
    public RAGServiceException(string message) : base(message) { }
    public RAGServiceException(string message, Exception inner) : base(message, inner) { }
}

public class ChromaDBException : RAGServiceException
{
    public ChromaDBException(string message) : base(message) { }
    public ChromaDBException(string message, Exception inner) : base(message, inner) { }
}

public class DocumentProcessingException : Exception
{
    public DocumentProcessingException(string message) : base(message) { }
    public DocumentProcessingException(string message, Exception inner) : base(message, inner) { }
}