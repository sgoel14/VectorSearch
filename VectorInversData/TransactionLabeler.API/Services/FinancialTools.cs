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

        [Description("Finds potential duplicate bank transactions. Can be used for specific amount/date duplicates or salary-specific duplicates within date ranges. For salary duplicates, use salaryOnly=true with startDate/endDate. For specific duplicates, use amount and transactionDate. Useful for detecting double payments, duplicate invoices, or erroneous transactions.")]
        public async Task<List<TransactionResult>> FindDuplicatePayments(
            decimal? amount = null,
            object? transactionDate = null,
            string? description = null,
            string? bankAccountNumber = null,
            object? startDate = null,
            object? endDate = null,
            bool salaryOnly = false)
        {
            // Convert string dates to DateTime? if needed
            DateTime? parsedTransactionDate = null;
            DateTime? parsedStartDate = null;
            DateTime? parsedEndDate = null;
            
            if (transactionDate != null)
            {
                if (transactionDate is string transactionDateStr && DateTime.TryParse(transactionDateStr, out var parsedTransaction))
                {
                    parsedTransactionDate = parsedTransaction;
                }
                else if (transactionDate is DateTime transactionDateDt)
                {
                    parsedTransactionDate = transactionDateDt;
                }
                else if (transactionDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)transactionDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedTransactionFromJson))
                        {
                            parsedTransactionDate = parsedTransactionFromJson;
                        }
                    }
                }
            }
            
            if (startDate != null)
            {
                if (startDate is string startDateStr && DateTime.TryParse(startDateStr, out var parsedStart))
                {
                    parsedStartDate = parsedStart;
                }
                else if (startDate is DateTime startDateDt)
                {
                    parsedStartDate = startDateDt;
                }
                else if (startDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)startDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedStartFromJson))
                        {
                            parsedStartDate = parsedStartFromJson;
                        }
                    }
                }
            }
            
            if (endDate != null)
            {
                if (endDate is string endDateStr && DateTime.TryParse(endDateStr, out var parsedEnd))
                {
                    parsedEndDate = parsedEnd;
                }
                else if (endDate is DateTime endDateDt)
                {
                    parsedEndDate = endDateDt;
                }
                else if (endDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)endDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedEndFromJson))
                        {
                            parsedEndDate = parsedEndFromJson;
                        }
                    }
                }
            }
            
            return await _transactionService.FindDuplicatePaymentsAsync(_connectionString, amount, parsedTransactionDate, description, bankAccountNumber, parsedStartDate, parsedEndDate, salaryOnly);
        }

        [Description("Finds transactions within a specific date range with optional filtering by amount, description, or transaction type. Useful for period analysis and spending pattern identification.")]
        public async Task<List<TransactionResult>> FindTransactionsInDateRange(
            object startDate,
            object endDate,
            decimal? minAmount = null,
            decimal? maxAmount = null,
            string? description = null,
            string? transactionType = null)
        {
            // Convert string dates to DateTime if needed
            DateTime parsedStartDate;
            DateTime parsedEndDate;
            
            if (startDate is string startDateStr && DateTime.TryParse(startDateStr, out var parsedStart))
            {
                parsedStartDate = parsedStart;
            }
            else if (startDate is DateTime startDateDt)
            {
                parsedStartDate = startDateDt;
            }
            else if (startDate.GetType().Name == "JsonElement")
            {
                // Handle JsonElement from JSON deserialization
                var jsonElement = (System.Text.Json.JsonElement)startDate;
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var dateStr = jsonElement.GetString();
                    if (DateTime.TryParse(dateStr, out var parsedStartFromJson))
                    {
                        parsedStartDate = parsedStartFromJson;
                    }
                    else
                    {
                        throw new ArgumentException("startDate must be a valid date string or DateTime");
                    }
                }
                else
                {
                    throw new ArgumentException("startDate must be a valid date string or DateTime");
                }
            }
            else
            {
                throw new ArgumentException("startDate must be a valid date string or DateTime");
            }
            
            if (endDate is string endDateStr && DateTime.TryParse(endDateStr, out var parsedEnd))
            {
                parsedEndDate = parsedEnd;
            }
            else if (endDate is DateTime endDateDt)
            {
                parsedEndDate = endDateDt;
            }
            else if (endDate.GetType().Name == "JsonElement")
            {
                // Handle JsonElement from JSON deserialization
                var jsonElement = (System.Text.Json.JsonElement)endDate;
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var dateStr = jsonElement.GetString();
                    if (DateTime.TryParse(dateStr, out var parsedEndFromJson))
                    {
                        parsedEndDate = parsedEndFromJson;
                    }
                    else
                    {
                        throw new ArgumentException("endDate must be a valid date string or DateTime");
                    }
                }
                else
                {
                    throw new ArgumentException("endDate must be a valid date string or DateTime");
                }
            }
            else
            {
                throw new ArgumentException("endDate must be a valid date string or DateTime");
            }
            
            return await _transactionService.FindTransactionsInDateRangeAsync(_connectionString, parsedStartDate, parsedEndDate, minAmount, maxAmount, description, transactionType);
        }

        [Description("Identifies unusual or outlier transactions based on statistical analysis. Useful for fraud detection and identifying data entry errors.")]
        public async Task<List<TransactionResult>> FindUnusualTransactions(
            string? categoryCode = null,
            double standardDeviations = 2.0,
            int minimumTransactions = 10)
        {
            return await _transactionService.FindUnusualTransactionsAsync(_connectionString, categoryCode, standardDeviations, minimumTransactions);
        }



        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexible(object? startDate, object? endDate, int? year, string? customerName = null, int topN = 5)
        {
            // Convert string dates to DateTime? if needed
            DateTime? parsedStartDate = null;
            DateTime? parsedEndDate = null;
            
            if (startDate != null)
            {
                if (startDate is string startDateStr && DateTime.TryParse(startDateStr, out var parsedStart))
                {
                    parsedStartDate = parsedStart;
                }
                else if (startDate is DateTime startDateDt)
                {
                    parsedStartDate = startDateDt;
                }
                else if (startDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)startDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedStartFromJson))
                        {
                            parsedStartDate = parsedStartFromJson;
                        }
                    }
                }
            }
            
            if (endDate != null)
            {
                if (endDate is string endDateStr && DateTime.TryParse(endDateStr, out var parsedEnd))
                {
                    parsedEndDate = parsedEnd;
                }
                else if (endDate is DateTime endDateDt)
                {
                    parsedEndDate = endDateDt;
                }
                else if (endDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)endDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedEndFromJson))
                        {
                            parsedEndDate = parsedEndFromJson;
                        }
                    }
                }
            }
            
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
            DateTime? parsedStartDate = null;
            DateTime? parsedEndDate = null;
            
            if (startDate != null)
            {
                if (startDate is string startDateStr && DateTime.TryParse(startDateStr, out var parsedStart))
                {
                    parsedStartDate = parsedStart;
                }
                else if (startDate is DateTime startDateDt)
                {
                    parsedStartDate = startDateDt;
                }
                else if (startDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)startDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedStartFromJson))
                        {
                            parsedStartDate = parsedStartFromJson;
                        }
                    }
                }
            }
            
            if (endDate != null)
            {
                if (endDate is string endDateStr && DateTime.TryParse(endDateStr, out var parsedEnd))
                {
                    parsedEndDate = parsedEnd;
                }
                else if (endDate is DateTime endDateDt)
                {
                    parsedEndDate = endDateDt;
                }
                else if (endDate.GetType().Name == "JsonElement")
                {
                    // Handle JsonElement from JSON deserialization
                    var jsonElement = (System.Text.Json.JsonElement)endDate;
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dateStr = jsonElement.GetString();
                        if (DateTime.TryParse(dateStr, out var parsedEndFromJson))
                        {
                            parsedEndDate = parsedEndFromJson;
                        }
                    }
                }
            }
            
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
    }
}
