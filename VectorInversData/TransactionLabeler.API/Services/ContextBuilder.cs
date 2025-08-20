using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    /// <summary>
    /// Helper class for building and managing context from chat history
    /// </summary>
    public static class ContextBuilder
    {
        /// <summary>
        /// Builds condensed context from recent chat history by extracting key parameters
        /// </summary>
        public static string BuildCondensedContextFromHistory(IEnumerable<ChatMessageInfo> recentHistory, string currentQuery)
        {
            try
            {
                var contextBuilder = new List<string>();
                
                // Extract customer names from BOTH user queries AND assistant responses
                var customerNames = new List<string>();
                
                // From assistant responses FIRST (extract actual customer names returned) - preserve previous context
                var assistantCustomerNames = recentHistory
                    .Where(msg => msg.Role == AuthorRole.Assistant)
                    .SelectMany(msg => ContextExtractor.ExtractCustomerNamesFromResponse(msg.Content))
                    .Distinct()
                    .ToList();
                customerNames.AddRange(assistantCustomerNames);
                
                // From user queries SECOND (add new customer names if any)
                var userCustomerNames = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ContextExtractor.ExtractCustomerNames(msg.Content))
                    .Distinct()
                    .ToList();
                customerNames.AddRange(userCustomerNames);
                
                // Filter out common words and question words
                var filteredCustomerNames = customerNames
                    .Where(name => !name.IsCommonWord() && !name.IsQuestionWord() && name.Length > 2)
                    .Distinct()
                    .Take(3) // Increased to preserve more context
                    .ToList();
                
                if (filteredCustomerNames.Any())
                {
                    contextBuilder.Add($"Customer: {string.Join(", ", filteredCustomerNames)}");
                }

                // Extract categories from BOTH user queries AND assistant responses
                var categories = new List<string>();
                
                // From user queries - look for the most recent category, but preserve previous ones
                var userCategories = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ContextExtractor.ExtractCategories(msg.Content))
                    .Distinct()
                    .ToList();
                
                // From assistant responses (extract actual categories returned)
                var assistantCategories = recentHistory
                    .Where(msg => msg.Role == AuthorRole.Assistant)
                    .SelectMany(msg => ContextExtractor.ExtractCategoriesFromResponse(msg.Content))
                    .Distinct()
                    .ToList();
                
                // Merge categories: keep previous ones and add new ones
                categories.AddRange(assistantCategories); // Previous context from AI responses
                categories.AddRange(userCategories);      // New context from user queries
                
                // Filter out common words and question words
                var filteredCategories = categories
                    .Where(cat => !cat.IsCommonWord() && !cat.IsQuestionWord() && cat.Length > 2)
                    .ToList();
                
                if (filteredCategories.Any())
                {
                    contextBuilder.Add($"Categories: {string.Join(", ", filteredCategories)}");
                }

                // Extract time periods from BOTH user queries AND assistant responses
                var timePeriods = new List<string>();
                
                // From assistant responses FIRST (extract actual time periods returned) - preserve previous context
                var assistantTimePeriods = recentHistory
                    .Where(msg => msg.Role == AuthorRole.Assistant)
                    .SelectMany(msg => ContextExtractor.ExtractTimePeriodsFromResponse(msg.Content))
                    .Distinct()
                    .ToList();
                timePeriods.AddRange(assistantTimePeriods);
                
                // From user queries SECOND (add new time periods if any)
                var userTimePeriods = recentHistory
                    .Where(msg => msg.Role == AuthorRole.User)
                    .SelectMany(msg => ContextExtractor.ExtractTimePeriods(msg.Content))
                    .Distinct()
                    .ToList();
                timePeriods.AddRange(userTimePeriods);
                
                if (timePeriods.Distinct().Any())
                {
                    contextBuilder.Add($"Time: {string.Join(", ", timePeriods.Distinct().Take(3))}"); // Increased to preserve more context
                }

                return string.Join(" | ", contextBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building condensed context: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Builds chat history for the kernel with context management
        /// </summary>
        public static List<ChatMessageContent> BuildChatHistoryForKernel(string sessionId, string currentQuery)
        {
            var chatHistory = new List<ChatMessageContent>();
            
            // Add the current query as a user message
            chatHistory.Add(new ChatMessageContent(AuthorRole.User, currentQuery));
            
            // Add condensed context from recent history if available
            // This would need to be implemented based on your chat history storage
            // For now, we'll return the basic structure
            
            return chatHistory;
        }

        /// <summary>
        /// Builds intelligent context summary from query and response
        /// </summary>
        public static string BuildIntelligentContextSummary(string query, string response)
        {
            try
            {
                var summaryBuilder = new List<string>();
                
                // Extract key information from the query
                var queryCustomers = ContextExtractor.ExtractCustomerNames(query);
                var queryCategories = ContextExtractor.ExtractCategories(query);
                var queryTimePeriods = ContextExtractor.ExtractTimePeriods(query);
                
                // Extract key information from the response
                var responseCustomers = ContextExtractor.ExtractCustomerNamesFromResponse(response);
                var responseCategories = ContextExtractor.ExtractCategoriesFromResponse(response);
                var responseTimePeriods = ContextExtractor.ExtractTimePeriodsFromResponse(response);
                
                // Combine and prioritize information
                var allCustomers = queryCustomers.Concat(responseCustomers).Distinct().Take(2).ToList();
                var allCategories = queryCategories.Concat(responseCategories).Distinct().Take(3).ToList();
                var allTimePeriods = queryTimePeriods.Concat(responseTimePeriods).Distinct().Take(2).ToList();
                
                if (allCustomers.Any())
                {
                    summaryBuilder.Add($"Customers: {string.Join(", ", allCustomers)}");
                }
                
                if (allCategories.Any())
                {
                    summaryBuilder.Add($"Categories: {string.Join(", ", allCategories)}");
                }
                
                if (allTimePeriods.Any())
                {
                    summaryBuilder.Add($"Time Periods: {string.Join(", ", allTimePeriods)}");
                }
                
                // Add query type if identifiable
                if (query.ToLower().Contains("transaction") || response.ToLower().Contains("transaction"))
                {
                    summaryBuilder.Add("Query Type: Transaction Analysis");
                }
                else if (query.ToLower().Contains("category") || response.ToLower().Contains("category"))
                {
                    summaryBuilder.Add("Query Type: Category Analysis");
                }
                else if (query.ToLower().Contains("expense") || response.ToLower().Contains("expense"))
                {
                    summaryBuilder.Add("Query Type: Expense Analysis");
                }
                
                return string.Join(" | ", summaryBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building intelligent context summary: {ex.Message}");
                return "";
            }
        }
    }

    /// <summary>
    /// Extension methods for common word and question word checking
    /// </summary>
    public static class ContextExtractorExtensions
    {
        /// <summary>
        /// Checks if a word is a common word that should be filtered out
        /// </summary>
        public static bool IsCommonWord(this string word)
        {
            var commonWords = new[]
            {
                "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
                "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
                "will", "would", "could", "should", "may", "might", "can", "must", "shall",
                "this", "that", "these", "those", "i", "you", "he", "she", "it", "we", "they",
                "me", "him", "her", "us", "them", "my", "your", "his", "her", "its", "our", "their",
                "mine", "yours", "hers", "ours", "theirs", "myself", "yourself", "himself", "herself",
                "itself", "ourselves", "yourselves", "themselves",
                "what", "when", "where", "who", "whom", "which", "whose", "why", "how",
                "all", "any", "both", "each", "few", "more", "most", "other", "some", "such",
                "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very"
            };
            
            return commonWords.Contains(word.ToLower());
        }

        /// <summary>
        /// Checks if a word is a question word that should be filtered out
        /// </summary>
        public static bool IsQuestionWord(this string word)
        {
            var questionWords = new[]
            {
                "what", "when", "where", "who", "whom", "which", "whose", "why", "how",
                "can", "could", "would", "should", "will", "may", "might", "must", "shall"
            };
            
            return questionWords.Contains(word.ToLower());
        }
    }
}
