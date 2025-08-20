using Microsoft.SemanticKernel.ChatCompletion;

namespace TransactionLabeler.API.Models
{
    /// <summary>
    /// Custom message structure to store both role and content
    /// </summary>
    public class ChatMessageInfo
    {
        public AuthorRole Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }

        public ChatMessageInfo(AuthorRole role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = DateTime.UtcNow;
        }
    }
}
