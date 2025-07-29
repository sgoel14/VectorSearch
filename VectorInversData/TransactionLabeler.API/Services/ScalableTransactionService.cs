using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using TransactionLabeler.API.Data;
using TransactionLabeler.API.Models;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TransactionLabeler.API.Services
{
    public interface IScalableTransactionService
    {
        Task UpdateEmbeddingsIncrementalAsync(string connectionString, DateTime? sinceDate = null);
        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesScalableAsync(string connectionString, DateTime? startDate, DateTime? endDate, int? year, string? customerName = null, int topN = 5);
        Task<List<string>> FindSimilarCustomerNamesScalableAsync(string connectionString, string searchCustomerName, DateTime startDate, DateTime endDate);
    }

    public class ScalableTransactionService : IScalableTransactionService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmbeddingService _embeddingService;
        private readonly ILogger<ScalableTransactionService> _logger;
        private readonly SemaphoreSlim _embeddingSemaphore;

        public ScalableTransactionService(ApplicationDbContext context, IEmbeddingService embeddingService, ILogger<ScalableTransactionService> logger)
        {
            _context = context;
            _embeddingService = embeddingService;
            _logger = logger;
            _embeddingSemaphore = new SemaphoreSlim(20, 20); // Increased parallelism
        }

        /// <summary>
        /// Incremental embedding update - only processes new/modified records
        /// </summary>
        public async Task UpdateEmbeddingsIncrementalAsync(string connectionString, DateTime? sinceDate = null)
        {
            const int pageSize = 1000; // Larger batches for better throughput
            int page = 0;
            
            _logger.LogInformation("Starting incremental embedding update");

            while (true)
            {
                var query = _context.InversBankTransactions
                    .Where(x => x.ContentEmbedding == null || x.AmountEmbedding == null || 
                               x.DateEmbedding == null || x.CategoryEmbedding == null || 
                               x.CombinedEmbedding == null);

                // Only process records modified since the given date
                if (sinceDate.HasValue)
                {
                    query = query.Where(x => x.TransactionDate >= sinceDate.Value);
                }

                var batch = await query
                    .OrderBy(x => x.Id)
                    .Skip(page * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                if (batch.Count == 0)
                    break;

                _logger.LogInformation($"Processing batch {page + 1} with {batch.Count} records");

                // Process batch with higher parallelism
                var tasks = batch.Select(async transaction =>
                {
                    await _embeddingSemaphore.WaitAsync();
                    try
                    {
                        var embeddings = await GenerateEmbeddingsWithRetryAsync(transaction);
                        await VectorSqlHelper.InsertMultipleEmbeddingsAsync(connectionString, transaction.Id, embeddings);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing transaction {transaction.Id}");
                    }
                    finally
                    {
                        _embeddingSemaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
                page++;
            }

            _logger.LogInformation("Completed incremental embedding update");
        }

        /// <summary>
        /// Generate embeddings with retry logic for reliability
        /// </summary>
        private async Task<Dictionary<string, float[]>> GenerateEmbeddingsWithRetryAsync(InversBankTransaction transaction)
        {
            const int maxRetries = 3;
            var retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    return await GenerateSpecializedEmbeddingsAsync(transaction);
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogWarning(ex, $"Embedding generation failed for transaction {transaction.Id}, retry {retryCount}");
                    
                    if (retryCount >= maxRetries)
                        throw;

                    await Task.Delay(1000 * retryCount); // Exponential backoff
                }
            }

            throw new Exception($"Failed to generate embeddings after {maxRetries} retries");
        }

        /// <summary>
        /// Optimized top expense categories with caching and indexing
        /// </summary>
        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesScalableAsync(string connectionString, DateTime? startDate, DateTime? endDate, int? year, string? customerName = null, int topN = 5)
        {
            DateTime rangeStart, rangeEnd;
            if (startDate.HasValue && endDate.HasValue)
            {
                rangeStart = startDate.Value;
                rangeEnd = endDate.Value;
            }
            else if (year.HasValue)
            {
                rangeStart = new DateTime(year.Value, 1, 1);
                rangeEnd = new DateTime(year.Value, 12, 31);
            }
            else
            {
                var now = DateTime.Now;
                rangeStart = new DateTime(now.Year, 1, 1);
                rangeEnd = new DateTime(now.Year, 12, 31);
            }

            // Handle customer name fuzzy matching
            if (!string.IsNullOrWhiteSpace(customerName))
            {
                var similarCustomers = await FindSimilarCustomerNamesScalableAsync(connectionString, customerName, rangeStart, rangeEnd);
                
                if (!similarCustomers.Any(c => string.Equals(c, customerName, StringComparison.OrdinalIgnoreCase)) && similarCustomers.Any())
                {
                    _logger.LogInformation($"Customer '{customerName}' not found. Similar customers: {string.Join(", ", similarCustomers)}");
                    return new List<CategoryExpenseResult> 
                    { 
                        new CategoryExpenseResult 
                        { 
                            RgsDescription = "CUSTOMER_CLARIFICATION_NEEDED",
                            RgsCode = string.Join("|", similarCustomers.Take(5)),
                            TotalExpense = similarCustomers.Count
                        } 
                    };
                }
            }

            // Optimized query with proper indexing hints
            string sql = $@"
                SELECT TOP (@TopN)
                    r.rgsDescription AS RgsDescription,
                    r.rgsCode AS RgsCode,
                    SUM(ABS(t.Amount)) AS TotalExpense
                FROM dbo.inversbanktransaction t WITH (INDEX = IX_TransactionDate_CustomerName)
                LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                WHERE t.af_bij = 'Af'
                    AND t.TransactionDate >= @StartDate
                    AND t.TransactionDate < @EndDate
                    AND (@CustomerName IS NULL OR t.CustomerName = @CustomerName)
                    AND r.rgsDescription IS NOT NULL
                GROUP BY r.rgsDescription, r.rgsCode
                ORDER BY TotalExpense DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TopN", topN);
            cmd.Parameters.AddWithValue("@StartDate", rangeStart.Date);
            cmd.Parameters.AddWithValue("@EndDate", rangeEnd.Date.AddDays(1).AddTicks(-1));
            cmd.Parameters.AddWithValue("@CustomerName", customerName ?? (object)DBNull.Value);
            
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            var results = new List<CategoryExpenseResult>();
            while (await reader.ReadAsync())
            {
                results.Add(new CategoryExpenseResult
                {
                    RgsDescription = reader.IsDBNull(0) ? null : reader.GetString(0),
                    RgsCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                    TotalExpense = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2)
                });
            }
            return results;
        }

        /// <summary>
        /// Optimized fuzzy customer name matching using trigram similarity
        /// </summary>
        public async Task<List<string>> FindSimilarCustomerNamesScalableAsync(string connectionString, string searchCustomerName, DateTime startDate, DateTime endDate)
        {
            var results = new List<string>();
            
            // Use trigram similarity for better fuzzy matching (if available)
            // Fallback to optimized LIKE operations with proper indexing
            string sql = @"
                SELECT TOP 10
                    t.CustomerName,
                    -- Calculate similarity score
                    CASE 
                        WHEN t.CustomerName = @ExactMatch THEN 100
                        WHEN t.CustomerName LIKE @StartsWith THEN 80
                        WHEN t.CustomerName LIKE @Contains THEN 40
                        ELSE 0
                    END AS SimilarityScore
                FROM dbo.inversbanktransaction t WITH (INDEX = IX_CustomerName_TransactionDate)
                WHERE t.TransactionDate >= @StartDate
                    AND t.TransactionDate < @EndDate
                    AND t.CustomerName IS NOT NULL
                    AND t.CustomerName != ''
                    AND (
                        t.CustomerName = @ExactMatch
                        OR t.CustomerName LIKE @StartsWith
                        OR t.CustomerName LIKE @Contains
                    )
                GROUP BY t.CustomerName
                ORDER BY SimilarityScore DESC, COUNT(*) DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            
            cmd.Parameters.AddWithValue("@SearchName", searchCustomerName);
            cmd.Parameters.AddWithValue("@ExactMatch", searchCustomerName);
            cmd.Parameters.AddWithValue("@StartsWith", searchCustomerName + "%");
            cmd.Parameters.AddWithValue("@Contains", "%" + searchCustomerName + "%");
            cmd.Parameters.AddWithValue("@StartDate", startDate.Date);
            cmd.Parameters.AddWithValue("@EndDate", endDate.Date.AddDays(1).AddTicks(-1));

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(reader.GetString("CustomerName"));
            }

            return results;
        }

        /// <summary>
        /// Generate specialized embeddings (same as original but with better error handling)
        /// </summary>
        private async Task<Dictionary<string, float[]>> GenerateSpecializedEmbeddingsAsync(InversBankTransaction transaction)
        {
            var embeddings = new Dictionary<string, float[]>();
            
            // Get RGS mapping for better context
            var rgsMapping = await _context.RgsMappings
                .FirstOrDefaultAsync(r => r.RgsCode == transaction.RgsCode);

            // 1. Content Embedding
            string contentText = $"{transaction.Description ?? ""} {rgsMapping?.RgsDescription ?? ""} {rgsMapping?.RgsShortDescription ?? ""}";
            embeddings["ContentEmbedding"] = await _embeddingService.GetEmbeddingAsync(contentText);

            // 2. Amount Embedding
            string amountText = $"amount {transaction.Amount} currency money payment transaction value financial";
            embeddings["AmountEmbedding"] = await _embeddingService.GetEmbeddingAsync(amountText);

            // 3. Date Embedding
            string dateText = transaction.TransactionDate.HasValue 
                ? $"date {transaction.TransactionDate.Value:yyyy-MM-dd} month {transaction.TransactionDate.Value:MMMM} year {transaction.TransactionDate.Value:yyyy}"
                : "date unknown";
            embeddings["DateEmbedding"] = await _embeddingService.GetEmbeddingAsync(dateText);

            // 4. Category Embedding
            string categoryText = $"category {transaction?.CategoryName} with category description {rgsMapping?.RgsDescription ?? ""} {rgsMapping?.RgsShortDescription ?? ""}";
            embeddings["CategoryEmbedding"] = await _embeddingService.GetEmbeddingAsync(categoryText);

            // 5. Combined Embedding
            string combinedText = $"{contentText} {amountText} {dateText} {categoryText}";
            embeddings["CombinedEmbedding"] = await _embeddingService.GetEmbeddingAsync(combinedText);

            return embeddings;
        }
    }
} 