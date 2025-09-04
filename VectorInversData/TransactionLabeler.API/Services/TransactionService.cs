using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TransactionLabeler.API.Data;
using TransactionLabeler.API.Models;

namespace TransactionLabeler.API.Services
{
    public interface ITransactionService
    {
        Task UpdateAllInversBankTransactionEmbeddingsAsync(string connectionString);
        Task UpdateEmbeddingsForAccountAsync(string connectionString, string transactionIdentifierAccountNumber);
        
        // Essential methods needed by FinancialTools
        Task<List<CategorySearchResult>> SearchCategoriesByVectorAsync(string connectionString, string categoryQuery, int topCategories = 5, string? customerName = null);
        Task<List<TransactionResult>> GetTopTransactionsForCategoriesAsync(string connectionString, List<string> rgsCodes, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null);
        Task<List<TransactionResult>> GetTopTransactionsForCategoryQueryAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null, int topCategories = 3);
        
        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesForDateRangeAsync(string connectionString, DateTime startDate, DateTime endDate, string? customerName = null, int topN = 5);
        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexibleAsync(string connectionString, DateTime? startDate = null, DateTime? endDate = null, int? year = null, string? customerName = null, int topN = 5);
        Task<CategorySpendingResult> GetCategorySpendingAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, string? customerName = null);
        Task<string> ExecuteCustomSQLQueryAsync(string sqlQuery, string connectionString);
        
        // Transaction monitoring and anomaly detection methods
        Task<string> AnalyzeCounterpartyActivityAsync(string connectionString, int currentPeriodDays, int historicalPeriodDays, string? customerName, decimal? minAmount, decimal? maxAmount, string? transactionType);
        Task<string> AnalyzeTransactionAnomaliesAsync(string connectionString, int periodDays, string? customerName, double thresholdMultiplier, string comparisonMethod, string? transactionType);
        Task<string> GetTransactionProfilesAsync(string connectionString, int periodMonths, string? customerName, string? counterpartyAccount, string? transactionType, bool includeStatistics);
        
        // Customer name validation and suggestion methods
        Task<CustomerNameValidationResult> ValidateCustomerNameAsync(string connectionString, string? customerName);
        Task<List<string>> GetAvailableCustomerNamesAsync(string connectionString, int limit = 50);
    }

    public class TransactionService : ITransactionService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmbeddingService _embeddingService;

        public TransactionService(ApplicationDbContext context, IEmbeddingService embeddingService)
        {
            _context = context;
            _embeddingService = embeddingService;
        }



        // Batch update embeddings for all rows in inversbanktransaction
        public async Task UpdateAllInversBankTransactionEmbeddingsAsync(string connectionString)
        {
            const int pageSize = 200;
            const int maxParallelism = 8; // Throttle to avoid Azure SQL resource limits
            int page = 0;
            List<InversBankTransaction> batch;

            do
            {
                // Join inversbanktransaction with rgsmapping to get rgsDescription and rgsShortDescription
                batch = await (from t in _context.InversBankTransactions
                              join r in _context.RgsMappings on t.RgsCode equals r.RgsCode into rgsJoin
                              from rgs in rgsJoin.DefaultIfEmpty()
                              where t.ContentEmbedding == null || t.AmountEmbedding == null || t.DateEmbedding == null || t.CategoryEmbedding == null || t.CombinedEmbedding == null
                              orderby t.Id
                              select new InversBankTransaction
                              {
                                  Id = t.Id,
                                  Description = t.Description,
                                  Amount = t.Amount,
                                  TransactionDate = t.TransactionDate,
                                  BankAccountName = t.BankAccountName,
                                  BankAccountNumber = t.BankAccountNumber,
                                  TransactionType = t.TransactionType,
                                  RgsCode = t.RgsCode,
                                  // For embedding text
                                  CategoryName = rgs.RgsDescription,
                                  BusinessId = rgs.RgsShortDescription
                              })
                              .Skip(page * pageSize)
                              .Take(pageSize)
                              .ToListAsync();

                using var semaphore = new SemaphoreSlim(maxParallelism);
                await Task.WhenAll(batch.Select(async row =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Generate specialized embeddings for different query types
                        var embeddings = await GenerateSpecializedEmbeddingsAsync(row);
                        await InsertMultipleEmbeddingsAsync(connectionString, row.Id, embeddings);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                page++;
            } while (batch.Count == pageSize);
        }

        public async Task UpdateEmbeddingsForAccountAsync(string connectionString, string transactionIdentifierAccountNumber)
        {
            const int pageSize = 1500;
            const int maxParallelism = 20; // Lower parallelism for targeted updates
            int page = 0;
            List<InversBankTransaction> batch;

            do
            {
                // Get transactions for the specific account that need embedding updates
                batch = await (from t in _context.InversBankTransactions
                              join r in _context.RgsMappings on t.RgsCode equals r.RgsCode into rgsJoin
                              from rgs in rgsJoin.DefaultIfEmpty()
                              where t.TransactionIdentifierAccountNumber == transactionIdentifierAccountNumber
                                    && (t.ContentEmbedding == null || t.AmountEmbedding == null || t.DateEmbedding == null || t.CategoryEmbedding == null || t.CombinedEmbedding == null)
                              orderby t.Id
                              select new InversBankTransaction
                              {
                                  Id = t.Id,
                                  Description = t.Description,
                                  Amount = t.Amount,
                                  TransactionDate = t.TransactionDate,
                                  BankAccountName = t.BankAccountName,
                                  BankAccountNumber = t.BankAccountNumber,
                                  TransactionType = t.TransactionType,
                                  RgsCode = t.RgsCode,
                                  TransactionIdentifierAccountNumber = t.TransactionIdentifierAccountNumber,
                                  CustomerName = t.CustomerName,
                                  // For embedding text
                                  CategoryName = rgs.RgsDescription,
                                  BusinessId = rgs.RgsShortDescription
                              })
                    .Skip(page * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                if (!batch.Any())
                    break;

                using var semaphore = new SemaphoreSlim(maxParallelism);
                await Task.WhenAll(batch.Select(async row =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Generate specialized embeddings for different query types
                        var embeddings = await GenerateSpecializedEmbeddingsAsync(row);
                        await InsertMultipleEmbeddingsAsync(connectionString, row.Id, embeddings);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                page++;
            } while (batch.Count == pageSize);
        }

        public static async Task InsertMultipleEmbeddingsAsync(string connectionString, Guid id, Dictionary<string, float[]> embeddings)
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            foreach (var kvp in embeddings)
            {
                // For VECTOR columns, we need to cast the JSON array to VECTOR type
                string embeddingJson = "[" + string.Join(",", kvp.Value.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";
                string safeEmbeddingJson = embeddingJson.Replace("'", "''");
                
                // Use CAST to convert JSON array to VECTOR type
                string sql = $"UPDATE dbo.inversbanktransaction SET {kvp.Key} = CAST('{safeEmbeddingJson}' AS VECTOR({kvp.Value.Length})) WHERE id = @id";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private async Task<Dictionary<string, float[]>> GenerateSpecializedEmbeddingsAsync(InversBankTransaction row)
        {
            var embeddings = new Dictionary<string, float[]>();

            // 1. Content Embedding (for description-based queries)
            string contentText = $"{row.Description ?? ""} {row.CategoryName ?? ""} {row.BusinessId ?? ""}";
            embeddings["ContentEmbedding"] = await _embeddingService.GetEmbeddingAsync(contentText);

            // 2. Amount Embedding (for amount-based queries)
            string amountText = $"amount {row.Amount} currency money payment transaction value financial";
            embeddings["AmountEmbedding"] = await _embeddingService.GetEmbeddingAsync(amountText);

            // 3. Date Embedding (for temporal queries)
            string dateText = row.TransactionDate.HasValue
                ? $"date {row.TransactionDate.Value:yyyy-MM-dd} month {row.TransactionDate.Value:MMMM} year {row.TransactionDate.Value:yyyy} day {row.TransactionDate.Value:dd} weekday {row.TransactionDate.Value:dddd}"
                : "date unknown";
            embeddings["DateEmbedding"] = await _embeddingService.GetEmbeddingAsync(dateText);

            // 4. Category Embedding (for category-based queries)
            string categoryText = $"category {row.RgsCode ?? ""} {row.CategoryName ?? ""} {row.BusinessId ?? ""} type {row.TransactionType ?? ""}";
            embeddings["CategoryEmbedding"] = await _embeddingService.GetEmbeddingAsync(categoryText);

            // 5. Combined Embedding (for general semantic search)
            string combinedText = $"{contentText} {amountText} {dateText} {categoryText}";
            embeddings["CombinedEmbedding"] = await _embeddingService.GetEmbeddingAsync(combinedText);

            return embeddings;
        }

        // New category-based transaction search methods - works for ANY category mentioned in the query
        public async Task<List<CategorySearchResult>> SearchCategoriesByVectorAsync(string connectionString, string categoryQuery, int topCategories = 5, string? customerName = null)
        {
            // Handle empty category query - just return all categories
            if (string.IsNullOrWhiteSpace(categoryQuery))
            {
                return await GetAllCategoriesAsync(connectionString, topCategories, customerName);
            }

            // Get embedding for the category query (only when not empty)
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(categoryQuery);
            string embeddingJson = "[" + string.Join(",", queryEmbedding.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";

            var whereConditions = new List<string>();
            var parameters = new List<SqlParameter>();

            // Base condition for vector search
            whereConditions.Add("t.CategoryEmbedding IS NOT NULL");

            // Add customer name filter if provided
            if (!string.IsNullOrWhiteSpace(customerName))
            {
                whereConditions.Add("t.CustomerName = @CustomerName");
                parameters.Add(new SqlParameter("@CustomerName", customerName));
            }

            string whereClause = "WHERE " + string.Join(" AND ", whereConditions);

            string sql = $@"
                SELECT TOP (@TopCategories)
                    r.rgsDescription AS RgsDescription,
                    r.rgsCode AS RgsCode,
                    VECTOR_DISTANCE('cosine', t.CategoryEmbedding, CAST('{embeddingJson}' AS VECTOR({queryEmbedding.Length}))) AS similarity
                FROM dbo.inversbanktransaction t
                LEFT JOIN dbo.rgsmapping r ON t.rgsCode = r.rgsCode
                {whereClause}
                ORDER BY similarity ASC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TopCategories", topCategories);
            cmd.CommandTimeout = 120; // 120 second timeout for database operations

            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<CategorySearchResult>();
            while (await reader.ReadAsync())
            {
                results.Add(new CategorySearchResult
                {
                    RgsDescription = reader.IsDBNull(0) ? null : reader.GetString(0),
                    RgsCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Similarity = (float)Convert.ToDouble(reader[2])
                });
            }
            return results;
        }

        // Helper method to get all categories (for empty category query)
        private async Task<List<CategorySearchResult>> GetAllCategoriesAsync(string connectionString, int topCategories, string? customerName)
        {
            // If topCategories is very high (like 1000), treat it as "get all" and remove TOP clause
            bool getAllCategories = topCategories >= 1000;
            
            if (string.IsNullOrWhiteSpace(customerName) || customerName.ToLower().Contains("all"))
            {
                string sql = getAllCategories ? @"
                    SELECT 
                        r.rgsDescription AS RgsDescription,
                        r.rgsCode AS RgsCode,
                        1.0 AS similarity
                    FROM dbo.rgsmapping r
                    WHERE r.rgsDescription IS NOT NULL
                    ORDER BY r.rgsDescription ASC" : $@"
                    SELECT TOP (@TopCategories)
                        r.rgsDescription AS RgsDescription,
                        r.rgsCode AS RgsCode,
                        1.0 AS similarity
                    FROM dbo.rgsmapping r
                    WHERE r.rgsDescription IS NOT NULL
                    ORDER BY r.rgsDescription ASC";

                using var conn = new SqlConnection(connectionString);
                using var cmd = new SqlCommand(sql, conn);
                if (!getAllCategories)
                {
                    cmd.Parameters.AddWithValue("@TopCategories", topCategories);
                }

                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync();
                var results = new List<CategorySearchResult>();
                while (await reader.ReadAsync())
                {
                    results.Add(new CategorySearchResult
                    {
                        RgsDescription = reader.IsDBNull(0) ? null : reader.GetString(0),
                        RgsCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Similarity = (float)Convert.ToDouble(reader[2])
                    });
                }
                return results;
            }
            else
            {
                // For specific customer queries, filter by customer transactions
                string sql = getAllCategories ? @"
                    SELECT 
                        r.rgsDescription AS RgsDescription,
                        r.rgsCode AS RgsCode,
                        1.0 AS similarity
                    FROM dbo.inversbanktransaction t
                    LEFT JOIN dbo.rgsmapping r ON t.rgsCode = r.rgsCode
                    WHERE t.CustomerName = @CustomerName
                        AND r.rgsDescription IS NOT NULL
                    GROUP BY r.rgsDescription, r.rgsCode
                    ORDER BY r.rgsDescription ASC" : $@"
                    SELECT TOP (@TopCategories)
                        r.rgsDescription AS RgsDescription,
                        r.rgsCode AS RgsCode,
                        1.0 AS similarity
                    FROM dbo.inversbanktransaction t
                    LEFT JOIN dbo.rgsmapping r ON t.rgsCode = r.rgsCode
                    WHERE t.CustomerName = @CustomerName
                        AND r.rgsDescription IS NOT NULL
                    GROUP BY r.rgsDescription, r.rgsCode
                    ORDER BY r.rgsDescription ASC";

                using var conn = new SqlConnection(connectionString);
                using var cmd = new SqlCommand(sql, conn);
                if (!getAllCategories)
                {
                    cmd.Parameters.AddWithValue("@TopCategories", topCategories);
                }
                cmd.Parameters.AddWithValue("@CustomerName", customerName);

                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync();
                var results = new List<CategorySearchResult>();
                while (await reader.ReadAsync())
                {
                    results.Add(new CategorySearchResult
                    {
                        RgsDescription = reader.IsDBNull(0) ? null : reader.GetString(0),
                        RgsCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Similarity = (float)Convert.ToDouble(reader[2])
                    });
                }
                return results;
            }
        }

        public async Task<List<TransactionResult>> GetTopTransactionsForCategoriesAsync(string connectionString, List<string> rgsCodes, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null)
        {
            var results = new List<TransactionResult>();
            
            // Build date filter
            string dateFilter = "";
            if (startDate.HasValue && endDate.HasValue)
            {
                dateFilter = "AND t.TransactionDate >= @StartDate AND t.TransactionDate < @EndDate";
            }
            else if (year.HasValue)
            {
                dateFilter = "AND YEAR(t.TransactionDate) = @Year";
            }

            string sql = $@"
                SELECT TOP (@TopN)
                    t.Description,
                    t.Amount,
                    t.TransactionDate,
                    t.RgsCode,
                    r.rgsDescription,
                    t.BankAccountName,
                    t.CustomerName
                FROM dbo.inversbanktransaction t
                LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                WHERE t.RgsCode IN ({string.Join(",", rgsCodes.Select((_, i) => $"@RgsCode{i}"))})
                    {dateFilter}
                    AND (@CustomerName IS NULL OR t.CustomerName LIKE @CustomerName)
                ORDER BY t.TransactionDate DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TopN", topN);
            cmd.Parameters.AddWithValue("@CustomerName", customerName ?? (object)DBNull.Value);
            
            if (startDate.HasValue && endDate.HasValue)
            {
                cmd.Parameters.AddWithValue("@StartDate", startDate.Value.Date);
                cmd.Parameters.AddWithValue("@EndDate", endDate.Value.Date.AddDays(1).AddTicks(-1));
            }
            else if (year.HasValue)
            {
                cmd.Parameters.AddWithValue("@Year", year.Value);
            }

            // Add RGS code parameters
            for (int i = 0; i < rgsCodes.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@RgsCode{i}", rgsCodes[i]);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(new TransactionResult
                {
                    Description = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Amount = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1),
                    TransactionDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                    RgsCode = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    RgsDescription = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    BankAccountName = reader.IsDBNull(5) ? "" : reader.GetString(5)
                });
            }

            return results;
        }

        public async Task<List<TransactionResult>> GetTopTransactionsForCategoryQueryAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null, int topCategories = 3)
        {
            // First, find the most relevant categories using vector search
            var categories = await SearchCategoriesByVectorAsync(connectionString, categoryQuery, topCategories, customerName);
            
            if (!categories.Any())
                return new List<TransactionResult>();

            // Get transactions for the top categories
            var rgsCodes = categories.Select(c => c.RgsCode).ToList();
            return await GetTopTransactionsForCategoriesAsync(connectionString, rgsCodes, startDate, endDate, year, topN, customerName);
        }

        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesForDateRangeAsync(string connectionString, DateTime startDate, DateTime endDate, string? customerName = null, int topN = 5)
        {
            var results = new List<CategoryExpenseResult>();
            string sql = $@"
                SELECT TOP (@TopN)
                    r.rgsDescription AS RgsDescription,
                    r.rgsCode AS RgsCode,
                    SUM(ABS(t.Amount)) AS TotalExpense
                FROM dbo.inversbanktransaction t
                LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                WHERE t.af_bij = 'Af'
                    AND t.TransactionDate >= @StartDate
                    AND t.TransactionDate < @EndDate
                    AND (@CustomerName IS NULL OR t.CustomerName LIKE @CustomerName)
                    AND r.rgsDescription IS NOT NULL
                GROUP BY r.rgsDescription, r.rgsCode
                ORDER BY TotalExpense DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TopN", topN);
            cmd.Parameters.AddWithValue("@StartDate", startDate.Date);
            cmd.Parameters.AddWithValue("@EndDate", endDate.Date.AddDays(1).AddTicks(-1)); // End of the day
            cmd.Parameters.AddWithValue("@CustomerName", customerName ?? (object)DBNull.Value);
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
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

        public async Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexibleAsync(string connectionString, DateTime? startDate, DateTime? endDate, int? year, string? customerName = null, int topN = 5)
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

            return await GetTopExpenseCategoriesForDateRangeAsync(connectionString, rangeStart, rangeEnd, customerName, topN);
        }

        public async Task<CategorySpendingResult> GetCategorySpendingAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, string? customerName = null)
        {
            // Determine date range
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

            // First, find the most relevant categories using vector search
            var categories = await SearchCategoriesByVectorAsync(connectionString, categoryQuery, 5, customerName);
            
            if (!categories.Any())
                return new CategorySpendingResult
                {
                    CategoryQuery = categoryQuery,
                    TotalSpending = 0,
                    TransactionCount = 0,
                    StartDate = rangeStart,
                    EndDate = rangeEnd,
                    CustomerName = customerName,
                    Breakdown = new List<CategorySpendingBreakdown>()
                };

            var rgsCodes = categories.Select(c => c.RgsCode).ToList();
            
            // Get spending breakdown for the categories
            string sql = $@"
                SELECT 
                    r.rgsCode AS RgsCode,
                    r.rgsDescription AS RgsDescription,
                    r.rgsShortDescription AS RgsShortDescription,
                    SUM(ABS(t.Amount)) AS Amount,
                    COUNT(*) AS TransactionCount
                FROM dbo.inversbanktransaction t
                LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                WHERE t.af_bij = 'Af'
                    AND t.RgsCode IN ({string.Join(",", rgsCodes.Select((_, i) => $"@RgsCode{i}"))})
                    AND t.TransactionDate >= @StartDate
                    AND t.TransactionDate < @EndDate
                    AND (@CustomerName IS NULL OR t.CustomerName LIKE @CustomerName)
                GROUP BY r.rgsCode, r.rgsDescription, r.rgsShortDescription
                ORDER BY Amount DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@StartDate", rangeStart.Date);
            cmd.Parameters.AddWithValue("@EndDate", rangeEnd.Date.AddDays(1).AddTicks(-1));
            cmd.Parameters.AddWithValue("@CustomerName", customerName ?? (object)DBNull.Value);
            
            // Add RGS code parameters
            for (int i = 0; i < rgsCodes.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@RgsCode{i}", rgsCodes[i]);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            var breakdown = new List<CategorySpendingBreakdown>();
            decimal totalSpending = 0;
            int totalTransactions = 0;
            
            while (await reader.ReadAsync())
            {
                var amount = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3);
                var transactionCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                
                totalSpending += amount;
                totalTransactions += transactionCount;
                
                breakdown.Add(new CategorySpendingBreakdown
                {
                    RgsCode = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    RgsDescription = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    RgsShortDescription = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Amount = amount,
                    TransactionCount = transactionCount
                });
            }

            return new CategorySpendingResult
            {
                CategoryQuery = categoryQuery,
                TotalSpending = totalSpending,
                TransactionCount = totalTransactions,
                StartDate = rangeStart,
                EndDate = rangeEnd,
                CustomerName = customerName,
                Breakdown = breakdown
            };
        }



        /// <summary>
        /// Execute custom SQL queries for complex financial analysis
        /// </summary>
        public async Task<string> ExecuteCustomSQLQueryAsync(string sqlQuery, string connectionString)
        {
            try
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sqlQuery, connection);
                command.CommandTimeout = 30; // 30 second timeout

                using var reader = await command.ExecuteReaderAsync();
                
                var results = new List<Dictionary<string, object>>();
                var columns = new List<string>();

                // Get column names
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                // Read data
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[columns[i]] = value;
                    }
                    results.Add(row);
                }

                // Format results as a readable string
                if (results.Count == 0)
                {
                    return "📊 Query executed successfully. No results found.";
                }

                var formattedResults = FormatSQLResults(columns, results);
                return $"📊 Query executed successfully. Found {results.Count} results:\n\n{formattedResults}";
            }
            catch (Exception ex)
            {
                throw new Exception($"Database execution error: {ex.Message}");
            }
        }

        /// <summary>
        /// Format SQL results as a readable markdown table
        /// </summary>
        private static string FormatSQLResults(List<string> columns, List<Dictionary<string, object>> results)
        {
            var output = new System.Text.StringBuilder();

            // Header
            output.AppendLine("| " + string.Join(" | ", columns) + " |");
            output.AppendLine("|" + string.Join("|", columns.Select(c => "---")) + "|");

            // Data rows
            foreach (var row in results.Take(100)) // Limit to first 100 rows for readability
            {
                var values = columns.Select(col => 
                {
                    var value = row.ContainsKey(col) ? row[col] : null;
                    return value?.ToString() ?? "NULL";
                });
                output.AppendLine("| " + string.Join(" | ", values) + " |");
            }

            if (results.Count > 100)
            {
                output.AppendLine($"\n... and {results.Count - 100} more rows (showing first 100 for readability)");
            }

            return output.ToString();
        }

        /// <summary>
        /// Analyzes counterparty activity to detect unknown/new counterparties
        /// </summary>
        public async Task<string> AnalyzeCounterpartyActivityAsync(string connectionString, int currentPeriodDays, int historicalPeriodDays, string? customerName, decimal? minAmount, decimal? maxAmount, string? transactionType)
        {
            try
            {
                var currentPeriodEnd = DateTime.UtcNow;
                var currentPeriodStart = currentPeriodEnd.AddDays(-currentPeriodDays);
                var historicalPeriodEnd = currentPeriodStart;
                var historicalPeriodStart = historicalPeriodEnd.AddDays(-historicalPeriodDays);

                // Build filters
                var customerFilter = string.IsNullOrEmpty(customerName) ? "" : $"AND CustomerName = '{customerName.Replace("'", "''")}'";
                var amountFilter = "";
                if (minAmount.HasValue || maxAmount.HasValue)
                {
                    if (minAmount.HasValue && maxAmount.HasValue)
                        amountFilter = $"AND Amount BETWEEN {minAmount.Value} AND {maxAmount.Value}";
                    else if (minAmount.HasValue)
                        amountFilter = $"AND Amount >= {minAmount.Value}";
                    else
                        amountFilter = $"AND Amount <= {maxAmount.Value}";
                }
                var typeFilter = string.IsNullOrEmpty(transactionType) ? "" : $"AND af_bij = '{transactionType}'";

                // Find unknown counterparties (in current period but not in historical period)
                var unknownCounterpartiesQuery = $@"
                    SELECT DISTINCT p.BankAccountNumber, p.CustomerName, p.TransactionDate, p.Amount, p.Description, p.af_bij
                    FROM dbo.inversbanktransaction p
                    WHERE p.TransactionDate > '{currentPeriodStart:yyyy-MM-dd}' 
                        AND p.TransactionDate <= '{currentPeriodEnd:yyyy-MM-dd}'
                        {customerFilter}
                        {amountFilter}
                        {typeFilter}
                        AND p.BankAccountNumber NOT IN (
                            SELECT DISTINCT BankAccountNumber
                            FROM dbo.inversbanktransaction
                            WHERE TransactionDate > '{historicalPeriodStart:yyyy-MM-dd}' 
                                AND TransactionDate <= '{historicalPeriodEnd:yyyy-MM-dd}'
                                {customerFilter}
                                {amountFilter}
                                {typeFilter}
                        )
                    ORDER BY p.TransactionDate DESC, p.Amount DESC";

                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                using var cmd = new SqlCommand(unknownCounterpartiesQuery, conn);
                using var reader = await cmd.ExecuteReaderAsync();

                var results = new List<Dictionary<string, object>>();
                var columns = new List<string> { "BankAccountNumber", "CustomerName", "TransactionDate", "Amount", "Description", "TransactionType" };

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>
                    {
                        ["BankAccountNumber"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ["CustomerName"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ["TransactionDate"] = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                        ["Amount"] = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                        ["Description"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        ["TransactionType"] = reader.IsDBNull(5) ? "" : reader.GetString(5)
                    };
                    results.Add(row);
                }

                if (results.Count == 0)
                {
                    return $"🔍 **Counterparty Activity Analysis**\n\n" +
                           $"**Periods:** Current: {currentPeriodDays} days, Historical: {historicalPeriodDays} days\n" +
                           $"**Customer:** {customerName ?? "All"}\n" +
                           $"**Result:** No unknown counterparties found in the current period.\n\n" +
                           $"All counterparties in the current period were also active in the historical period.";
                }

                var formattedResults = FormatSQLResults(columns, results);
                return $"🔍 **Counterparty Activity Analysis**\n\n" +
                       $"**Periods:** Current: {currentPeriodDays} days, Historical: {historicalPeriodDays} days\n" +
                       $"**Customer:** {customerName ?? "All"}\n" +
                       $"**Unknown Counterparties Found:** {results.Count}\n\n" +
                       $"**Unknown Counterparty Transactions:**\n\n{formattedResults}\n\n" +
                       $"**Analysis:** These counterparties appeared in the current period but were not active in the historical period, indicating new or recently reactivated business relationships.";
            }
            catch (Exception ex)
            {
                throw new Exception($"Error analyzing counterparty activity: {ex.Message}");
            }
        }

        /// <summary>
        /// Analyzes transaction anomalies by comparing current vs historical patterns
        /// </summary>
        public async Task<string> AnalyzeTransactionAnomaliesAsync(string connectionString, int periodDays, string? customerName, double thresholdMultiplier, string comparisonMethod, string? transactionType)
        {
            try
            {
                var currentPeriodEnd = DateTime.UtcNow;
                var currentPeriodStart = currentPeriodEnd.AddDays(-periodDays);
                var historicalPeriodEnd = currentPeriodStart;
                var historicalPeriodStart = historicalPeriodEnd.AddMonths(-15); // 15 months of history

                var customerFilter = string.IsNullOrEmpty(customerName) ? "" : $"AND CustomerName = '{customerName.Replace("'", "''")}'";
                var typeFilter = string.IsNullOrEmpty(transactionType) ? "" : $"AND af_bij = '{transactionType}'";

                // Get historical transaction profiles for all counterparties
                var historicalProfilesQuery = $@"
                    SELECT 
                        BankAccountNumber,
                        MAX(Amount) as HistoricalMaxAmount,
                        MIN(Amount) as HistoricalMinAmount,
                        AVG(Amount) as HistoricalAvgAmount,
                        COUNT(*) as HistoricalTransactionCount
                    FROM dbo.inversbanktransaction
                    WHERE TransactionDate > '{historicalPeriodStart:yyyy-MM-dd}' 
                        AND TransactionDate <= '{historicalPeriodEnd:yyyy-MM-dd}'
                        {customerFilter}
                        {typeFilter}
                    GROUP BY BankAccountNumber
                    HAVING COUNT(*) >= 3"; // Only counterparties with at least 3 historical transactions

                // Get current period transactions
                var currentTransactionsQuery = $@"
                    SELECT 
                        p.BankAccountNumber,
                        p.CustomerName,
                        p.TransactionDate,
                        p.Amount,
                        p.Description,
                        p.af_bij,
                        h.HistoricalMaxAmount,
                        h.HistoricalMinAmount,
                        h.HistoricalAvgAmount,
                        h.HistoricalTransactionCount
                    FROM dbo.inversbanktransaction p
                    LEFT JOIN ({historicalProfilesQuery}) h ON p.BankAccountNumber = h.BankAccountNumber
                    WHERE p.TransactionDate > '{currentPeriodStart:yyyy-MM-dd}' 
                        AND p.TransactionDate <= '{currentPeriodEnd:yyyy-MM-dd}'
                        {customerFilter}
                        {typeFilter}
                        AND h.BankAccountNumber IS NOT NULL
                    ORDER BY p.TransactionDate DESC, p.Amount DESC";

                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                using var cmd = new SqlCommand(currentTransactionsQuery, conn);
                cmd.CommandTimeout = 120; // 60 second timeout for complex queries
                using var reader = await cmd.ExecuteReaderAsync();

                var results = new List<Dictionary<string, object>>();
                var columns = new List<string> { "BankAccountNumber", "CustomerName", "TransactionDate", "Amount", "Description", "TransactionType", "HistoricalMax", "HistoricalMin", "HistoricalAvg", "AnomalyType" };

                while (await reader.ReadAsync())
                {
                    var currentAmount = reader.GetDecimal(3);
                    var historicalMax = reader.GetDecimal(6);
                    var historicalMin = reader.GetDecimal(7);
                    var historicalAvg = reader.GetDecimal(8);

                    var anomalyType = "";
                    if (currentAmount > historicalMax * (decimal)thresholdMultiplier)
                        anomalyType = $"🚨 HIGH: {currentAmount:C} > {historicalMax * (decimal)thresholdMultiplier:C} ({thresholdMultiplier}x historical max)";
                    else if (currentAmount < historicalMin / (decimal)thresholdMultiplier)
                        anomalyType = $"🚨 LOW: {currentAmount:C} < {historicalMin / (decimal)thresholdMultiplier:C} ({thresholdMultiplier}x below historical min)";
                    else if (currentAmount > historicalMax)
                        anomalyType = $"⚠️ Above: {currentAmount:C} > {historicalMax:C} (historical max)";
                    else if (currentAmount < historicalMin)
                        anomalyType = $"⚠️ Below: {currentAmount:C} < {historicalMin:C} (historical min)";
                    else
                        anomalyType = "✅ Normal";

                    var row = new Dictionary<string, object>
                    {
                        ["BankAccountNumber"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ["CustomerName"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ["TransactionDate"] = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                        ["Amount"] = currentAmount,
                        ["Description"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        ["TransactionType"] = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        ["HistoricalMax"] = historicalMax,
                        ["HistoricalMin"] = historicalMin,
                        ["HistoricalAvg"] = historicalAvg,
                        ["AnomalyType"] = anomalyType
                    };
                    results.Add(row);
                }

                if (results.Count == 0)
                {
                    return $"🚨 **Transaction Anomaly Analysis**\n\n" +
                           $"**Period:** {periodDays} days\n" +
                           $"**Customer:** {customerName ?? "All"}\n" +
                           $"**Threshold:** {thresholdMultiplier}x normal\n" +
                           $"**Result:** No transactions found for analysis.";
                }

                var formattedResults = FormatSQLResults(columns, results);
                return $"🚨 **Transaction Anomaly Analysis**\n\n" +
                       $"**Period:** {periodDays} days\n" +
                       $"**Customer:** {customerName ?? "All"}\n" +
                       $"**Threshold:** {thresholdMultiplier}x normal\n" +
                       $"**Transactions Analyzed:** {results.Count}\n\n" +
                       $"**Transaction Analysis Results:**\n\n{formattedResults}\n\n" +
                       $"**Legend:**\n" +
                       $"🚨 HIGH: Amount significantly above historical maximum\n" +
                       $"🚨 LOW: Amount significantly below historical minimum\n" +
                       $"⚠️ Above: Amount above historical maximum but within threshold\n" +
                       $"⚠️ Below: Amount below historical minimum but within threshold\n" +
                       $"✅ Normal: Amount within historical range";
            }
            catch (Exception ex)
            {
                throw new Exception($"Error analyzing transaction anomalies: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets transaction profiles and historical statistics for counterparties
        /// </summary>
        public async Task<string> GetTransactionProfilesAsync(string connectionString, int periodMonths, string? customerName, string? counterpartyAccount, string? transactionType, bool includeStatistics)
        {
            try
            {
                var periodEnd = DateTime.UtcNow;
                var periodStart = periodEnd.AddMonths(-periodMonths);

                var customerFilter = string.IsNullOrEmpty(customerName) ? "" : $"AND CustomerName = '{customerName.Replace("'", "''")}'";
                var counterpartyFilter = string.IsNullOrEmpty(counterpartyAccount) ? "" : $"AND BankAccountNumber = '{counterpartyAccount.Replace("'", "''")}'";
                var typeFilter = string.IsNullOrEmpty(transactionType) ? "" : $"AND af_bij = '{transactionType}'";

                var query = $@"
                    SELECT 
                        BankAccountNumber,
                        CustomerName,
                        COUNT(*) as TransactionCount,
                        SUM(Amount) as TotalAmount,
                        AVG(Amount) as AverageAmount,
                        MAX(Amount) as MaxAmount,
                        MIN(Amount) as MinAmount,
                        MAX(TransactionDate) as LastTransactionDate,
                        MIN(TransactionDate) as FirstTransactionDate";

                if (includeStatistics)
                {
                    query += @",
                        STDEV(Amount) as AmountStandardDeviation,
                        PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY Amount) OVER (PARTITION BY BankAccountNumber) as Amount95thPercentile,
                        PERCENTILE_CONT(0.05) WITHIN GROUP (ORDER BY Amount) OVER (PARTITION BY BankAccountNumber) as Amount5thPercentile";
                }

                query += $@"
                    FROM dbo.inversbanktransaction
                    WHERE TransactionDate > '{periodStart:yyyy-MM-dd}' 
                        AND TransactionDate <= '{periodEnd:yyyy-MM-dd}'
                        {customerFilter}
                        {counterpartyFilter}
                        {typeFilter}
                    GROUP BY BankAccountNumber, CustomerName
                    ORDER BY TotalAmount DESC, TransactionCount DESC";

                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                using var cmd = new SqlCommand(query, conn);
                using var reader = await cmd.ExecuteReaderAsync();

                var results = new List<Dictionary<string, object>>();
                var columns = new List<string> { "BankAccountNumber", "CustomerName", "TransactionCount", "TotalAmount", "AverageAmount", "MaxAmount", "MinAmount", "LastTransactionDate", "FirstTransactionDate" };

                if (includeStatistics)
                {
                    columns.AddRange(new[] { "AmountStdDev", "Amount95thPercentile", "Amount5thPercentile" });
                }

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>
                    {
                        ["BankAccountNumber"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ["CustomerName"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ["TransactionCount"] = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        ["TotalAmount"] = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                        ["AverageAmount"] = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
                        ["MaxAmount"] = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5),
                        ["MinAmount"] = reader.IsDBNull(6) ? 0m : reader.GetDecimal(6),
                        ["LastTransactionDate"] = reader.IsDBNull(7) ? DateTime.MinValue : reader.GetDateTime(7),
                        ["FirstTransactionDate"] = reader.IsDBNull(8) ? DateTime.MinValue : reader.GetDateTime(8)
                    };

                    if (includeStatistics)
                    {
                        row["AmountStdDev"] = reader.IsDBNull(9) ? 0m : reader.GetDecimal(9);
                        row["Amount95thPercentile"] = reader.IsDBNull(10) ? 0m : reader.GetDecimal(10);
                        row["Amount5thPercentile"] = reader.IsDBNull(11) ? 0m : reader.GetDecimal(11);
                    }

                    results.Add(row);
                }

                if (results.Count == 0)
                {
                    return $"📊 **Transaction Profile Analysis**\n\n" +
                           $"**Period:** {periodMonths} months\n" +
                           $"**Customer:** {customerName ?? "All"}\n" +
                           $"**Counterparty:** {counterpartyAccount ?? "All"}\n" +
                           $"**Result:** No transaction profiles found for the specified criteria.";
                }

                var formattedResults = FormatSQLResults(columns, results);
                return $"📊 **Transaction Profile Analysis**\n\n" +
                       $"**Period:** {periodMonths} months\n" +
                       $"**Customer:** {customerName ?? "All"}\n" +
                       $"**Counterparty:** {counterpartyAccount ?? "All"}\n" +
                       $"**Profiles Found:** {results.Count}\n" +
                       $"**Statistics Included:** {includeStatistics}\n\n" +
                       $"**Transaction Profiles:**\n\n{formattedResults}\n\n" +
                       $"**Profile Summary:**\n" +
                       $"• **Total Transactions:** {results.Sum(r => Convert.ToInt32(r["TransactionCount"]))}\n" +
                       $"• **Total Amount:** {results.Sum(r => Convert.ToDecimal(r["TotalAmount"])):C}\n" +
                       $"• **Average Transaction:** {results.Average(r => Convert.ToDecimal(r["AverageAmount"])):C}";
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting transaction profiles: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates customer name and suggests corrections if misspelled
        /// </summary>
        public async Task<CustomerNameValidationResult> ValidateCustomerNameAsync(string connectionString, string? customerName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(customerName))
                {
                    return new CustomerNameValidationResult
                    {
                        IsValid = true,
                        Message = "No customer name provided - will search all customers"
                    };
                }

                // First, try exact match
                var exactMatchQuery = @"
                    SELECT DISTINCT CustomerName 
                    FROM dbo.inversbanktransaction 
                    WHERE CustomerName = @CustomerName";

                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                using var exactCmd = new SqlCommand(exactMatchQuery, conn);
                exactCmd.Parameters.AddWithValue("@CustomerName", customerName);
                
                var exactMatch = await exactCmd.ExecuteScalarAsync();
                if (exactMatch != null)
                {
                    return new CustomerNameValidationResult
                    {
                        IsValid = true,
                        CorrectedName = exactMatch.ToString(),
                        Message = "Customer name found exactly"
                    };
                }

                // If no exact match, try fuzzy matching
                var fuzzyMatchQuery = @"
                    SELECT DISTINCT CustomerName,
                        CASE 
                            WHEN CustomerName LIKE @Pattern1 THEN 0.9
                            WHEN CustomerName LIKE @Pattern2 THEN 0.8
                            WHEN CustomerName LIKE @Pattern3 THEN 0.7
                            WHEN SOUNDEX(CustomerName) = SOUNDEX(@CustomerName) THEN 0.6
                            ELSE 0.5
                        END as SimilarityScore
                    FROM dbo.inversbanktransaction 
                    WHERE CustomerName LIKE @Pattern1 
                       OR CustomerName LIKE @Pattern2 
                       OR CustomerName LIKE @Pattern3
                       OR SOUNDEX(CustomerName) = SOUNDEX(@CustomerName)
                    ORDER BY SimilarityScore DESC";

                using var fuzzyCmd = new SqlCommand(fuzzyMatchQuery, conn);
                fuzzyCmd.Parameters.AddWithValue("@CustomerName", customerName);
                fuzzyCmd.Parameters.AddWithValue("@Pattern1", $"{customerName}%"); // Starts with
                fuzzyCmd.Parameters.AddWithValue("@Pattern2", $"%{customerName}%"); // Contains
                fuzzyCmd.Parameters.AddWithValue("@Pattern3", $"%{customerName}"); // Ends with

                using var reader = await fuzzyCmd.ExecuteReaderAsync();
                var suggestions = new List<string>();
                string? bestMatch = null;
                double bestScore = 0;

                while (await reader.ReadAsync())
                {
                    var suggestedName = reader.GetString(0);
                    var score = Convert.ToDouble(reader.GetDecimal(1));
                    
                    suggestions.Add(suggestedName);
                    
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = suggestedName;
                    }
                }

                if (bestMatch != null && bestScore >= 0.6)
                {
                    return new CustomerNameValidationResult
                    {
                        IsValid = false,
                        CorrectedName = bestMatch,
                        Suggestions = suggestions.Take(5).ToList(),
                        SimilarityScore = bestScore,
                        Message = $"Customer name '{customerName}' not found. Did you mean '{bestMatch}'? Here are similar names: {string.Join(", ", suggestions.Take(3))}"
                    };
                }

                // If no good fuzzy matches, get all available customer names
                var allCustomers = await GetAvailableCustomerNamesAsync(connectionString, 10);
                
                return new CustomerNameValidationResult
                {
                    IsValid = false,
                    Suggestions = allCustomers,
                    Message = $"Customer name '{customerName}' not found. Available customers: {string.Join(", ", allCustomers.Take(5))}"
                };
            }
            catch (Exception ex)
            {
                return new CustomerNameValidationResult
                {
                    IsValid = false,
                    Message = $"Error validating customer name: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Gets all available customer names from the database
        /// </summary>
        public async Task<List<string>> GetAvailableCustomerNamesAsync(string connectionString, int limit = 50)
        {
            try
            {
                var query = $@"
                    SELECT DISTINCT CustomerName 
                    FROM dbo.inversbanktransaction 
                    WHERE CustomerName IS NOT NULL AND CustomerName != ''
                    ORDER BY CustomerName
                    {(limit > 0 ? $"OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY" : "")}";

                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                using var cmd = new SqlCommand(query, conn);
                using var reader = await cmd.ExecuteReaderAsync();

                var customers = new List<string>();
                while (await reader.ReadAsync())
                {
                    customers.Add(reader.GetString(0));
                }

                return customers;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting available customer names: {ex.Message}");
            }
        }
    }

    // Customer name validation result model
    public class CustomerNameValidationResult
    {
        public bool IsValid { get; set; }
        public string? CorrectedName { get; set; }
        public List<string> Suggestions { get; set; } = new();
        public string? Message { get; set; }
        public double SimilarityScore { get; set; }
    }
}        