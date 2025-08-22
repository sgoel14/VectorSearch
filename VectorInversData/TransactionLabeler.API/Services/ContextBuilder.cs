using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    /// <summary>
    /// Helper class for building and managing context from chat history using AI-powered extraction
    /// </summary>
    public static class ContextBuilder
    {
        private static Kernel? _kernel;

        /// <summary>
        /// Initialize the kernel for AI-powered context extraction
        /// </summary>
        public static void InitializeKernel(Kernel kernel)
        {
            _kernel = kernel;
        }

        /// <summary>
        /// Builds condensed context from recent chat history using AI-powered extraction
        /// </summary>
        public static async Task<string> BuildCondensedContextFromHistoryAsync(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            try
            {
                if (_kernel == null)
                {
                    Console.WriteLine("⚠️ Kernel not initialized, falling back to basic context extraction");
                    return BuildBasicContextFromHistory(recentHistory, currentQuery);
                }

                if (!recentHistory.Any())
                {
                    return "";
                }

                Console.WriteLine($"🤖 AI-powered context extraction for query: '{currentQuery}'");
                Console.WriteLine($"📊 Processing {recentHistory.Count()} messages from chat history");

                // Build chat history for AI analysis
                var chatHistoryForAI = BuildChatHistoryForContextExtraction(recentHistory);
                
                // Create the system instruction for context extraction
                var systemInstruction = @"You are a context extraction expert. Analyze the provided chat history and extract the most relevant context information.

                    IMPORTANT RULES:
                    - Extract ONLY the following information in this exact format:
                      * Customer names (business names, company names, client names)
                      * Categories (expense categories, transaction types, business areas)
                      * Time periods (years, quarters, months, specific dates)
                      * Query types (what the user is asking about)

                    - Format your response EXACTLY like this example:
                      Customer: Company Name 1, Company Name 2
                      Categories: Category 1, Category 2, Category 3
                      Time: 2025, Q2 2025, January 2025
                      Query Type: Transaction Analysis

                    - Rules for extraction:
                      * Customer names: Extract actual business/company names, not generic words
                      * Categories: Extract specific expense categories or transaction types
                      * Time periods: Extract years, quarters, months, or specific dates
                      * Query Type: Identify if this is about transactions, categories, expenses, or general questions

                    - Filter out:
                      * Common words (the, and, or, for, etc.)
                      * Question words (what, when, where, etc.)
                      * Generic terms that don't add context

                    - If no relevant information is found for a category, omit that line entirely
                    - Keep the format clean and consistent
                    - Only output the extracted context, nothing else";

                // Create execution settings for context extraction
                var extractionSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 300, // Short response for context extraction
                    Temperature = 0.1f, // Low temperature for consistent extraction
                    TopP = 0.9f,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.None() // No function calling for extraction
                };

                // Build the prompt for context extraction
                var extractionPrompt = $"{systemInstruction}\n\nChat History:\n{string.Join("\n", chatHistoryForAI)}\n\nCurrent Query: {currentQuery}\n\nExtracted Context:";

                // Use the AI model to extract context
                var extractionResult = await _kernel.InvokePromptAsync(extractionPrompt, new KernelArguments(extractionSettings));
                
                if (extractionResult != null)
                {
                    var extractedContext = extractionResult.ToString().Trim();
                    
                    // Clean up the response
                    extractedContext = CleanExtractedContext(extractedContext);
                    
                    if (!string.IsNullOrEmpty(extractedContext))
                    {
                        Console.WriteLine($"✅ AI Context Extraction Result: {extractedContext}");
                        return extractedContext;
                    }
                }

                Console.WriteLine($"❌ AI context extraction failed, falling back to basic extraction");
                return BuildBasicContextFromHistory(recentHistory, currentQuery);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error in AI context extraction: {ex.Message}, falling back to basic extraction");
                return BuildBasicContextFromHistory(recentHistory, currentQuery);
            }
        }

        /// <summary>
        /// Fallback method for basic context extraction when AI is not available
        /// </summary>
        private static string BuildBasicContextFromHistory(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            try
            {
                var contextBuilder = new List<string>();
                
                // Simple extraction without hardcoded patterns
                var allContent = string.Join(" ", recentHistory.Select(msg => msg.Content));
                
                // Extract years (basic pattern)
                var yearMatches = System.Text.RegularExpressions.Regex.Matches(allContent, @"\b20[12]\d\b");
                var years = yearMatches.Select(m => m.Value).Distinct().Take(3).ToList();
                if (years.Any())
                {
                    contextBuilder.Add($"Time: {string.Join(", ", years)}");
                }
                
                // Extract quarters (basic pattern)
                var quarterMatches = System.Text.RegularExpressions.Regex.Matches(allContent, @"\bQ[1-4]\s*20[12]\d\b");
                var quarters = quarterMatches.Select(m => m.Value).Distinct().Take(3).ToList();
                if (quarters.Any())
                {
                    contextBuilder.Add($"Quarters: {string.Join(", ", quarters)}");
                }
                
                return string.Join(" | ", contextBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in basic context extraction: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Builds chat history for AI context extraction
        /// </summary>
        private static List<string> BuildChatHistoryForContextExtraction(IEnumerable<ChatMessageInfo> recentHistory)
        {
            var historyForAI = new List<string>();
            
            // Take last 8 messages (4 user, 4 AI) for context
            var recentHistoryList = recentHistory.TakeLast(8).ToList();
            
            foreach (var message in recentHistoryList)
            {
                var role = message.Role == AuthorRole.User ? "User" : "Assistant";
                var content = message.Content.Length > 300 ? message.Content.Substring(0, 300) + "..." : message.Content;
                historyForAI.Add($"{role}: {content}");
            }
            
            return historyForAI;
        }

        /// <summary>
        /// Cleans the extracted context from AI response
        /// </summary>
        private static string CleanExtractedContext(string extractedContext)
        {
            // Remove quotes if present
            extractedContext = extractedContext.Trim('"', '\'', '`');
            
            // Remove common AI prefixes
            var prefixesToRemove = new[] { "extracted context:", "context:", "result:", "analysis:" };
            foreach (var prefix in prefixesToRemove)
            {
                if (extractedContext.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    extractedContext = extractedContext.Substring(prefix.Length).Trim();
                    break;
                }
            }
            
            // Ensure proper line breaks
            extractedContext = extractedContext.Replace("\\n", "\n");
            
            return extractedContext;
        }






    }
}
