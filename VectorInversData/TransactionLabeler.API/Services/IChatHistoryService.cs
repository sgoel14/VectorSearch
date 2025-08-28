using TransactionLabeler.API.Models;

namespace TransactionLabeler.API.Services
{
    public interface IChatHistoryService
    {
        Task<List<ChatMessageInfo>> GetChatHistoryAsync(string sessionId);
        Task<List<ChatMessageInfo>> GetRelevantChatHistoryAsync(string sessionId, string currentQuery, int maxResults = 20);
        Task AddMessageAsync(string sessionId, ChatMessageInfo message);
        Task ClearChatHistoryAsync(string sessionId);
        Task<List<ChatMessageInfo>> SearchSimilarMessagesAsync(string sessionId, string query, int maxResults = 5);
        Task<string> GetContextSummaryAsync(string sessionId);
        Task<string> GetRelevantContextSummaryAsync(string sessionId, string currentQuery);
        Task UpdateContextSummaryAsync(string sessionId, string summary);
    }
}
