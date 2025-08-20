using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.SemanticKernel;
using TransactionLabeler.API.Models;

namespace TransactionLabeler.API.Services
{
    public class FinancialTools
    {
        private readonly ITransactionService _transactionService;
        private readonly string _connectionString;

        public FinancialTools(ITransactionService transactionService, string connectionString)
        {
            _transactionService = transactionService;
            _connectionString = connectionString;
        }

        /// <summary>
        /// Helper method to convert various date input types to DateTime?
        /// </summary>
        private static DateTime? ParseDateParameter(object? dateParameter)
        {
            if (dateParameter == null)
                return null;

            if (dateParameter is string dateStr && DateTime.TryParse(dateStr, out var parsedDate))
            {
                return parsedDate;
            }
            else if (dateParameter is DateTime dateTime)
            {
                return dateTime;
            }
            else if (dateParameter.GetType().Name == "JsonElement")
            {
                var jsonElement = (System.Text.Json.JsonElement)dateParameter;
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var jsonDateStr = jsonElement.GetString();
                    if (DateTime.TryParse(jsonDateStr, out var parsedDateFromJson))
                    {
                        return parsedDateFromJson;
                    }
                }
            }

            return null;
        }

        [KernelFunction, Description("Returns the top N expense categories for a given date range, month, quarter, or year, optionally filtered by customer name. Use for queries like 'What are my top 3 expenses for January 2024?', 'Show my biggest spending categories in Q2 2023', 'Top 5 expense categories in 2023', 'Top expenses for Company ABC'. Only expense transactions (AfBij = 'Af') are included. If the user does not specify a date range or year, use the current year. If the user does not specify a number, use 5.")]
        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexible(object? startDate, object? endDate, int? year, string? customerName = null, int topN = 5)
        {
            // Convert string dates to DateTime? if needed
            DateTime? parsedStartDate = ParseDateParameter(startDate);
            DateTime? parsedEndDate = ParseDateParameter(endDate);
            
            return await _transactionService.GetTopExpenseCategoriesFlexibleAsync(_connectionString, parsedStartDate, parsedEndDate, year, customerName, topN);
        }

        [KernelFunction, Description("Finds and returns transaction details for a SPECIFIC category using semantic understanding. Use this when the user wants to see actual transaction records for a particular category. Works with queries like 'list transactions for staff drink and food', 'get transactions for marketing', 'find transactions for travel', 'show me transactions for office supplies', 'top 10 transactions for car repair', 'transactions for restaurant expenses', 'display transactions for utilities', 'retrieve transactions for advertising', etc. First searches for relevant categories using semantic similarity, then returns the top transactions for those categories. Extract the category from the user's query. If no number specified, use 10. Only include year parameter if explicitly mentioned in the query.")]
        public async Task<List<TransactionResult>> GetTopTransactionsForCategory(
            string categoryQuery,
            object? startDate = null,
            object? endDate = null,
            int? year = null,
            int topN = 10,
            string? customerName = null,
            int topCategories = 3)
        {
            // DEBUG: Function is actually being called!
            Console.WriteLine($"🔥 REAL FUNCTION CALLED: GetTopTransactionsForCategory with categoryQuery='{categoryQuery}', year={year}, topN={topN}, customerName='{customerName}'");
            
            // Ensure topCategories is at least 1 to avoid empty results
            if (topCategories <= 0)
            {
                topCategories = 3; // Default to 3 if 0 or negative
            }
            
            // Convert string dates to DateTime? if needed
            DateTime? parsedStartDate = ParseDateParameter(startDate);
            DateTime? parsedEndDate = ParseDateParameter(endDate);
            
            var results = await _transactionService.GetTopTransactionsForCategoryQueryAsync(_connectionString, categoryQuery, parsedStartDate, parsedEndDate, year, topN, customerName, topCategories);
            
            // DEBUG: Log what we're returning
            Console.WriteLine($"🔥 GetTopTransactionsForCategory returning {results.Count} transactions");
            foreach (var result in results.Take(3)) // Log first 3 for debugging
            {
                Console.WriteLine($"🔥 Transaction: Amount={result.Amount}, Description='{result.Description}', RgsCode='{result.RgsCode}', RgsDescription='{result.RgsDescription}'");
            }
            
            return results;
        }

        [KernelFunction, Description("PRIORITY 1 - ALL CATEGORIES FUNCTION: Use this function when the user asks for 'all categories', 'list all categories', 'show me all categories', 'what categories are available', 'categories for Nova Creations', 'all categories for Company ABC'. This function returns EVERY single category available in the system without any filtering. ALWAYS use this for 'all categories' requests. Set topCategories to 1000 to get everything. DO NOT use SearchCategories for 'all categories' requests.")]
        public async Task<List<CategorySearchResult>> GetAllCategoriesForCustomer(int topCategories = 1000, string? customerName = null)
        {
            Console.WriteLine($"🔥 GetAllCategoriesForCustomer called with topCategories={topCategories}, customerName='{customerName}'");
            var result = await _transactionService.SearchCategoriesByVectorAsync(_connectionString, null, topCategories, customerName);
            Console.WriteLine($"🔥 GetAllCategoriesForCustomer returning {result.Count} categories");
            return result;
        }

        [KernelFunction, Description("PRIORITY 2 - SPECIFIC CATEGORY SEARCH: WARNING: DO NOT use this function for 'all categories' requests! This function is ONLY for searching specific categories by topic. Use this for queries like 'travel categories', 'food categories', 'office expense categories', 'marketing categories', 'utility categories'. This function performs semantic search to find categories related to a specific topic. For 'all categories' or 'list all categories' requests, ALWAYS use GetAllCategoriesForCustomer instead.")]
        public async Task<List<CategorySearchResult>> SearchCategories(string categoryQuery, int topCategories = 5, string? customerName = null)
        {
            Console.WriteLine($"🔍 SearchCategories called with categoryQuery='{categoryQuery}', topCategories={topCategories}, customerName='{customerName}'");
            var result = await _transactionService.SearchCategoriesByVectorAsync(_connectionString, categoryQuery, topCategories, customerName);
            Console.WriteLine($"🔍 SearchCategories returning {result.Count} categories");
            return result;
        }

        [KernelFunction, Description("Calculates total spending for a specific category within a date range. Use for queries like 'How much did we spend on marketing last month?', 'What was our travel expenses in Q1 2024?', 'Total spending on office supplies this year', 'Marketing costs for Nova Creations in January', 'Travel expenses for Company ABC in 2023'. Extracts the category from the user's query and calculates total spending with breakdown by RGS codes. Only includes expense transactions (AfBij = 'Af').")]
        public async Task<CategorySpendingResult> GetCategorySpending(
            string categoryQuery,
            object? startDate = null,
            object? endDate = null,
            int? year = null,
            string? customerName = null)
        {
            // Convert string dates to DateTime? if needed
            DateTime? parsedStartDate = ParseDateParameter(startDate);
            DateTime? parsedEndDate = ParseDateParameter(endDate);
            
            return await _transactionService.GetCategorySpendingAsync(_connectionString, categoryQuery, parsedStartDate, parsedEndDate, year, customerName);
        }
    }
}
