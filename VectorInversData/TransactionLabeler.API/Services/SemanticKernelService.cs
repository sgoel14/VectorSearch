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
                // Generate session ID if not provided
                if (string.IsNullOrEmpty(sessionId))
                {
                    sessionId = $"session_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..8]}";
                    Console.WriteLine($"🆔 Generated new session ID: {sessionId}");
                }
                else
                {
                    Console.WriteLine($"🆔 Using existing session ID: {sessionId}");
                }

                // Get or create chat history for this session
                Console.WriteLine($"🔍 Checking _chatHistory dictionary:");
                Console.WriteLine($"   Dictionary contains {_chatHistory.Count} sessions");
                Console.WriteLine($"   Session keys: [{string.Join(", ", _chatHistory.Keys)}]");
                Console.WriteLine($"   Looking for session: {sessionId}");
                Console.WriteLine($"   ContainsKey result: {_chatHistory.ContainsKey(sessionId)}");
                
                if (!_chatHistory.ContainsKey(sessionId))
                {
                    _chatHistory[sessionId] = new List<ChatMessageInfo>();
                    Console.WriteLine($"📝 Created new chat history for session: {sessionId}");
                }
                else
                {
                    Console.WriteLine($"📝 Found existing chat history for session: {sessionId} with {_chatHistory[sessionId].Count} messages");
                }

                // Log current chat history state
                var currentHistory = _chatHistory[sessionId];
                Console.WriteLine($"🔍 Current chat history for session {sessionId}:");
                foreach (var msg in currentHistory)
                {
                    Console.WriteLine($"   {msg.Role}: {msg.Content.Substring(0, Math.Min(100, msg.Content.Length))}...");
                }

                // Add user message to chat history
                var userMessage = new ChatMessageInfo(AuthorRole.User, query);
                currentHistory.Add(userMessage);
                Console.WriteLine($"✅ Added user message to chat history: '{query}'");

                // Log updated chat history count
                Console.WriteLine($"📊 Chat history now contains {currentHistory.Count} messages");

                // Let Semantic Kernel intelligently decide which tools to use
                Console.WriteLine($"🤖 Processing query intelligently: {query}");
                var result = await ProcessQueryIntelligentlyAsync(connectionString, query, sessionId, currentHistory);
                
                // Add AI response to chat history
                var aiMessage = new ChatMessageInfo(AuthorRole.Assistant, result);
                currentHistory.Add(aiMessage);
                Console.WriteLine($"✅ Added AI response to chat history: '{result.Substring(0, Math.Min(100, result.Length))}...'");

                // Log final chat history state
                Console.WriteLine($"📊 Final chat history for session {sessionId} contains {currentHistory.Count} messages:");
                foreach (var msg in currentHistory)
                {
                    Console.WriteLine($"   {msg.Role}: {msg.Content.Substring(0, Math.Min(100, msg.Content.Length))}...");
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in ProcessIntelligentQueryWithAdvancedFeaturesAsync: {ex.Message}");
                throw;
            }
        }

        private async Task<string> ProcessQueryIntelligentlyAsync(string connectionString, string query, string sessionId, List<ChatMessageInfo> chatHistory)
        {
            Console.WriteLine($"🚀 ProcessQueryIntelligentlyAsync called with:");
            Console.WriteLine($"   Query: '{query}'");
            Console.WriteLine($"   Session ID: {sessionId}");
            Console.WriteLine($"   Chat History Count: {chatHistory.Count}");
            Console.WriteLine($"   Chat History Content:");
            foreach (var msg in chatHistory)
            {
                Console.WriteLine($"     {msg.Role}: {msg.Content.Substring(0, Math.Min(100, msg.Content.Length))}...");
            }
            
            try
            {
                // Apply context-aware question reframing FIRST for general questions
                var reframedQuestion = ReframeQuestionWithContext(query, chatHistory);
                if (reframedQuestion != query)
                {
                    Console.WriteLine($"🔄 Context Reframing: '{query}' → '{reframedQuestion}'");
                    query = reframedQuestion; // Use the reframed question
                    
                    // CRITICAL: Update the chat history with the reframed question for future context analysis
                    if (chatHistory.Any())
                    {
                        var lastUserMessage = chatHistory.LastOrDefault(m => m.Role == AuthorRole.User);
                        if (lastUserMessage != null)
                        {
                            lastUserMessage.Content = reframedQuestion; // Update with reframed version
                        }
                    }
                }
                
                // NOW check if this is a business concept question (after reframing)
                if (IsBusinessConceptQuestion(query))
                {
                    return await ProcessBusinessConceptQuestionAsync(query);
                }
                
                // Check if FinancialTools plugin is already imported
                if (!_kernel.Plugins.Any(p => p.Name == "FinancialTools"))
                {
                    // Create financial tools instance
                    var financialTools = new FinancialTools(_transactionService, connectionString);
                    
                    // Import the financial tools into the kernel
                    _kernel.ImportPluginFromObject(financialTools, "FinancialTools");
                    
                    Console.WriteLine($"✅ FinancialTools plugin imported. Available plugins: {_kernel.Plugins.Count}");
                }
                else
                {
                    Console.WriteLine($"✅ FinancialTools plugin already exists. Available plugins: {_kernel.Plugins.Count}");
                }

                // Build chat history for context window management
                var chatHistoryForKernel = BuildChatHistoryForKernel(sessionId, query);

                // Create execution settings with function calling enabled
                var executionSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 4000, // Back to working value for Azure OpenAI
                    Temperature = 0.3f, // Keep the improved creativity
                    TopP = 0.9f,
                    PresencePenalty = 0.1f,
                    FrequencyPenalty = 0.1f,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() // Enable automatic function calling
                };

                // Convert chat history to a comprehensive prompt that includes the system prompt
                var systemPrompt = GetSystemPrompt();
                var comprehensivePrompt = $"{systemPrompt}\n\n{string.Join("\n", chatHistoryForKernel.Select(msg => $"{msg.Role}: {msg.Content}"))}\n\nUser Query: {query}\n\nIMPORTANT: Only use FinancialTools functions if the user is asking for specific financial data analysis, transactions, or spending calculations. For general knowledge questions (weather, geography, business concepts, etc.), provide comprehensive, detailed responses similar to ChatGPT/Gemini quality. Be thorough, informative, and engaging.";

                // Use Semantic Kernel's function calling capabilities with the comprehensive prompt
                var result = await _kernel.InvokePromptAsync(comprehensivePrompt, new KernelArguments(executionSettings));
                
                Console.WriteLine($"Semantic Kernel result: {result}");

                if (result != null)
                {
                    // Extract the actual content from the FunctionResult
                    var responseContent = result.ToString();

                    // Add assistant response to chat history
                    chatHistory.Add(new ChatMessageInfo(AuthorRole.Assistant, responseContent ?? "No response generated."));

                    // Update context summary for long-term RAG
                    await UpdateContextSummaryAsync(sessionId, query, responseContent ?? "");

                    return responseContent ?? "No response generated.";
                }

                return "No response generated from the AI model.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in intelligent processing: {ex.Message}");
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
            return @"You are an intelligent AI assistant with expertise in financial analysis AND general knowledge. You can help with both financial questions and general questions about any topic.

            INTELLIGENT TOOL SELECTION:
            - Use FinancialTools functions when the user asks for specific financial data analysis, transactions, or spending calculations
            - For general knowledge questions (weather, geography, business concepts, industry information), provide helpful and informative responses using your knowledge
            - You are a helpful AI assistant that can answer both financial and general questions - don't restrict yourself to only financial topics

            AVAILABLE FINANCIAL TOOLS:
            - FinancialTools.GetTopExpenseCategoriesFlexible: Returns top expense categories for date ranges
            - FinancialTools.GetTopTransactionsForCategory: Finds transactions for specific categories using semantic search
            - FinancialTools.SearchCategories: Searches for relevant categories using vector similarity
            - FinancialTools.GetCategorySpending: Calculates total spending for specific categories

            WHEN TO USE FINANCIAL TOOLS:
            - User asks for actual transaction data: 'show me transactions for X', 'get transactions for customer Y'
            - User asks for spending calculations: 'how much did we spend on X', 'total spending for Y'
            - User asks for expense analysis: 'top expenses for period Z', 'expense categories for customer A'
            - User asks for specific financial data from the system
            - User asks for real spending data: 'how much did Nova Creations spend on car repairs?'

            WHEN NOT TO USE FINANCIAL TOOLS:
            - General knowledge questions: weather, geography, history, science, cooking, etc.
            - Business concept explanations: 'what are typical expenses for this industry?', 'most relevant expense categories for X business'
            - Industry analysis: 'what expenses do companies in this sector typically have?'
            - Company information: 'what does this company do?', 'main business of X'
            - Educational questions: 'explain machine learning', 'how does photosynthesis work?'
            - Business advice: 'what should a company budget for?', 'typical costs for this type of business'
            - Examples: 'What are typical expenses for an embroidery business?' → General knowledge ✅

            WHEN TO USE GENERAL KNOWLEDGE:
            - User asks about business concepts: 'what are typical expenses for this industry?'
            - User asks about general business knowledge: 'what does this company do?', 'main business of X'
            - User asks for explanations: 'explain expense categories', 'describe business operations'
            - User asks about industry knowledge: 'what expenses do embroidery businesses have?'

            CRITICAL: ALL TRANSACTION RESPONSES MUST INCLUDE RGS CODES AND RGS DESCRIPTIONS

            PARAMETER EXTRACTION:
            - Extract numbers for topN: 'top 5', 'top 10', '5 transactions'
            - Extract dates: '2024', '2025', 'Q1 2024', 'Q2 2025', 'January 2024', 'year 2025'
            - Extract categories: 'car repair', 'marketing', 'travel', 'office expenses', 'utilities'
            - CRITICAL: For quarter dates like 'Q2 2025', convert to date range: startDate='2025-04-01', endDate='2025-06-30'

            RESPONSE FORMAT:
            - For FinancialTools results: Show the actual RGS codes and descriptions in a clear list format
            - For general knowledge questions: Provide comprehensive, detailed responses similar to ChatGPT/Gemini quality
            - Be thorough, informative, and engaging with specific examples and industry insights
            - Structure responses with clear sections when appropriate
            - For any question: Be helpful, informative, and engaging - you're not limited to financial topics

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

        private bool IsBusinessConceptQuestion(string query)
        {
            var lowerQuery = query.ToLower();
            
            // Keywords that indicate business concept questions (NOT financial data requests)
            var businessConceptKeywords = new[]
            {
                "most relevant expense categories for",
                "typical expenses for",
                "what are typical expenses",
                "what expenses do companies",
                "what should a company budget for",
                "typical costs for this type of business",
                "business description",
                "kvk",
                "industry",
                "sector",
                "type of business",
                "business model",
                "operational costs",
                "overhead expenses",
                "business operations"
            };
            
            // Check if the query contains business concept keywords
            return businessConceptKeywords.Any(keyword => lowerQuery.Contains(keyword));
        }

        private async Task<string> ProcessBusinessConceptQuestionAsync(string query)
        {
            try
            {
                // Use the kernel directly for business concept questions without importing financial tools
                var executionSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 4000, // Back to working value for Azure OpenAI
                    Temperature = 0.4f, // Keep the improved creativity
                    TopP = 0.9f,
                    PresencePenalty = 0.1f,
                    FrequencyPenalty = 0.1f
                };

                var businessPrompt = $@"You are a senior business consultant with extensive expertise in various industries and markets. The user is asking about business concepts, typical expenses, or industry analysis.

User Question: {query}

IMPORTANT: Provide a comprehensive, detailed response similar to what ChatGPT or Gemini would provide. Include:

1. **Company/Industry Overview**: Detailed background and positioning
2. **Specific Products/Services**: Name and describe key offerings
3. **Target Market Analysis**: Who they serve and why
4. **Business Model Insights**: How they operate and generate revenue
5. **Market Position**: Their competitive advantages and market share
6. **Operational Details**: Typical costs, expenses, and business processes
7. **Industry Standards**: Best practices and benchmarks
8. **Growth Strategy**: How they expand and evolve
9. **Regional Specifics**: Local market considerations and adaptations
10. **Practical Business Advice**: Actionable insights for users

Be thorough, informative, and engaging. Structure your response with clear sections and provide specific examples when possible. Aim for a response length similar to ChatGPT's detailed company descriptions.";

                var result = await _kernel.InvokePromptAsync(businessPrompt, new KernelArguments(executionSettings));
                
                if (result != null)
                {
                    return result.ToString();
                }
                
                return "I apologize, but I'm unable to provide a response at the moment. Please try rephrasing your question.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing business concept question: {ex.Message}");
                return $"I apologize, but I encountered an error while processing your business question: {ex.Message}";
            }
        }

        private string ReframeQuestionWithContext(string question, List<ChatMessageInfo> chatHistory)
        {
            Console.WriteLine($"🔄 ReframeQuestionWithContext called with question: '{question}'");
            Console.WriteLine($"📊 Chat history contains {chatHistory.Count} messages:");
            foreach (var msg in chatHistory)
            {
                Console.WriteLine($"   {msg.Role}: {msg.Content.Substring(0, Math.Min(100, msg.Content.Length))}...");
            }

            var lowerQuestion = question.ToLower();
            var reframedQuestion = question;

            // Check if this is a follow-up question that needs context
            if (IsFollowUpQuestion(lowerQuestion))
            {
                Console.WriteLine($"✅ Question '{question}' identified as follow-up question");
                
                // Extract context from recent chat history
                var context = ExtractContextFromHistory(chatHistory);
                Console.WriteLine($"🔍 Extracted context - Geography: [{string.Join(", ", context.GeographicContext)}], Topic: [{string.Join(", ", context.TopicContext)}], Entity: [{string.Join(", ", context.EntityContext)}]");
                
                // Add geographic context if available
                if (context.GeographicContext.Any())
                {
                    var geography = context.GeographicContext.First();
                    reframedQuestion = AddContextToQuestion(reframedQuestion, geography);
                    Console.WriteLine($"🌍 Added geographic context '{geography}': '{reframedQuestion}'");
                }

                // Add topic context if available
                if (context.TopicContext.Any())
                {
                    var topic = context.TopicContext.First();
                    reframedQuestion = AddContextToQuestion(reframedQuestion, topic);
                    Console.WriteLine($"📚 Added topic context '{topic}': '{reframedQuestion}'");
                }

                // Add entity context if available
                if (context.EntityContext.Any())
                {
                    var entity = context.EntityContext.First();
                    reframedQuestion = AddContextToQuestion(reframedQuestion, entity);
                    Console.WriteLine($"🏢 Added entity context '{entity}': '{reframedQuestion}'");
                }
            }
            else
            {
                Console.WriteLine($"❌ Question '{question}' NOT identified as follow-up question");
            }

            Console.WriteLine($"🔄 Final reframed question: '{reframedQuestion}'");
            return reframedQuestion;
        }

        private bool IsFollowUpQuestion(string question)
        {
            // Questions that typically need context from previous conversation
            var followUpPatterns = new[]
            {
                "can you recommend",
                "what about",
                "how about",
                "tell me more",
                "explain",
                "describe",
                "what is",
                "who is",
                "where is",
                "when is",
                "why is",
                "how is",
                "recommend",
                "suggest",
                "provide",
                "give me",
                "show me"
            };

            return followUpPatterns.Any(pattern => question.Contains(pattern));
        }

        private string AddContextToQuestion(string question, string context)
        {
            // Don't add context if it's already mentioned in the question
            if (question.ToLower().Contains(context.ToLower()))
            {
                return question;
            }

            // Add context in a natural way
            if (question.EndsWith("?"))
            {
                question = question.TrimEnd('?');
                return $"{question} for {context}?";
            }
            else
            {
                return $"{question} for {context}";
            }
        }

        private class ContextInfo
        {
            public List<string> GeographicContext { get; set; } = new();
            public List<string> TopicContext { get; set; } = new();
            public List<string> EntityContext { get; set; } = new();
            public List<string> DomainContext { get; set; } = new();
            public List<string> TemporalContext { get; set; } = new();
        }

        private ContextInfo ExtractContextFromHistory(List<ChatMessageInfo> chatHistory)
        {
            Console.WriteLine($"🔍 ExtractContextFromHistory called with {chatHistory.Count} messages");
            var context = new ContextInfo();
            
            // Analyze last 3-4 conversation turns for context
            var recentMessages = chatHistory.TakeLast(6).ToList(); // 3 user questions + 3 AI responses
            Console.WriteLine($"📊 Analyzing last {recentMessages.Count} messages for context");
            
            foreach (var message in recentMessages)
            {
                var content = message.Content.ToLower();
                Console.WriteLine($"🔍 Analyzing message ({message.Role}): '{content.Substring(0, Math.Min(100, content.Length))}...'");
                
                // Extract geographic context
                ExtractGeographicContext(content, context);
                
                // Extract topic context
                ExtractTopicContext(content, context);
                
                // Extract entity context
                ExtractEntityContext(content, context);
                
                // Extract domain context
                ExtractDomainContext(content, context);
                
                // Extract temporal context
                ExtractTemporalContext(content, context);
            }

            Console.WriteLine($"🔍 Final extracted context - Geography: [{string.Join(", ", context.GeographicContext)}], Topic: [{string.Join(", ", context.TopicContext)}], Entity: [{string.Join(", ", context.EntityContext)}]");
            return context;
        }

        private void ExtractGeographicContext(string content, ContextInfo context)
        {
            // Countries
            var countries = new[] { "thailand", "netherlands", "germany", "france", "spain", "italy", "uk", "usa", "canada", "australia", "japan", "china", "india", "brazil" };
            foreach (var country in countries)
            {
                if (content.Contains(country) && !context.GeographicContext.Contains(country))
                {
                    context.GeographicContext.Add(country);
                }
            }

            // Cities
            var cities = new[] { "amsterdam", "rotterdam", "the hague", "utrecht", "bangkok", "phuket", "chicago", "new york", "london", "paris", "berlin", "tokyo" };
            foreach (var city in cities)
            {
                if (content.Contains(city) && !context.GeographicContext.Contains(city))
                {
                    context.GeographicContext.Add(city);
                }
            }
        }

        private void ExtractTopicContext(string content, ContextInfo context)
        {
            var topics = new[] { "news", "weather", "business", "technology", "finance", "health", "education", "sports", "entertainment", "politics", "science", "travel" };
            foreach (var topic in topics)
            {
                if (content.Contains(topic) && !context.TopicContext.Contains(topic))
                {
                    context.TopicContext.Add(topic);
                }
            }
        }

        private void ExtractEntityContext(string content, ContextInfo context)
        {
            // Companies and organizations
            var entities = new[] { "visma", "asml", "acorel", "tesla", "apple", "microsoft", "google", "amazon", "netflix", "spotify" };
            foreach (var entity in entities)
            {
                if (content.Contains(entity) && !context.EntityContext.Contains(entity))
                {
                    context.EntityContext.Add(entity);
                }
            }
        }

        private void ExtractDomainContext(string content, ContextInfo context)
        {
            var domains = new[] { "software", "semiconductor", "transport", "mobility", "energy", "banking", "healthcare", "retail", "manufacturing", "consulting" };
            foreach (var domain in domains)
            {
                if (content.Contains(domain) && !context.DomainContext.Contains(domain))
                {
                    context.DomainContext.Add(domain);
                }
            }
        }

        private void ExtractTemporalContext(string content, ContextInfo context)
        {
            var temporal = new[] { "today", "yesterday", "tomorrow", "this week", "this month", "this year", "recent", "latest", "current" };
            foreach (var time in temporal)
            {
                if (content.Contains(time) && !context.TemporalContext.Contains(time))
                {
                    context.TemporalContext.Add(time);
                }
            }
        }


    }
}
