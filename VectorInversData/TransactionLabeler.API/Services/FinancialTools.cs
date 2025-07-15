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
            DateTime? transactionDate = null,
            string? description = null,
            string? bankAccountNumber = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            bool salaryOnly = false)
        {
            return await _transactionService.FindDuplicatePaymentsAsync(_connectionString, amount, transactionDate, description, bankAccountNumber, startDate, endDate, salaryOnly);
        }

        [Description("Finds transactions within a specific date range with optional filtering by amount, description, or transaction type. Useful for period analysis and spending pattern identification.")]
        public async Task<List<TransactionResult>> FindTransactionsInDateRange(
            DateTime startDate,
            DateTime endDate,
            decimal? minAmount = null,
            decimal? maxAmount = null,
            string? description = null,
            string? transactionType = null)
        {
            return await _transactionService.FindTransactionsInDateRangeAsync(_connectionString, startDate, endDate, minAmount, maxAmount, description, transactionType);
        }

        [Description("Identifies unusual or outlier transactions based on statistical analysis. Useful for fraud detection and identifying data entry errors.")]
        public async Task<List<TransactionResult>> FindUnusualTransactions(
            string? categoryCode = null,
            double standardDeviations = 2.0,
            int minimumTransactions = 10)
        {
            return await _transactionService.FindUnusualTransactionsAsync(_connectionString, categoryCode, standardDeviations, minimumTransactions);
        }



        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexible(DateTime? startDate, DateTime? endDate, int? year, string? customerName = null, int topN = 5)
        {
            return await _transactionService.GetTopExpenseCategoriesFlexibleAsync(_connectionString, startDate, endDate, year, customerName, topN);
        }
    }
}
