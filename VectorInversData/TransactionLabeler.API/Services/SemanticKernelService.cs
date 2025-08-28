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
        private readonly IChatHistoryService _chatHistoryService;
        private const int MESSAGES_BEFORE_SUMMARY = 5; // Create summary every 5 messages (2.5 conversation turns)


        public SemanticKernelService(ITransactionService transactionService, string connectionString, IConfiguration configuration, IChatHistoryService chatHistoryService)
        {
            _transactionService = transactionService;
            _connectionString = connectionString;
            _configuration = configuration;
            _chatHistoryService = chatHistoryService;
            _chatHistory = new Dictionary<string, List<ChatMessageInfo>>();
            _contextSummaries = new Dictionary<string, string>();
            
            // Initialize Semantic Kernel with the existing IChatClient foundation
            _kernel = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(
                    _configuration["AzureOpenAI:ChatDeploymentName"]!,
                    _configuration["AzureOpenAI:Endpoint"]!,
                    _configuration["AzureOpenAI:Key"]!)
                .Build();

            // Initialize the kernel in helper classes for AI-powered context extraction
            ContextBuilder.InitializeKernel(_kernel);
            ContextManager.InitializeKernel(_kernel);
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

                // Add user message to Azure AI Search
                var userMessage = new ChatMessageInfo(AuthorRole.User, query);
                await _chatHistoryService.AddMessageAsync(sessionId, userMessage);
                Console.WriteLine($"✅ Added user message to Azure AI Search: '{query}'");

                // Let Semantic Kernel intelligently decide which tools to use
                Console.WriteLine($"🤖 Processing query intelligently: {query}");
                var result = await ProcessQueryIntelligentlyAsync(connectionString, query, sessionId);
                
                // Add AI response to Azure AI Search
                var aiMessage = new ChatMessageInfo(AuthorRole.Assistant, result);
                await _chatHistoryService.AddMessageAsync(sessionId, aiMessage);
                Console.WriteLine($"✅ Added AI response to Azure AI Search: '{result.Substring(0, Math.Min(100, result.Length))}...'");

                // Check if we should create an automatic context summary
                await CheckAndCreateAutomaticSummaryAsync(sessionId);

                // Log final chat history state from Azure AI Search
                var finalHistory = await _chatHistoryService.GetChatHistoryAsync(sessionId);
                Console.WriteLine($"📊 Final chat history for session {sessionId} contains {finalHistory.Count} messages:");
                foreach (var msg in finalHistory)
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

        private async Task<string> ProcessQueryIntelligentlyAsync(string connectionString, string query, string sessionId)
        {
            Console.WriteLine($"🚀 ProcessQueryIntelligentlyAsync called with:");
            Console.WriteLine($"   Query: '{query}'");
            
            try
            {
                // Apply AI-powered context-aware question reframing FIRST for general questions
                var reframedQuestion = await ReframeQuestionWithContextAsync(query, sessionId);
                if (reframedQuestion != query)
                {
                    Console.WriteLine($" Context Reframing: '{query}' → '{reframedQuestion}'");
                    query = reframedQuestion; // Use the reframed question
                    
                                         // CRITICAL: Update the chat history with the reframed question for future context analysis
                     // Note: The reframed question will be used for this query, but we don't update previous messages
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
                var chatHistoryForKernel = await BuildChatHistoryForKernelAsync(sessionId, query);

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
                    await _chatHistoryService.AddMessageAsync(sessionId, new ChatMessageInfo(AuthorRole.Assistant, responseContent ?? "No response generated."));

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



        private async Task<List<ChatMessageContent>> BuildChatHistoryForKernelAsync(string sessionId, string currentQuery)
        {
            var chatHistory = new List<ChatMessageContent>
            {
                // Add system prompt for context
                new(AuthorRole.System, GetSystemPrompt())
            };

            // Use vector search to get semantically relevant chat history instead of just recent messages
            var relevantHistory = await _chatHistoryService.GetRelevantChatHistoryAsync(sessionId, currentQuery, 8);
            if (relevantHistory.Any())
            {
                Console.WriteLine($"🔍 Retrieved {relevantHistory.Count} semantically relevant messages for context");
                foreach (var message in relevantHistory)
                {
                    // Truncate long messages to reduce token count
                    var truncatedContent = message.Content.Length > 500 ? message.Content.Substring(0, 500) + "..." : message.Content;
                    chatHistory.Add(new ChatMessageContent(message.Role, truncatedContent));
                }
                
                // Add condensed enhanced context from relevant chat history
                if (relevantHistory.Any())
                {
                    var enhancedContext = await ContextBuilder.BuildCondensedContextFromHistoryAsync(relevantHistory, currentQuery);
                    if (!string.IsNullOrEmpty(enhancedContext))
                    {
                        Console.WriteLine($"🔍 Built context from relevant history: {enhancedContext}");
                        chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Context: {enhancedContext}"));
                    }
                }
            }

            // Use vector search to get semantically relevant context summary
            var relevantContextSummary = await _chatHistoryService.GetRelevantContextSummaryAsync(sessionId, currentQuery);
            if (!string.IsNullOrEmpty(relevantContextSummary))
            {
                var condensedSummary = relevantContextSummary.Length > 300 ? relevantContextSummary.Substring(0, 300) + "..." : relevantContextSummary;
                chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Relevant Summary: {condensedSummary}"));
                Console.WriteLine($"🔍 Added semantically relevant context summary: {condensedSummary.Substring(0, Math.Min(100, condensedSummary.Length))}...");
            }
            else
            {
                // Fallback to regular context summary if vector search doesn't find relevant summary
                var fallbackContextSummary = await _chatHistoryService.GetContextSummaryAsync(sessionId);
                if (!string.IsNullOrEmpty(fallbackContextSummary))
                {
                    var fallbackCondensedSummary = fallbackContextSummary.Length > 300 ? fallbackContextSummary.Substring(0, 300) + "..." : fallbackContextSummary;
                    chatHistory.Add(new ChatMessageContent(AuthorRole.System, $"Summary: {fallbackCondensedSummary}"));
                }
            }

            return chatHistory;
        }

        private static string GetSystemPrompt()
        {
            return @"You are an intelligent AI assistant with expertise in financial analysis AND general knowledge. You can help with both financial questions and general questions about any topic.

            🚀 ULTIMATE POWER: You can now generate and execute custom SQL queries for complex financial analysis!

            INTELLIGENT TOOL SELECTION:
            - Use FinancialTools functions when the user asks for specific financial data analysis, transactions, or spending calculations
            - For general knowledge questions (weather, geography, business concepts, industry information), provide helpful and informative responses using your knowledge
            - You are a helpful AI assistant that can answer both financial and general questions - don't restrict yourself to only financial topics
            - For complex queries that cannot be handled by standard functions, use ExecuteReadOnlySQLQuery to generate custom SQL

            AVAILABLE FINANCIAL TOOLS:
            - FinancialTools.GetTopExpenseCategoriesFlexible: Returns top expense categories for date ranges
            - FinancialTools.GetTopTransactionsForCategory: Finds transactions for specific categories using semantic search
            - FinancialTools.SearchCategories: Searches for relevant categories using vector similarity
            - FinancialTools.GetCategorySpending: Calculates total spending for specific categories
            - 🚀 FinancialTools.ExecuteReadOnlySQLQuery: Execute custom SQL queries for complex analysis

            🗄️ DATABASE SCHEMA FOR SQL GENERATION:
            You have access to these tables and can generate SQL queries:

            Table: 'inversbanktransaction'
            Columns: 
            - amount (decimal): Transaction amount
            - bankaccountname (string): Name of the bank account
            - bankaccountnumber (string): Counter Party Bank account number
            - description (string): Transaction description
            - transactiondate (datetime): Date of transaction
            - transactionidentifier_accountnumber (string): Account identifier
            - rgsCode (string): RGS classification code
            - CategoryEmbedding (vector): Vector embedding for semantic search
            - af_bij (string): Transaction type ('Af' for expenses, 'Bij' for income)
            - customername (string): Name of the customer/company

            Table: 'rgsmapping'
            Columns:
            - rgsCode (string): RGS classification code
            - rgsDescription (string): Human-readable description of the RGS code

            🎯 WHEN TO USE EXECUTEREADONLYSQLQUERY:
            - Complex aggregations: 'Show me total spending by month for 2024'
            - Custom filtering: 'Find all transactions above 1000 euros for Nova Creations'
            - Advanced analysis: 'Show me spending patterns by RGS code for Q1 2025'
            - Custom reports: 'Generate a summary of expenses by category and customer'
            - Complex joins: 'Show me all transactions with their RGS descriptions'
            - Date range analysis: 'Compare spending between Q1 and Q2 2025'
            - Statistical queries: 'What's the average transaction amount by month?'
            - Unknown counterparty analysis: 'Find transactions to/from new/unusual bank accounts'
            - Pattern detection: 'Identify transactions that deviate from historical patterns'
            - Historical comparison: 'Compare current month transactions with previous months'
            
            🎯 UNKNOWN COUNTERPARTY ANALYSIS APPROACH:
            - Use subqueries to identify bank accounts that appear in current period but not in historical periods
            - Compare current month transactions with previous 3-6 months to find new counterparties
            - Use NOT EXISTS or NOT IN with subqueries to find new bank accounts
            - Example: Find transactions where bankaccountnumber NOT IN (SELECT DISTINCT bankaccountnumber FROM previous_periods)

            🔒 SQL SECURITY RULES:
            - ONLY generate SELECT queries (never INSERT, UPDATE, DELETE, DROP, CREATE, ALTER)
            - The function automatically blocks dangerous SQL keywords - but basic SQL constructs are allowed
            - All queries are read-only for security
            - You CAN use: subqueries, date functions, aggregations, JOINs, WHERE clauses, ORDER BY, GROUP BY, TOP
            - You CANNOT use: INSERT, UPDATE, DELETE, DROP, CREATE, ALTER, EXEC, sp_, xp_
            - Use parameterized queries when possible
            - Limit results to reasonable amounts (use TOP clause)
            - For unknown counterparty analysis, use subqueries to compare current vs historical patterns

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
            - For SQL query results: Display the formatted table results clearly
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
            - For unknown counterparty queries: ALWAYS use ExecuteReadOnlySQLQuery to generate and execute SQL
            - SQL queries are safe and allowed - don't be overly cautious about basic SQL constructs
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
            - DATE CONVERSION: Always convert quarter references to actual startDate and endDate parameters
            - SQL QUERY GENERATION: When using ExecuteReadOnlySQLQuery, generate clear, readable SQL with proper formatting and comments. Always include ORDER BY clauses for predictable results and use TOP clauses to limit large result sets.";
        }

        public async Task<List<object>> GetChatHistoryAsync(string sessionId)
        {
            var chatHistory = await _chatHistoryService.GetChatHistoryAsync(sessionId);
            
            // Convert ChatMessageInfo to simple objects for compatibility
            return chatHistory.Select(msg => new
            {
                Role = msg.Role.ToString(),
                msg.Content,
                msg.Timestamp
            }).Cast<object>().ToList();
        }

        public async Task ClearChatHistoryAsync(string sessionId)
        {
            await _chatHistoryService.ClearChatHistoryAsync(sessionId);
            if (_contextSummaries.ContainsKey(sessionId))
            {
                _contextSummaries.Remove(sessionId);
            }
        }

        public async Task<string> GetContextSummaryAsync(string sessionId)
        {
            // Try to get from Azure AI Search first
            var azureSummary = await _chatHistoryService.GetContextSummaryAsync(sessionId);
            if (!string.IsNullOrEmpty(azureSummary))
            {
                return azureSummary;
            }
            
            // Fallback to local dictionary
            return _contextSummaries.TryGetValue(sessionId, out string? value) ? value : "No context available for this session.";
        }





        private async Task UpdateContextSummaryAsync(string sessionId, string query, string response)
        {
            // Update context summary using ContextManager (for local processing)
            await ContextManager.UpdateContextSummaryAsync(_contextSummaries, sessionId, query, response);
            
            // Also update in Azure AI Search
            var summary = _contextSummaries.GetValueOrDefault(sessionId, "");
            if (!string.IsNullOrEmpty(summary))
            {
                await _chatHistoryService.UpdateContextSummaryAsync(sessionId, summary);
            }
        }

        /// <summary>
        /// Automatically creates context summaries every N messages to keep the chat-summaries-vector index populated
        /// </summary>
        private async Task CheckAndCreateAutomaticSummaryAsync(string sessionId)
        {
            try
            {
                // Get current chat history count for this session
                var chatHistory = await _chatHistoryService.GetChatHistoryAsync(sessionId);
                var messageCount = chatHistory.Count;

                Console.WriteLine($"🔍 Checking if automatic summary needed for session {sessionId} (current messages: {messageCount})");

                // Create summary every MESSAGES_BEFORE_SUMMARY messages
                if (messageCount > 0 && messageCount % MESSAGES_BEFORE_SUMMARY == 0)
                {
                    Console.WriteLine($"📝 Creating automatic context summary for session {sessionId} after {messageCount} messages");

                    // Get the last few messages to create a meaningful summary
                    var recentMessages = chatHistory.TakeLast(Math.Min(10, messageCount)).ToList();
                    
                    // Create a simple but informative summary
                    var summaryBuilder = new System.Text.StringBuilder();
                    summaryBuilder.AppendLine($"Session Summary (Last {recentMessages.Count} messages):");
                    
                    foreach (var message in recentMessages)
                    {
                        var role = message.Role == AuthorRole.User ? "User" : "Assistant";
                        var truncatedContent = message.Content.Length > 100 
                            ? message.Content.Substring(0, 100) + "..." 
                            : message.Content;
                        summaryBuilder.AppendLine($"- {role}: {truncatedContent}");
                    }

                    var automaticSummary = summaryBuilder.ToString().Trim();
                    
                    // Store the automatic summary in Azure AI Search
                    await _chatHistoryService.UpdateContextSummaryAsync(sessionId, automaticSummary);
                    
                    Console.WriteLine($"✅ Automatic context summary created and stored for session {sessionId}");
                    Console.WriteLine($"📝 Summary preview: {automaticSummary.Substring(0, Math.Min(100, automaticSummary.Length))}...");
                }
                else
                {
                    Console.WriteLine($"ℹ️ No automatic summary needed yet for session {sessionId} (need {MESSAGES_BEFORE_SUMMARY - (messageCount % MESSAGES_BEFORE_SUMMARY)} more messages)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error creating automatic summary for session {sessionId}: {ex.Message}");
                // Don't throw - this is a background operation that shouldn't break the main flow
            }
        }

        private async Task<string> ReframeQuestionWithContextAsync(string question, string sessionId)
        {
            try
            {
                // Use vector search to get semantically relevant chat history for question reframing
                var relevantHistory = await _chatHistoryService.GetRelevantChatHistoryAsync(sessionId, question, 10);
                if (relevantHistory == null || relevantHistory.Count <= 1)
                {
                    return question;
                }

                Console.WriteLine($"🔄 AI-powered query rewriting for: '{question}'");
                Console.WriteLine($"📊 Retrieved {relevantHistory.Count} semantically relevant messages for reframing");

                // Build chat history for the AI model using relevant messages
                var chatHistoryForAI = BuildChatHistoryForQueryRewriting(relevantHistory);
                
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

        private static List<string> BuildChatHistoryForQueryRewriting(List<ChatMessageInfo> chatHistory)
        {
            var historyForAI = new List<string>();
            
            // Take last 6 messages (5 user, 5 AI) for context
            var recentHistory = chatHistory.TakeLast(10).ToList();
            
            foreach (var message in recentHistory)
            {
                var role = message.Role == AuthorRole.User ? "User" : "Assistant";
                var content = message.Content.Length > 200 ? message.Content.Substring(0, 200) + "..." : message.Content;
                historyForAI.Add($"{role}: {content}");
            }
            
            return historyForAI;
        }

        private static string CleanRewrittenQuestion(string rewrittenQuestion)
        {
            // Remove quotes if present
            rewrittenQuestion = rewrittenQuestion.Trim('"', '\'', '`');
            
            // Remove common AI prefixes
            var prefixesToRemove = new[] { "rewritten question:", "question:", "answer:", "rewritten:", "result:" };
            foreach (var prefix in prefixesToRemove)
            {
                if (rewrittenQuestion.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    rewrittenQuestion = rewrittenQuestion[prefix.Length..].Trim();
                    break;
                }
            }
            
            return rewrittenQuestion;
        }


















    }
}
