using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
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





        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexible(object? startDate, object? endDate, int? year, string? customerName = null, int topN = 5)
        {
            // Convert string dates to DateTime? if needed
            DateTime? parsedStartDate = ParseDateParameter(startDate);
            DateTime? parsedEndDate = ParseDateParameter(endDate);
            
            return await _transactionService.GetTopExpenseCategoriesFlexibleAsync(_connectionString, parsedStartDate, parsedEndDate, year, customerName, topN);
        }

        [Description("Finds and returns transaction details for ANY specific category using semantic understanding. Works with natural language queries about transactions, transaction lists, or transaction details. Handles queries like 'list transactions for staff drink and food', 'get transactions for marketing', 'find transactions for travel', 'show me transactions for office supplies', 'top 10 transactions for car repair', 'transactions for restaurant expenses', 'display transactions for utilities', 'retrieve transactions for advertising', etc. First searches for relevant categories using semantic similarity, then returns the top transactions for those categories. Extract the category from the user's query. If no number specified, use 10. Only include year parameter if explicitly mentioned in the query.")]
        public async Task<List<TransactionResult>> GetTopTransactionsForCategory(
            string categoryQuery,
            object? startDate = null,
            object? endDate = null,
            int? year = null,
            int topN = 10,
            string? customerName = null,
            int topCategories = 3)
        {
            // Ensure topCategories is at least 1 to avoid empty results
            if (topCategories <= 0)
            {
                topCategories = 3; // Default to 3 if 0 or negative
            }
            
            // Convert string dates to DateTime? if needed
            DateTime? parsedStartDate = ParseDateParameter(startDate);
            DateTime? parsedEndDate = ParseDateParameter(endDate);
            
            return await _transactionService.GetTopTransactionsForCategoryQueryAsync(_connectionString, categoryQuery, parsedStartDate, parsedEndDate, year, topN, customerName, topCategories);
        }

        [Description("Searches for and explores available categories using semantic understanding. Use this tool when the user wants to understand what categories are available, explore category options, or get information about available transaction categories. Examples: 'what categories are available', 'list all categories', 'show me categories', 'explore categories', 'travel categories', 'food categories', 'office expense categories', 'travel categories for Company ABC', 'search all categories for Nova Creations'. For 'all categories' queries, use a higher topCategories value like 50.")]
        public async Task<List<CategorySearchResult>> SearchCategories(string categoryQuery, int topCategories = 5, string? customerName = null)
        {
            // For "all categories" queries, increase the limit to get more results
            if (string.IsNullOrWhiteSpace(categoryQuery) || categoryQuery.ToLower().Contains("all"))
            {
                topCategories = Math.Max(topCategories, 50); // Get more categories for "all" queries
            }
            
            return await _transactionService.SearchCategoriesByVectorAsync(_connectionString, categoryQuery, topCategories, customerName);
        }

        [Description("Calculates total spending for a specific category within a date range. Use for queries like 'How much did we spend on marketing last month?', 'What was our travel expenses in Q1 2024?', 'Total spending on office supplies this year', 'Marketing costs for Nova Creations in January', 'Travel expenses for Company ABC in 2023'. Extracts the category from the user's query and calculates total spending with breakdown by RGS codes. Only includes expense transactions (AfBij = 'Af').")]
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
