using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;
using System.Text.Json;
using System.Linq;
using Azure.AI.OpenAI;
using System.ClientModel;
using Azure; // Added for .TakeLast() and .Select()

namespace TransactionLabeler.API.Services
{
    public interface ISemanticKernelService
    {
        Task<string> ProcessIntelligentQueryWithAdvancedFeaturesAsync(string connectionString, string query, string? sessionId = null);
        Task<List<ChatMessage>> GetChatHistoryAsync(string sessionId);
        Task ClearChatHistoryAsync(string sessionId);
        Task<string> GetContextSummaryAsync(string sessionId);
    }

    public class SemanticKernelService : ISemanticKernelService
    {
        private readonly ITransactionService _transactionService;
        private readonly string _connectionString;
        private readonly Kernel _kernel;
        private readonly Dictionary<string, List<ChatMessageInfo>> _chatHistory;
        private readonly Dictionary<string, string> _contextSummaries;
        private readonly IConfiguration _configuration;

        // Custom message structure to store both role and content
        private class ChatMessageInfo
        {
            public ChatRole Role { get; set; }
            public string Content { get; set; }
            public DateTime Timestamp { get; set; }

            public ChatMessageInfo(ChatRole role, string content)
            {
                Role = role;
                Content = content;
                Timestamp = DateTime.UtcNow;
            }
        }

        public SemanticKernelService(ITransactionService transactionService, string connectionString, IConfiguration configuration)
        {
            _transactionService = transactionService;
            _connectionString = connectionString;
            _configuration = configuration;
            _chatHistory = new Dictionary<string, List<ChatMessageInfo>>();
            _contextSummaries = new Dictionary<string, string>();
            // Initialize Semantic Kernel with the existing IChatClient foundation
            _kernel = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(
                    _configuration["AzureOpenAI:ChatDeploymentName"]!,
                    _configuration["AzureOpenAI:Endpoint"]!,
                    _configuration["AzureOpenAI:Key"]!)
                .Build();
        }

        public async Task<string> ProcessIntelligentQueryWithAdvancedFeaturesAsync(string connectionString, string query, string? sessionId = null)
        {
            try
            {
                sessionId ??= Guid.NewGuid().ToString();
                
                // Initialize or get chat history for this session
                if (!_chatHistory.TryGetValue(sessionId, out List<ChatMessageInfo>? value))
                {
                    value = ([]);
                    _chatHistory[sessionId] = value;
                }

                // Add user query to chat history
                value.Add(new ChatMessageInfo(ChatRole.User, query));

                // Create financial tools instance
                var financialTools = new FinancialTools(_transactionService, connectionString);
                
                // Import the financial tools directly into the kernel
                _kernel.ImportPluginFromObject(financialTools, "FinancialTools");

                // Build chat history for context window management
                var chatHistory = BuildChatHistoryForKernel(sessionId, query);

                // Create execution settings with advanced features
                var executionSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 4000,
                    Temperature = 0.1f,
                    TopP = 0.9f,
                    PresencePenalty = 0.1f,
                    FrequencyPenalty = 0.1f
                };

                // Create the chat completion request
                var chatRequest = new ChatHistory(chatHistory);
                chatRequest.AddUserMessage(query);

                // Execute the chat completion with tools using the kernel's chat service
                var chatService = _kernel.GetRequiredService<IChatCompletionService>();
                if (chatService == null)
                {
                    return "Chat completion service not available. Please check the configuration.";
                }

                var result = await chatService.GetChatMessageContentsAsync(chatRequest, executionSettings);

                if (result.Count > 0)
                {
                    var response = result[0];
                    
                    // Add assistant response to chat history
                    value.Add(new ChatMessageInfo(ChatRole.Assistant, response.Content ?? "No response generated."));

                    // Update context summary for long-term RAG
                    await UpdateContextSummaryAsync(sessionId, query, response.Content ?? "");

                    return response.Content ?? "No response generated.";
                }

                return "No response generated from the AI model.";
            }
            catch (Exception ex)
            {
                return $"Error processing query with advanced features: {ex.Message}. Please try a simpler query or use the basic vector search instead.";
            }
        }

        private List<ChatMessageContent> BuildChatHistoryForKernel(string sessionId, string currentQuery)
        {
            var chatHistory = new List<ChatMessageContent>();

            // Add system prompt for context
            chatHistory.Add(new ChatMessageContent(AuthorRole.System, GetSystemPrompt()));

            // Add recent chat history for context window management (last 10 messages)
            if (_chatHistory.ContainsKey(sessionId))
            {
                var recentHistory = _chatHistory[sessionId].TakeLast(10);
                foreach (var message in recentHistory)
                {
                    var role = message.Role == ChatRole.User ? AuthorRole.User : AuthorRole.Assistant;
                    chatHistory.Add(new ChatMessageContent(role, message.Content));
                }
            }

            // Add context summary if available
            if (_contextSummaries.ContainsKey(sessionId))
            {
                chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Context Summary: {_contextSummaries[sessionId]}"));
            }

            return chatHistory;
        }

        private string GetSystemPrompt()
        {
            return @"
                You are a financial analysis assistant with advanced capabilities. You can help users analyze transaction data using the available tools.
                
                AVAILABLE TOOLS:
                - FinancialTools.GetTopExpenseCategoriesFlexible: Returns top expense categories for date ranges
                - FinancialTools.GetTopTransactionsForCategory: Finds transactions for specific categories using semantic search
                - FinancialTools.SearchCategories: Searches for relevant categories using vector similarity
                - FinancialTools.GetCategorySpending: Calculates total spending for specific categories
                
                PARAMETER EXTRACTION:
                - Extract customer names: 'for customer X', 'customer X', 'for X', 'Nova Creations', etc.
                - Extract numbers for topN: 'top 5', 'top 10', '5 transactions', '10 transactions', 'top 10 transactions'
                - Extract dates: '2024', '2025', 'Q1 2024', 'January 2024', 'first half of 2024', 'year 2025', 'last 3 months', 'last 6 months', 'last 12 months', 'last 30 days', 'last 60 days', 'last 90 days', 'last 120 days', 'last 180 days', 'last 365 days'
                - Extract categories: 'staff drink and food', 'car repair', 'marketing', 'travel', 'office expenses', 'utilities like gas electricity', 'housing electricity gas', etc.
                - For category extraction, be flexible: 'category staff drink and food' → 'staff drink and food', 'transactions for marketing' → 'marketing'
                - For spending queries, extract the FULL category description: 'utilities like gas, electricity etc' → 'utilities like gas, electricity etc', 'housing, electricity, gas etc' → 'housing, electricity, gas etc'
                - Extract spending queries: 'how much did we spend on', 'total spending on', 'what was our spending on', 'costs for', 'expenses for'
                
                PARAMETER RULES:
                - Always include topCategories=3 for GetTopTransactionsForCategory calls
                - Always include topN parameter for transaction queries (default to 10 if not specified)
                - Always include customerName when specified in the query
                - Only include year parameter if explicitly mentioned in the query (e.g., 'in 2024', 'for 2023', 'this year', 'year 2025')
                - Do NOT automatically add year filters unless the user specifically asks for a particular year
                - For category queries, extract the category name from phrases like 'category staff drink and food' → 'staff drink and food'
                - CRITICAL: For ""all categories"" requests (e.g., ""show all categories"", ""list all categories"", ""what categories are available""), set categoryQuery to empty string or null, NOT to the customer name
                - CRITICAL: When user asks ""show all categories for X"", set categoryQuery="" and customerName=""X""
                - CRITICAL: When user asks ""what categories does X have"", set categoryQuery="" and customerName=""X""
                - CRITICAL: When user asks ""show me all categories for X"", set categoryQuery="" and customerName=""X""
                
                RESPONSE FORMAT:
                - For SearchCategories results: Show the actual RGS codes and descriptions in a clear list format. Do not apologize or say there was an issue.
                - For GetTopExpenseCategories results: Show the actual category names, RGS codes, and amounts exactly as returned by the function. Do not summarize or generalize.
                - For GetTopTransactionsForCategory results: Show the actual transaction details including description, amount, date, and RGS code
                - For GetCategorySpending results: Show the total spending amount, transaction count, date range, customer (if specified), and breakdown by RGS codes with amounts and transaction counts
                - For other results: Provide a clear summary of the results
                - If no results found, explain that no transactions match the criteria
                
                ADVANCED FEATURES:
                - You have access to chat history and context for better conversation flow
                - You can maintain context across multiple queries in the same session
                - You can provide more intelligent responses based on previous interactions
                - You can suggest follow-up questions based on the current analysis
                
                CRITICAL RULES:
                - Make only ONE function call per query, then provide a final response with the actual data
                - Never apologize or say there was an issue - just show the data
                - For category searches, always show the RGS codes and descriptions clearly
                - For transaction searches, always show the actual transaction details
                - If you receive transaction data, format it clearly showing: Description, Amount, Date, RGS Code, and Category
                - DO NOT make multiple function calls when you already have results - provide the final response immediately
                - NEVER say ""no transactions found"" if the function returned actual transaction data
                - ALWAYS show the actual transaction details when they are provided by the function
                - If the function returns an array of transactions, display each transaction with full details
                - IMPORTANT: When you receive category data from SearchCategories, display the actual categories that were returned. Do not say ""no transactions found"" when categories were successfully returned.
                - IMPORTANT: When you receive transaction data from GetTopTransactionsForCategory, display the actual transactions that were returned. Do not say ""no transactions found"" when transactions were successfully returned.
                - IMPORTANT: When you receive expense data from GetTopExpenseCategories, display the actual expense categories that were returned. Do not say ""no transactions found"" when expense data was successfully returned.
                - TOOL SELECTION: Use SearchCategories ONLY when the user wants to explore available categories. Use GetTopTransactionsForCategory when the user asks for transaction data, transaction details, or transaction lists.
                - EMPTY RESULTS: When a function returns no results (empty array), provide a clear explanation that no data matches the criteria. Do NOT try another function - just explain the empty result.
                - DATA DISPLAY: ALWAYS show the exact data returned by the function. Do NOT provide generic summaries or simplified descriptions. Show RGS codes, descriptions, and amounts exactly as they appear in the function result.
                - FORMATTED DATA: When you receive formatted data from the function, use that exact formatting. Do NOT create your own summaries or reformat the data. The formatted data already contains the proper structure with RGS codes, descriptions, and amounts.
                - EXACT DISPLAY: Copy the formatted data exactly as provided. Do NOT summarize, generalize, or create your own version. The function result already has the correct format.
                - ALL CATEGORIES RULE: When user asks for ""all categories"" or ""show all categories"", set categoryQuery to empty string (""""), NOT to the customer name. The customer name should be set separately in customerName parameter.
                - SPENDING QUERIES RULE: For spending queries like ""how much did we spend on X"", ""total spending on X"", ""what was our spending on X"", ""costs for X"", ""expenses for X"", use GetCategorySpending tool. Extract the FULL category description from the query and use it as categoryQuery parameter. For example: ""utilities like gas, electricity etc"" → categoryQuery=""utilities like gas, electricity etc"", ""housing, electricity, gas etc"" → categoryQuery=""housing, electricity, gas etc"".
                ";
        }

        public async Task<List<ChatMessage>> GetChatHistoryAsync(string sessionId)
        {
            if (!_chatHistory.ContainsKey(sessionId))
                return new List<ChatMessage>();

            // Convert ChatMessageInfo to ChatMessage for compatibility
            return _chatHistory[sessionId].Select(msg => new ChatMessage(msg.Role, msg.Content)).ToList();
        }

        public async Task ClearChatHistoryAsync(string sessionId)
        {
            if (_chatHistory.ContainsKey(sessionId))
            {
                _chatHistory[sessionId].Clear();
            }
            if (_contextSummaries.ContainsKey(sessionId))
            {
                _contextSummaries.Remove(sessionId);
            }
        }

        public async Task<string> GetContextSummaryAsync(string sessionId)
        {
            return _contextSummaries.ContainsKey(sessionId) ? _contextSummaries[sessionId] : "No context available for this session.";
        }

        private async Task UpdateContextSummaryAsync(string sessionId, string query, string response)
        {
            try
            {
                // Create a simple context summary based on the conversation
                var summary = $"Last query: {query.Substring(0, Math.Min(query.Length, 100))}... | Response: {response.Substring(0, Math.Min(response.Length, 200))}...";
                
                if (_contextSummaries.TryGetValue(sessionId, out string? existingSummary))
                {
                    _contextSummaries[sessionId] = $"{existingSummary} | {summary}";
                }
                else
                {
                    _contextSummaries[sessionId] = summary;
                }

                // Limit context summary length to prevent memory issues
                if (_contextSummaries[sessionId].Length > 1000)
                {
                    _contextSummaries[sessionId] = _contextSummaries[sessionId].Substring(0, 1000) + "...";
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail the main operation
                Console.WriteLine($"Error updating context summary: {ex.Message}");
            }
        }
    }
}
