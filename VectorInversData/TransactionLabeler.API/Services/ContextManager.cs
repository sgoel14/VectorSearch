using System;
using System.Collections.Generic;
using System.Linq;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    /// <summary>
    /// Helper class for managing context summaries and persistence
    /// </summary>
    public static class ContextManager
    {
        /// <summary>
        /// Updates context summary for a session
        /// </summary>
        public static async Task UpdateContextSummaryAsync(Dictionary<string, string> contextSummaries, string sessionId, string query, string response)
        {
            try
            {
                var summary = BuildIntelligentContextSummary(query, response);
                
                if (contextSummaries.ContainsKey(sessionId))
                {
                    // Update existing summary by merging with new information
                    var existingSummary = contextSummaries[sessionId];
                    var mergedSummary = MergeContextSummaries(existingSummary, summary);
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

        /// <summary>
        /// Merges two context summaries intelligently
        /// </summary>
        private static string MergeContextSummaries(string existingSummary, string newSummary)
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
    }
}
