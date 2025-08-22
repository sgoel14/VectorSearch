using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    /// <summary>
    /// Helper class for managing context summaries and persistence using AI-powered analysis
    /// </summary>
    public static class ContextManager
    {
        private static Kernel? _kernel;

        /// <summary>
        /// Initialize the kernel for AI-powered context management
        /// </summary>
        public static void InitializeKernel(Kernel kernel)
        {
            _kernel = kernel;
        }

        /// <summary>
        /// Updates context summary for a session using AI-powered analysis
        /// </summary>
        public static async Task UpdateContextSummaryAsync(Dictionary<string, string> contextSummaries, string sessionId, string query, string response)
        {
            try
            {
                var summary = await BuildIntelligentContextSummaryAsync(query, response);
                
                if (contextSummaries.ContainsKey(sessionId))
                {
                    // Update existing summary by merging with new information using AI
                    var existingSummary = contextSummaries[sessionId];
                    var mergedSummary = await MergeContextSummariesAsync(existingSummary, summary);
                    contextSummaries[sessionId] = mergedSummary;
                }
                else
                {
                    // Create new summary
                    contextSummaries[sessionId] = summary;
                }
                
                Console.WriteLine($"✅ Updated context summary for session {sessionId}: {contextSummaries[sessionId]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating context summary: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds intelligent context summary from query and response using AI
        /// </summary>
        public static async Task<string> BuildIntelligentContextSummaryAsync(string query, string response)
        {
            try
            {
                if (_kernel == null)
                {
                    Console.WriteLine("⚠️ Kernel not initialized, falling back to basic context summary");
                    return BuildBasicContextSummary(query, response);
                }

                Console.WriteLine($"🤖 AI-powered context summary generation");

                // Create the system instruction for context summary
                var systemInstruction = @"You are a context summary expert. Analyze the provided query and response to create a concise context summary.

                    IMPORTANT RULES:
                    - Extract ONLY the following information in this exact format:
                      * Customers: Business names, company names mentioned
                      * Categories: Expense categories, transaction types discussed
                      * Time Periods: Years, quarters, months, or specific dates mentioned
                      * Query Type: What the user was asking about (Transaction Analysis, Category Analysis, Expense Analysis, etc.)

                    - Format your response EXACTLY like this example:
                      Customers: Company Name 1, Company Name 2
                      Categories: Category 1, Category 2
                      Time Periods: 2025, Q2 2025
                      Query Type: Transaction Analysis

                    - Rules:
                      * Extract actual business names, not generic words
                      * Extract specific categories, not general terms
                      * Extract time references (years, quarters, months)
                      * Identify the main purpose of the query
                      * Keep it concise and relevant

                    - If no relevant information is found for a category, omit that line entirely
                    - Only output the summary, nothing else";

                // Create execution settings for summary generation
                var summarySettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 250, // Short response for summary
                    Temperature = 0.1f, // Low temperature for consistent summary
                    TopP = 0.9f,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.None() // No function calling for summary
                };

                // Build the prompt for summary generation
                var summaryPrompt = $"{systemInstruction}\n\nUser Query: {query}\n\nAI Response: {response}\n\nContext Summary:";

                // Use the AI model to generate summary
                var summaryResult = await _kernel.InvokePromptAsync(summaryPrompt, new KernelArguments(summarySettings));
                
                if (summaryResult != null)
                {
                    var generatedSummary = summaryResult.ToString().Trim();
                    
                    // Clean up the response
                    generatedSummary = CleanExtractedContext(generatedSummary);
                    
                    if (!string.IsNullOrEmpty(generatedSummary))
                    {
                        Console.WriteLine($"✅ AI Context Summary Generated: {generatedSummary}");
                        return generatedSummary;
                    }
                }

                Console.WriteLine($"❌ AI context summary generation failed, falling back to basic summary");
                return BuildBasicContextSummary(query, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error in AI context summary generation: {ex.Message}, falling back to basic summary");
                return BuildBasicContextSummary(query, response);
            }
        }

        /// <summary>
        /// Merges two context summaries intelligently using AI
        /// </summary>
        private static async Task<string> MergeContextSummariesAsync(string existingSummary, string newSummary)
        {
            try
            {
                if (_kernel == null)
                {
                    Console.WriteLine("⚠️ Kernel not initialized, falling back to basic summary merging");
                    return MergeContextSummariesBasic(existingSummary, newSummary);
                }

                Console.WriteLine($"🤖 AI-powered context summary merging");

                // Create the system instruction for summary merging
                var systemInstruction = @"You are a context summary merging expert. Merge two context summaries into one comprehensive summary.

                    IMPORTANT RULES:
                    - Combine information from both summaries intelligently
                    - Remove duplicates while preserving all unique information
                    - Maintain the exact format:
                      * Customers: Company Name 1, Company Name 2, Company Name 3
                      * Categories: Category 1, Category 2, Category 3, Category 4
                      * Time Periods: 2025, Q2 2025, January 2025
                      * Query Type: Transaction Analysis

                    - Rules for merging:
                      * Customers: Combine all unique customer names, limit to 3 most relevant
                      * Categories: Combine all unique categories, limit to 5 most relevant
                      * Time Periods: Combine all unique time references, limit to 3 most relevant
                      * Query Type: Keep the most specific or recent query type

                    - If no relevant information is found for a category, omit that line entirely
                    - Only output the merged summary, nothing else";

                // Create execution settings for summary merging
                var mergeSettings = new AzureOpenAIPromptExecutionSettings
                {
                    MaxTokens = 300, // Short response for merging
                    Temperature = 0.1f, // Low temperature for consistent merging
                    TopP = 0.9f,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.None() // No function calling for merging
                };

                // Build the prompt for summary merging
                var mergePrompt = $"{systemInstruction}\n\nExisting Summary:\n{existingSummary}\n\nNew Summary:\n{newSummary}\n\nMerged Summary:";

                // Use the AI model to merge summaries
                var mergeResult = await _kernel.InvokePromptAsync(mergePrompt, new KernelArguments(mergeSettings));
                
                if (mergeResult != null)
                {
                    var mergedSummary = mergeResult.ToString().Trim();
                    
                    // Clean up the response
                    mergedSummary = CleanExtractedContext(mergedSummary);
                    
                    if (!string.IsNullOrEmpty(mergedSummary))
                    {
                        Console.WriteLine($"✅ AI Context Summary Merged: {mergedSummary}");
                        return mergedSummary;
                    }
                }

                Console.WriteLine($"❌ AI context summary merging failed, falling back to basic merging");
                return MergeContextSummariesBasic(existingSummary, newSummary);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error in AI context summary merging: {ex.Message}, falling back to basic merging");
                return MergeContextSummariesBasic(existingSummary, newSummary);
            }
        }

        /// <summary>
        /// Fallback method for basic summary merging when AI is not available
        /// </summary>
        private static string MergeContextSummariesBasic(string existingSummary, string newSummary)
        {
            try
            {
                var mergedBuilder = new List<string>();
                
                // Parse existing summary
                var existingParts = existingSummary.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
                var existingCustomers = ExtractCustomersFromSummary(existingParts);
                var existingCategories = ExtractCategoriesFromSummary(existingParts);
                var existingTimePeriods = ExtractTimePeriodsFromSummary(existingParts);
                
                // Parse new summary
                var newParts = newSummary.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
                var newCustomers = ExtractCustomersFromSummary(newParts);
                var newCategories = ExtractCategoriesFromSummary(newParts);
                var newTimePeriods = ExtractTimePeriodsFromSummary(newParts);
                
                // Merge customers (keep existing, add new)
                var mergedCustomers = existingCustomers.Concat(newCustomers).Distinct().Take(3).ToList();
                if (mergedCustomers.Any())
                {
                    mergedBuilder.Add($"Customers: {string.Join(", ", mergedCustomers)}");
                }
                
                // Merge categories (keep existing, add new)
                var mergedCategories = existingCategories.Concat(newCategories).Distinct().Take(5).ToList();
                if (mergedCategories.Any())
                {
                    mergedBuilder.Add($"Categories: {string.Join(", ", mergedCategories)}");
                }
                
                // Merge time periods (keep existing, add new)
                var mergedTimePeriods = existingTimePeriods.Concat(newTimePeriods).Distinct().Take(3).ToList();
                if (mergedTimePeriods.Any())
                {
                    mergedBuilder.Add($"Time Periods: {string.Join(", ", mergedTimePeriods)}");
                }
                
                // Add query type from new summary if present
                var queryTypePart = newParts.FirstOrDefault(p => p.StartsWith("Query Type:"));
                if (!string.IsNullOrEmpty(queryTypePart))
                {
                    mergedBuilder.Add(queryTypePart);
                }
                
                return string.Join(" | ", mergedBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error merging context summaries: {ex.Message}");
                return newSummary; // Fallback to new summary if merging fails
            }
        }

        /// <summary>
        /// Cleans the extracted context from AI response
        /// </summary>
        private static string CleanExtractedContext(string extractedContext)
        {
            // Remove quotes if present
            extractedContext = extractedContext.Trim('"', '\'', '`');
            
            // Remove common AI prefixes
            var prefixesToRemove = new[] { "merged summary:", "summary:", "result:", "analysis:", "context:" };
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

        /// <summary>
        /// Extracts customer information from summary parts
        /// </summary>
        private static List<string> ExtractCustomersFromSummary(string[] summaryParts)
        {
            var customers = new List<string>();
            var customerPart = summaryParts.FirstOrDefault(p => p.StartsWith("Customers:"));
            if (!string.IsNullOrEmpty(customerPart))
            {
                var customerList = customerPart.Replace("Customers:", "").Trim();
                customers.AddRange(customerList.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()));
            }
            return customers;
        }

        /// <summary>
        /// Extracts category information from summary parts
        /// </summary>
        private static List<string> ExtractCategoriesFromSummary(string[] summaryParts)
        {
            var categories = new List<string>();
            var categoryPart = summaryParts.FirstOrDefault(p => p.StartsWith("Categories:"));
            if (!string.IsNullOrEmpty(categoryPart))
            {
                var categoryList = categoryPart.Replace("Categories:", "").Trim();
                categories.AddRange(categoryList.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()));
            }
            return categories;
        }

        /// <summary>
        /// Extracts time period information from summary parts
        /// </summary>
        private static List<string> ExtractTimePeriodsFromSummary(string[] summaryParts)
        {
            var timePeriods = new List<string>();
            var timePart = summaryParts.FirstOrDefault(p => p.StartsWith("Time Periods:"));
            if (!string.IsNullOrEmpty(timePart))
            {
                var timeList = timePart.Replace("Time Periods:", "").Trim();
                timePeriods.AddRange(timeList.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
            }
            return timePeriods;
        }

        /// <summary>
        /// Gets a condensed context summary for display
        /// </summary>
        public static string GetCondensedContextSummary(string summary, int maxLength = 300)
        {
            if (string.IsNullOrEmpty(summary))
                return "";
                
            if (summary.Length <= maxLength)
                return summary;
                
            return summary.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Clears context summary for a session
        /// </summary>
        public static void ClearContextSummary(Dictionary<string, string> contextSummaries, string sessionId)
        {
            if (contextSummaries.ContainsKey(sessionId))
            {
                contextSummaries.Remove(sessionId);
                Console.WriteLine($"🗑️ Cleared context summary for session {sessionId}");
            }
        }

        /// <summary>
        /// Gets all context summaries for debugging
        /// </summary>
        public static string GetAllContextSummaries(Dictionary<string, string> contextSummaries)
        {
            if (!contextSummaries.Any())
                return "No context summaries available";
                
            var summaries = contextSummaries.Select(kvp => $"Session: {kvp.Key} | Summary: {kvp.Value}");
            return string.Join("\n", summaries);
        }

        /// <summary>
        /// Fallback method for basic context summary when AI is not available
        /// </summary>
        private static string BuildBasicContextSummary(string query, string response)
        {
            try
            {
                var summaryBuilder = new List<string>();
                
                // Simple extraction without hardcoded patterns
                var allContent = $"{query} {response}";
                
                // Extract years (basic pattern)
                var yearMatches = System.Text.RegularExpressions.Regex.Matches(allContent, @"\b20[12]\d\b");
                var years = yearMatches.Select(m => m.Value).Distinct().Take(2).ToList();
                if (years.Any())
                {
                    summaryBuilder.Add($"Time Periods: {string.Join(", ", years)}");
                }
                
                // Extract quarters (basic pattern)
                var quarterMatches = System.Text.RegularExpressions.Regex.Matches(allContent, @"\bQ[1-4]\s*20[12]\d\b");
                var quarters = quarterMatches.Select(m => m.Value).Distinct().Take(2).ToList();
                if (quarters.Any())
                {
                    summaryBuilder.Add($"Quarters: {string.Join(", ", quarters)}");
                }
                
                // Basic query type detection
                if (allContent.ToLower().Contains("transaction"))
                {
                    summaryBuilder.Add("Query Type: Transaction Analysis");
                }
                else if (allContent.ToLower().Contains("category"))
                {
                    summaryBuilder.Add("Query Type: Category Analysis");
                }
                else if (allContent.ToLower().Contains("expense"))
                {
                    summaryBuilder.Add("Query Type: Expense Analysis");
                }
                
                return string.Join(" | ", summaryBuilder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in basic context summary: {ex.Message}");
                return "";
            }
        }
    }
}
