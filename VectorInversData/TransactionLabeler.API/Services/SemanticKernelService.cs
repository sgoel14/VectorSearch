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
                // Apply AI-powered context-aware question reframing FIRST for general questions
                var reframedQuestion = await ReframeQuestionWithContextAsync(query, chatHistory);
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



        private string BuildCondensedContextFromHistory(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            return ContextBuilder.BuildCondensedContextFromHistory(recentHistory, currentQuery);
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
            await ContextManager.UpdateContextSummaryAsync(_contextSummaries, sessionId, query, response);
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

        private async Task<string> ReframeQuestionWithContextAsync(string question, List<ChatMessageInfo> chatHistory)
        {
            try
            {
                // If no chat history or only 1 message, no need to reframe
                if (chatHistory == null || chatHistory.Count <= 1)
                {
                    return question;
                }

                Console.WriteLine($"🔄 AI-powered query rewriting for: '{question}'");
                Console.WriteLine($"📊 Chat history contains {chatHistory.Count} messages");

                // Build chat history for the AI model
                var chatHistoryForAI = BuildChatHistoryForQueryRewriting(chatHistory);
                
                // Create the system instruction for query rewriting
                var systemInstruction = @"You are a query rewriting expert. Based on the provided chat history, rephrase the current user question into a complete, standalone question that can be understood without the chat history.

                    IMPORTANT RULES:
                    - Only output the rewritten question and nothing else
                    - Preserve the user's intent and specific details
                    - Add missing context from chat history (customer names, categories, time periods, etc.)
                    - Make the question self-contained and clear
                    - Keep the same tone and style as the original question
                    - If the question is already complete, return it unchanged

                    Example transformations:
                    - 'check in 2024' → 'check transactions for Nova Creations in 2024'
                    - 'car repair' → 'show me car repair transactions for Nova Creations'
                    - 'what about Q2' → 'what are the top expense categories for Nova Creations in Q2 2025'
                    - 'when asked transactions for categories' -> 'fetch the various categories from previous response and call the transactions iteratively for each category'";

                // Create execution settings for query rewriting
                var rewriteSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 200, // Short response for query rewriting
                    Temperature = 0.1f, // Low temperature for consistent rewriting
                    TopP = 0.9f,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.None() // No function calling for rewriting
                };

                // Build the prompt for query rewriting
                var rewritePrompt = $"{systemInstruction}\n\nChat History:\n{string.Join("\n", chatHistoryForAI)}\n\nCurrent User Question: {question}\n\nRewritten Question:";

                // Use the AI model to rewrite the question
                var rewriteResult = await _kernel.InvokePromptAsync(rewritePrompt, new KernelArguments(rewriteSettings));
                
                if (rewriteResult != null)
                {
                    var rewrittenQuestion = rewriteResult.ToString().Trim();
                    
                    // Clean up the response (remove quotes, extra text, etc.)
                    rewrittenQuestion = CleanRewrittenQuestion(rewrittenQuestion);
                    
                    if (!string.IsNullOrEmpty(rewrittenQuestion) && rewrittenQuestion != question)
                    {
                        Console.WriteLine($"✅ AI Query Rewriting: '{question}' → '{rewrittenQuestion}'");
                        return rewrittenQuestion;
                    }
                }

                Console.WriteLine($"❌ AI query rewriting failed or no change needed, keeping original: '{question}'");
                return question;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error in AI query rewriting: {ex.Message}, keeping original question");
                return question;
            }
        }

        private List<string> BuildChatHistoryForQueryRewriting(List<ChatMessageInfo> chatHistory)
        {
            var historyForAI = new List<string>();
            
            // Take last 6 messages (3 user, 3 AI) for context
            var recentHistory = chatHistory.TakeLast(6).ToList();
            
            foreach (var message in recentHistory)
            {
                var role = message.Role == AuthorRole.User ? "User" : "Assistant";
                var content = message.Content.Length > 200 ? message.Content.Substring(0, 200) + "..." : message.Content;
                historyForAI.Add($"{role}: {content}");
            }
            
            return historyForAI;
        }

        private string CleanRewrittenQuestion(string rewrittenQuestion)
        {
            // Remove quotes if present
            rewrittenQuestion = rewrittenQuestion.Trim('"', '\'', '`');
            
            // Remove common AI prefixes
            var prefixesToRemove = new[] { "rewritten question:", "question:", "answer:", "rewritten:", "result:" };
            foreach (var prefix in prefixesToRemove)
            {
                if (rewrittenQuestion.ToLower().StartsWith(prefix.ToLower()))
                {
                    rewrittenQuestion = rewrittenQuestion.Substring(prefix.Length).Trim();
                    break;
                }
            }
            
            return rewrittenQuestion;
        }


















    }
}
