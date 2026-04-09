namespace Gmux.Core.Services;

public class UpdateDownloadException : Exception
{
    public UpdateDownloadException(string message) : base(message) { }
    public UpdateDownloadException(string message, Exception inner) : base(message, inner) { }
}
