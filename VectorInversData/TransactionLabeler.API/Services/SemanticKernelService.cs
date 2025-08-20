using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    public interface ISemanticKernelService
    {
        Task<string> ProcessIntelligentQueryWithAdvancedFeaturesAsync(string connectionString, string query, string? sessionId = null);
        Task<List<object>> GetChatHistoryAsync(string sessionId);
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
                    value = new List<ChatMessageInfo>();
                    _chatHistory[sessionId] = value;
                }

                // Add user query to chat history
                value.Add(new ChatMessageInfo(AuthorRole.User, query));

                // Create financial tools instance
                var financialTools = new FinancialTools(_transactionService, connectionString);
                
                // Import the financial tools directly into the kernel
                _kernel.ImportPluginFromObject(financialTools, "FinancialTools");
                
                // Debug: Check if plugin was imported
                Console.WriteLine($"Plugin imported successfully. Available plugins: {_kernel.Plugins.Count}");

                // Build chat history for context window management
                var chatHistory = BuildChatHistoryForKernel(sessionId, query);

                // Create execution settings with function calling enabled
                var executionSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 4000,
                    Temperature = 0.1f,
                    TopP = 0.9f,
                    PresencePenalty = 0.1f,
                    FrequencyPenalty = 0.1f,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() // Enable automatic function calling
                };

                // Convert chat history to a comprehensive prompt that includes the system prompt
                var systemPrompt = GetSystemPrompt();
                var comprehensivePrompt = $"{systemPrompt}\n\n{string.Join("\n", chatHistory.Select(msg => $"{msg.Role}: {msg.Content}"))}\n\nUser Query: {query}\n\nYou MUST use the available FinancialTools functions to get real data. Do not generate fake data.";

                // Use Semantic Kernel's function calling capabilities with the comprehensive prompt
                var result = await _kernel.InvokePromptAsync(comprehensivePrompt, new KernelArguments(executionSettings));
                
                Console.WriteLine($"Semantic Kernel result: {result}");

                if (result != null)
                {
                    // Extract the actual content from the FunctionResult
                    var responseContent = result.ToString();

                    // Add assistant response to chat history
                    value.Add(new ChatMessageInfo(AuthorRole.Assistant, responseContent ?? "No response generated."));

                    // Update context summary for long-term RAG
                    await UpdateContextSummaryAsync(sessionId, query, responseContent ?? "");

                    return responseContent ?? "No response generated.";
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
            var chatHistory = new List<ChatMessageContent>
            {
                // Add system prompt for context
                new(AuthorRole.System, GetSystemPrompt())
            };

            // Add recent chat history for context window management (last 5 messages to reduce token count)
            if (_chatHistory.TryGetValue(sessionId, out List<ChatMessageInfo>? value))
            {
                var recentHistory = value.TakeLast(5); // Reduced from 10 to 5
                foreach (var message in recentHistory)
                {
                    // Truncate long messages to reduce token count
                    var truncatedContent = message.Content.Length > 500 ? message.Content.Substring(0, 500) + "..." : message.Content;
                    chatHistory.Add(new ChatMessageContent(message.Role, truncatedContent));
                }
                
                // Add condensed enhanced context from chat history (shorter version)
                if (recentHistory.Any())
                {
                    var enhancedContext = BuildCondensedContextFromHistory(recentHistory, currentQuery);
                    if (!string.IsNullOrEmpty(enhancedContext))
                    {
                        Console.WriteLine($"🔍 Built context from history: {enhancedContext}");
                        chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Context: {enhancedContext}"));
                    }
                    
                    // Add specific follow-up context for common patterns
                    var followUpContext = BuildFollowUpContext(recentHistory, currentQuery);
                    if (!string.IsNullOrEmpty(followUpContext))
                    {
                        Console.WriteLine($"🔍 Built follow-up context: {followUpContext}");
                        chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Follow-up Context: {followUpContext}"));
                    }
                }
            }

            // Add condensed context summary if available (shorter version)
            if (_contextSummaries.TryGetValue(sessionId, out string? summary))
            {
                var condensedSummary = summary.Length > 300 ? summary.Substring(0, 300) + "..." : summary;
                chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Summary: {condensedSummary}"));
            }

            return chatHistory;
        }

        private string BuildEnhancedContextFromHistory(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            try
            {
                var contextBuilder = new List<string>();
                
                // Extract customer names mentioned in previous queries
                var customerNames = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ExtractCustomerNames(msg.Content))
                    .Distinct()
                    .ToList();
                
                if (customerNames.Any())
                {
                    contextBuilder.Add($"Previously mentioned customers: {string.Join(", ", customerNames)}");
                }

                // Extract categories mentioned in previous queries
                var categories = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ExtractCategories(msg.Content))
                    .Distinct()
                    .ToList();
                
                if (categories.Any())
                {
                    contextBuilder.Add($"Previously mentioned categories: {string.Join(", ", categories)}");
                }

                // Extract time periods mentioned in previous queries
                var timePeriods = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ExtractTimePeriods(msg.Content))
                    .Distinct()
                    .ToList();
                
                if (timePeriods.Any())
                {
                    contextBuilder.Add($"Previously mentioned time periods: {string.Join(", ", timePeriods)}");
                }

                return string.Join(" | ", contextBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building enhanced context: {ex.Message}");
                return "";
            }
        }

        private string BuildCondensedContextFromHistory(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            try
            {
                var contextBuilder = new List<string>();
                
                // Extract customer names from BOTH user queries AND assistant responses
                var customerNames = new List<string>();
                
                // From user queries - look for the most recent customer name
                var userCustomerNames = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ExtractCustomerNames(msg.Content))
                    .Distinct()
                    .ToList();
                customerNames.AddRange(userCustomerNames);
                
                // From assistant responses (extract actual customer names returned)
                var assistantCustomerNames = recentHistory
                    .Where(msg => msg.Role == AuthorRole.Assistant)
                    .SelectMany(msg => ExtractCustomerNamesFromResponse(msg.Content))
                    .Distinct()
                    .ToList();
                customerNames.AddRange(assistantCustomerNames);
                
                // Filter out common words and question words
                var filteredCustomerNames = customerNames
                    .Where(name => !IsCommonWord(name) && !IsQuestionWord(name) && name.Length > 2)
                    .Distinct()
                    .Take(2)
                    .ToList();
                
                if (filteredCustomerNames.Any())
                {
                    contextBuilder.Add($"Customer: {string.Join(", ", filteredCustomerNames)}");
                }

                // Extract categories from BOTH user queries AND assistant responses
                var categories = new List<string>();
                
                // From user queries - look for the most recent category
                var userCategories = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ExtractCategories(msg.Content))
                    .Distinct()
                    .ToList();
                categories.AddRange(userCategories);
                
                // From assistant responses (extract actual categories returned)
                var assistantCategories = recentHistory
                    .Where(msg => msg.Role == AuthorRole.Assistant)
                    .SelectMany(msg => ExtractCategoriesFromResponse(msg.Content))
                    .Distinct()
                    .ToList();
                categories.AddRange(assistantCategories);
                
                // Filter out common words and question words
                var filteredCategories = categories
                    .Where(cat => !IsCommonWord(cat) && !IsQuestionWord(cat) && cat.Length > 2)
                    .Distinct()
                    .Take(3)
                    .ToList();
                
                if (filteredCategories.Any())
                {
                    contextBuilder.Add($"Categories: {string.Join(", ", filteredCategories)}");
                }

                // Extract time periods from BOTH user queries AND assistant responses
                var timePeriods = new List<string>();
                
                // From user queries
                var userTimePeriods = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ExtractTimePeriods(msg.Content))
                    .Distinct()
                    .ToList();
                timePeriods.AddRange(userTimePeriods);
                
                // From assistant responses (extract actual time periods returned)
                var assistantTimePeriods = recentHistory
                    .Where(msg => msg.Role == AuthorRole.Assistant)
                    .SelectMany(msg => ExtractTimePeriodsFromResponse(msg.Content))
                    .Distinct()
                    .ToList();
                timePeriods.AddRange(assistantTimePeriods);
                
                if (timePeriods.Distinct().Any())
                {
                    contextBuilder.Add($"Time: {string.Join(", ", timePeriods.Distinct().Take(2))}");
                }

                return string.Join(" | ", contextBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building condensed context: {ex.Message}");
                return "";
            }
        }

        private string BuildFollowUpContext(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            try
            {
                // Check if this is a follow-up question about transactions
                if (currentQuery.Contains("transactions", StringComparison.OrdinalIgnoreCase) ||
                    currentQuery.Contains("show me", StringComparison.OrdinalIgnoreCase) ||
                    currentQuery.Contains("get", StringComparison.OrdinalIgnoreCase) ||
                    currentQuery.Contains("find", StringComparison.OrdinalIgnoreCase))
                {
                    var contextBuilder = new List<string>();
                    
                    // Look for the most recent customer name from previous context
                    var lastCustomer = recentHistory
                        .Where(msg => msg.Role == AuthorRole.User)
                        .SelectMany(msg => ExtractCustomerNames(msg.Content))
                        .Where(name => !IsCommonWord(name) && !IsQuestionWord(name) && name.Length > 2)
                        .LastOrDefault();
                    
                    if (!string.IsNullOrEmpty(lastCustomer))
                    {
                        contextBuilder.Add($"Use customer: {lastCustomer}");
                    }
                    
                    // Look for the most recent category from previous context
                    var lastCategory = recentHistory
                        .Where(msg => msg.Role == AuthorRole.User)
                        .SelectMany(msg => ExtractCategories(msg.Content))
                        .Where(cat => !IsCommonWord(cat) && !IsQuestionWord(cat) && cat.Length > 2)
                        .LastOrDefault();
                    
                    if (!string.IsNullOrEmpty(lastCategory))
                    {
                        contextBuilder.Add($"Use category: {lastCategory}");
                    }
                    
                    // Look for categories from AI responses (like RGS codes)
                    var responseCategories = recentHistory
                        .Where(msg => msg.Role == AuthorRole.Assistant)
                        .SelectMany(msg => ExtractCategoriesFromResponse(msg.Content))
                        .Where(cat => !IsCommonWord(cat) && !IsQuestionWord(cat) && cat.Length > 2)
                        .ToList();
                    
                    if (responseCategories.Any())
                    {
                        contextBuilder.Add($"Available categories from previous response: {string.Join(", ", responseCategories)}");
                    }
                    
                    // Look for time periods
                    var lastTimePeriod = recentHistory
                        .Where(msg => msg.Role == AuthorRole.User)
                        .SelectMany(msg => ExtractTimePeriods(msg.Content))
                        .LastOrDefault();
                    
                    if (!string.IsNullOrEmpty(lastTimePeriod))
                    {
                        contextBuilder.Add($"Use time period: {lastTimePeriod}");
                    }
                    
                    if (contextBuilder.Any())
                    {
                        return string.Join(" | ", contextBuilder);
                    }
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building follow-up context: {ex.Message}");
                return "";
            }
        }

        private List<string> ExtractCustomerNames(string content)
        {
            var customers = new List<string>();
            
            // Look for patterns like "for [Customer]", "customer [Customer]", "for customer [Customer]"
            var patterns = new[] { "for ", "customer ", "for customer " };
            foreach (var pattern in patterns)
            {
                if (content.Contains(pattern))
                {
                    var parts = content.Split(new[] { pattern }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var words = part.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length > 0)
                        {
                            // Try to extract customer name - could be 1, 2, 3+ words
                            var customerWords = new List<string>();
                            foreach (var word in words)
                            {
                                if (word.Length > 2 && !IsCommonWord(word))
                                {
                                    customerWords.Add(word);
                                }
                                else if (customerWords.Count > 0)
                                {
                                    // Stop if we hit a common word after finding customer words
                                    break;
                                }
                            }
                            
                            if (customerWords.Count > 0)
                            {
                                var customer = string.Join(" ", customerWords);
                                if (customer.Length > 3)
                                {
                                    customers.Add(customer);
                                }
                            }
                        }
                    }
                }
            }
            
            return customers;
        }
        
        private bool IsCommonWord(string word)
        {
            var commonWords = new[] { "the", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by", "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did", "will", "would", "could", "should", "may", "might", "can", "must", "shall" };
            return commonWords.Contains(word.ToLower());
        }

        private List<string> ExtractCategories(string content)
        {
            var categories = new List<string>();
            
            // Look for category-related keywords and extract what follows
            var categoryKeywords = new[] { "category", "categories", "for ", "transactions for ", "spending on ", "related to ", "about " };
            foreach (var keyword in categoryKeywords)
            {
                if (content.Contains(keyword))
                {
                    var parts = content.Split(new[] { keyword }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var words = part.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length > 0)
                        {
                            var categoryWords = new List<string>();
                            foreach (var word in words)
                            {
                                if (word.Length > 2 && !IsCommonWord(word) && !IsQuestionWord(word))
                                {
                                    categoryWords.Add(word);
                                }
                                else if (categoryWords.Count > 0)
                                {
                                    // Stop if we hit a common word after finding category words
                                    break;
                                }
                            }
                            
                            if (categoryWords.Count > 0)
                            {
                                var category = string.Join(" ", categoryWords);
                                if (category.Length > 3)
                                {
                                    categories.Add(category);
                                }
                            }
                        }
                    }
                }
            }
            
            // Look for specific patterns like "X related" or "X-related"
            var relatedPattern = @"(\w+)\s*(?:related|related to)";
            var matches = System.Text.RegularExpressions.Regex.Matches(content, relatedPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var category = match.Groups[1].Value.Trim();
                    if (category.Length > 2 && !IsCommonWord(category))
                    {
                        categories.Add(category);
                    }
                }
            }
            
            return categories;
        }
        
        private bool IsQuestionWord(string word)
        {
            var questionWords = new[] { "what", "when", "where", "who", "why", "how", "which", "whose", "whom" };
            return questionWords.Contains(word.ToLower());
        }

        private List<string> ExtractTimePeriods(string content)
        {
            var timePeriods = new List<string>();
            
            // Look for year mentions
            var yearPattern = @"\b(2024|2025)\b";
            var yearMatches = System.Text.RegularExpressions.Regex.Matches(content, yearPattern);
            foreach (System.Text.RegularExpressions.Match match in yearMatches)
            {
                timePeriods.Add(match.Value);
            }
            
            // Look for quarter mentions with year context
            var quarterPattern = @"\b(Q[1-4])\s*(?:of\s*)?(2024|2025)?\b";
            var quarterMatches = System.Text.RegularExpressions.Regex.Matches(content, quarterPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in quarterMatches)
            {
                var quarter = match.Groups[1].Value;
                var year = match.Groups[2].Value;
                if (!string.IsNullOrEmpty(year))
                {
                    timePeriods.Add($"{quarter} {year}");
                }
                else
                {
                    timePeriods.Add(quarter);
                }
            }
            
            // Look for month mentions
            var months = new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
            foreach (var month in months)
            {
                if (content.Contains(month, StringComparison.OrdinalIgnoreCase))
                {
                    timePeriods.Add(month);
                }
            }
            
            // Look for relative time periods
            var relativePatterns = new[] { "this year", "last year", "current year", "previous year" };
            foreach (var pattern in relativePatterns)
            {
                if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    timePeriods.Add(pattern);
                }
            }
            
            return timePeriods;
        }

        // Extract customer names from AI responses (more sophisticated parsing)
        private List<string> ExtractCustomerNamesFromResponse(string content)
        {
            var customers = new List<string>();
            
            // Look for "for [Customer]" patterns in responses
            if (content.Contains("for ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = content.Split(new[] { "for " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var words = part.Split(new[] { ' ', ',', '.', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 0)
                    {
                        var customerWords = new List<string>();
                        foreach (var word in words)
                        {
                            if (word.Length > 2 && !IsCommonWord(word))
                            {
                                customerWords.Add(word);
                            }
                            else if (customerWords.Count > 0)
                            {
                                // Stop if we hit a common word after finding customer words
                                break;
                            }
                        }
                        
                        if (customerWords.Count > 0)
                        {
                            var customer = string.Join(" ", customerWords);
                            if (customer.Length > 3)
                            {
                                customers.Add(customer);
                            }
                        }
                    }
                }
            }
            
            return customers;
        }

        // Extract categories from AI responses (parse actual returned data)
        private List<string> ExtractCategoriesFromResponse(string content)
        {
            var categories = new List<string>();
            
            // Look for RGS codes in responses
            var rgsPattern = @"RGS Code:\s*([A-Za-z0-9]+)";
            var rgsMatches = System.Text.RegularExpressions.Regex.Matches(content, rgsPattern);
            foreach (System.Text.RegularExpressions.Match match in rgsMatches)
            {
                if (match.Groups.Count > 1)
                {
                    categories.Add(match.Groups[1].Value);
                }
            }
            
            // Look for category descriptions in responses
            var categoryPattern = @"Category:\s*(.+?)(?:\n|$)";
            var categoryMatches = System.Text.RegularExpressions.Regex.Matches(content, categoryPattern);
            foreach (System.Text.RegularExpressions.Match match in categoryMatches)
            {
                if (match.Groups.Count > 1)
                {
                    var category = match.Groups[1].Value.Trim();
                    if (category.Length > 3)
                    {
                        categories.Add(category);
                    }
                }
            }
            
            // Look for any word followed by "related" or "related to"
            var relatedPattern = @"(\w+)\s*(?:related|related to)";
            var relatedMatches = System.Text.RegularExpressions.Regex.Matches(content, relatedPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in relatedMatches)
            {
                if (match.Groups.Count > 1)
                {
                    var category = match.Groups[1].Value.Trim();
                    if (category.Length > 2 && !IsCommonWord(category))
                    {
                        categories.Add(category);
                    }
                }
            }
            
            // Look for any word followed by "categories" or "category"
            var categoryWordPattern = @"(\w+)\s*(?:categories?|category)";
            var categoryWordMatches = System.Text.RegularExpressions.Regex.Matches(content, categoryWordPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in categoryWordMatches)
            {
                if (match.Groups.Count > 1)
                {
                    var category = match.Groups[1].Value.Trim();
                    if (category.Length > 2 && !IsCommonWord(category))
                    {
                        categories.Add(category);
                    }
                }
            }
            
            return categories;
        }

        // Extract time periods from AI responses
        private List<string> ExtractTimePeriodsFromResponse(string content)
        {
            var timePeriods = new List<string>();
            
            // Look for year mentions in responses
            var yearPattern = @"\b(2024|2025)\b";
            var yearMatches = System.Text.RegularExpressions.Regex.Matches(content, yearPattern);
            foreach (System.Text.RegularExpressions.Match match in yearMatches)
            {
                timePeriods.Add(match.Value);
            }
            
            // Look for quarter mentions with year context
            var quarterPattern = @"\b(Q[1-4])\s*(?:of\s*)?(2024|2025)?\b";
            var quarterMatches = System.Text.RegularExpressions.Regex.Matches(content, quarterPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in quarterMatches)
            {
                var quarter = match.Groups[1].Value;
                var year = match.Groups[2].Value;
                if (!string.IsNullOrEmpty(year))
                {
                    timePeriods.Add($"{quarter} {year}");
                }
                else
                {
                    timePeriods.Add(quarter);
                }
            }
            
            // Look for month mentions
            var months = new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
            foreach (var month in months)
            {
                if (content.Contains(month, StringComparison.OrdinalIgnoreCase))
                {
                    timePeriods.Add(month);
                }
            }
            
            return timePeriods;
        }

        private string GetSystemPrompt()
        {
            return @"You are a financial analysis assistant. Use available tools to analyze transaction data.

            CRITICAL: ALL TRANSACTION RESPONSES MUST INCLUDE RGS CODES AND RGS DESCRIPTIONS

            PARAMETER EXTRACTION:
            - Extract numbers for topN: 'top 5', 'top 10', '5 transactions'
            - Extract dates: '2024', '2025', 'Q1 2024', 'Q2 2025', 'January 2024', 'year 2025'
            - Extract categories: 'car repair', 'marketing', 'travel', 'office expenses', 'utilities'
            - CRITICAL: For quarter dates like 'Q2 2025', convert to date range: startDate='2025-04-01', endDate='2025-06-30'

            RESPONSE FORMAT:
            - For SearchCategories results: Show the actual RGS codes and descriptions in a clear list format
            - For GetTopExpenseCategories results: Show the actual category names, RGS codes, and amounts exactly as returned
            - For GetTopTransactionsForCategory results: Show the actual transaction details including description, amount, date, and RGS code
            - For GetCategorySpending results: Show the total spending amount, transaction count, date range, customer, and breakdown by RGS codes
            - If no results found, explain that no transactions match the criteria

            RGS CODE DISPLAY REQUIREMENTS:
            - ALWAYS include RGS codes in transaction responses
            - Format: RGS Code: CODE - DESCRIPTION
            - Example: RGS Code: WBedAutRoa - Reparatie en onderhoud auto's en andere vervoermiddelen
            - For each transaction, show: Description, Amount, Date, RGS Code, RGS Description
            - NEVER omit RGS codes from transaction responses

            CONTEXT AWARENESS:
            - Use information from previous queries to fill in missing parameters
            - If the current query is missing parameters (customer name, year, category), check the chat history for previously mentioned values
            - Maintain conversation flow by referencing previous queries and responses
            - For follow-up questions like 'show me transactions for these categories' or 'what about the costs', use the context from previous responses

            CRITICAL RULES:
            - Make only ONE function call per query, then provide a final response with the actual data
            - Never apologize or say there was an issue - just show the data
            - For category searches, always show the RGS codes and descriptions clearly
            - For transaction searches, always show the actual transaction details
            - MANDATORY: If you receive transaction data, format it clearly showing: Description, Amount, Date, RGS Code, and RGS Description
            - DO NOT make multiple function calls when you already have results - provide the final response immediately
            - NEVER say 'no transactions found' if the function returned actual transaction data
            - ALWAYS show the actual transaction details when they are provided by the function
            - If the function returns an array of transactions, display each transaction with full details
            - TOOL SELECTION: Use SearchCategories ONLY when the user wants to explore available categories. Use GetTopTransactionsForCategory when the user asks for transaction data, transaction details, or transaction lists.
            - EMPTY RESULTS: When a function returns no results (empty array), provide a clear explanation that no data matches the criteria. Do NOT try another function - just explain the empty result.
            - DATA DISPLAY: ALWAYS show the exact data returned by the function. Do NOT provide generic summaries or simplified descriptions. Show RGS codes, descriptions, and amounts exactly as they appear in the function result.
            - FORMATTED DATA: When you receive formatted data from the function, use that exact formatting. Do NOT create your own summaries or reformat the data. The formatted data already contains the proper structure with RGS codes, descriptions, and amounts.
            - EXACT DISPLAY: Copy the formatted data exactly as provided. Do NOT summarize, generalize, or create your own version. The function result already has the correct format.
            - ALL CATEGORIES RULE: When user asks for 'all categories' or 'show all categories', set categoryQuery to empty string (''), NOT to the customer name. The customer name should be set separately in customerName parameter.
            - SPENDING QUERIES RULE: For spending queries like 'how much did we spend on X', 'total spending on X', 'what was our spending on X', 'costs for X', 'expenses for X', use GetCategorySpending tool. Extract the FULL category description from the query and use it as categoryQuery parameter.
            - QUARTER DATE HANDLING: When user mentions quarters like 'Q2 2025', 'Q1 2024', convert to proper date ranges:
              * Q1: startDate='YYYY-01-01', endDate='YYYY-03-31'
              * Q2: startDate='YYYY-04-01', endDate='YYYY-06-30'
              * Q3: startDate='YYYY-07-01', endDate='YYYY-09-30'
              * Q4: startDate='YYYY-10-01', endDate='YYYY-12-31'
            - DATE CONVERSION: Always convert quarter references to actual startDate and endDate parameters";
        }

        public async Task<List<object>> GetChatHistoryAsync(string sessionId)
        {
            if (!_chatHistory.ContainsKey(sessionId))
                return new List<object>();

            // Convert ChatMessageInfo to simple objects for compatibility
            return _chatHistory[sessionId].Select(msg => new { 
                Role = msg.Role.ToString(), 
                Content = msg.Content, 
                Timestamp = msg.Timestamp 
            }).Cast<object>().ToList();
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
                // Create a more intelligent context summary based on the conversation
                var summary = BuildIntelligentContextSummary(query, response);
                
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

        private string BuildIntelligentContextSummary(string query, string response)
        {
            try
            {
                var summaryParts = new List<string>();
                
                // Extract key information from the query
                var customerNames = ExtractCustomerNames(query);
                var categories = ExtractCategories(query);
                var timePeriods = ExtractTimePeriods(query);
                
                if (customerNames.Any())
                {
                    summaryParts.Add($"Customer: {string.Join(", ", customerNames)}");
                }
                
                if (categories.Any())
                {
                    summaryParts.Add($"Categories: {string.Join(", ", categories)}");
                }
                
                if (timePeriods.Any())
                {
                    summaryParts.Add($"Time Periods: {string.Join(", ", timePeriods)}");
                }
                
                // Add the main query context
                summaryParts.Add($"Query: {query.Substring(0, Math.Min(query.Length, 100))}");
                
                // Add response summary
                summaryParts.Add($"Response: {response.Substring(0, Math.Min(response.Length, 150))}");
                
                return string.Join(" | ", summaryParts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building intelligent context summary: {ex.Message}");
                return $"Last query: {query.Substring(0, Math.Min(query.Length, 100))}... | Response: {response.Substring(0, Math.Min(response.Length, 200))}...";
            }
        }


    }
}
