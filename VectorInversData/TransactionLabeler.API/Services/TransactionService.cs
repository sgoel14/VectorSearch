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
        Task UpdateAllPersistentBankStatementEmbeddingsAsync(string connectionString);
        Task<float[]> GetEmbeddingAsync(string text);
        Task<List<PersistentBankStatementLine>> GetAllPersistentBankStatementLinesWithEmbeddingsAsync();
        Task<List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>> VectorSearchInSqlAsync(string connectionString, float[] queryEmbedding);
        Task<List<PersistentBankStatementLine>> GetPersistentBankStatementLinesWithEmbeddingsPageAsync(int page, int pageSize);
        Task UpdateAllInversBankTransactionEmbeddingsAsync(string connectionString);
        
        // Essential methods needed by FinancialTools
        Task<List<CategorySearchResult>> SearchCategoriesByVectorAsync(string connectionString, string categoryQuery, int topCategories = 5, string? customerName = null);
        Task<List<TransactionResult>> GetTopTransactionsForCategoriesAsync(string connectionString, List<string> rgsCodes, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null);
        Task<List<TransactionResult>> GetTopTransactionsForCategoryQueryAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null, int topCategories = 3);
        
        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesForDateRangeAsync(string connectionString, DateTime startDate, DateTime endDate, string? customerName = null, int topN = 5);
        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexibleAsync(string connectionString, DateTime? startDate, DateTime? endDate, int? year, string? customerName = null, int topN = 5);
        Task<CategorySpendingResult> GetCategorySpendingAsync(string connectionString, string categoryQuery, DateTime? startDate, DateTime? endDate, int? year, string? customerName = null);
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

        // Batch update embeddings for all rows in persistentbankstatementline
        public async Task UpdateAllPersistentBankStatementEmbeddingsAsync(string connectionString)
        {
            const int pageSize = 200;
            const int maxParallelism = 8; // Throttle to avoid Azure SQL resource limits
            int page = 0;
            List<PersistentBankStatementLine> batch;

            do
            {
                batch = await _context.PersistentBankStatementLines
                    .Where(x => x.Embedding == null)
                    .OrderBy(x => x.Id)
                    .Skip(page * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                using var semaphore = new SemaphoreSlim(maxParallelism);
                await Task.WhenAll(batch.Select(async row =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string textForEmbedding = $"{row.Description ?? ""} {row.Amount} {row.TransactionDate} {row.BankAccountName ?? ""} {row.BankAccountNumber ?? ""} {row.TransactionType ?? ""}";
                        var embedding = await _embeddingService.GetEmbeddingAsync(textForEmbedding);
                        // Update the embedding directly in the database
                        await UpdateEmbeddingInDatabase(connectionString, row.Id, embedding);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                page++;
            } while (batch.Count == pageSize);
        }

        // Batch update embeddings for all rows in inversbanktransaction
        public async Task UpdateAllInversBankTransactionEmbeddingsAsync(string connectionString)
        {
            const int pageSize = 200;
            const int maxParallelism = 8; // Throttle to avoid Azure SQL resource limits
            int page = 0;
            while (true)
            {
                // Join inversbanktransaction with rgsmapping to get rgsDescription and rgsShortDescription
                // This ensures we use the semantic meaning (descriptions) rather than just codes for embeddings
                var joinedBatch = await (from t in _context.InversBankTransactions
                                        join r in _context.RgsMappings on t.RgsCode equals r.RgsCode into rgsJoin
                                        from r in rgsJoin.DefaultIfEmpty()
                                        where t.Embedding == null
                                        orderby t.Id
                                        select new
                                        {
                                            t.Id,
                                            t.Description,
                                            t.Amount,
                                            t.TransactionDate,
                                            t.CustomerName,
                                            t.BankAccountName,
                                            t.BankAccountNumber,
                                            t.TransactionType,
                                            t.RgsCode,
                                            RgsDescription = r != null ? r.RgsDescription : "",
                                            RgsShortDescription = r != null ? r.RgsShortDescription : ""
                                        })
                                        .Skip(page * pageSize)
                                        .Take(pageSize)
                                        .ToListAsync();

                if (!joinedBatch.Any())
                    break;

                using var semaphore = new SemaphoreSlim(maxParallelism);
                await Task.WhenAll(joinedBatch.Select(async row =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string textForEmbedding = $"{row.Description ?? ""} {row.Amount} {row.TransactionDate} {row.CustomerName ?? ""} {row.BankAccountName ?? ""} {row.BankAccountNumber ?? ""} {row.TransactionType ?? ""} {row.RgsCode ?? ""} {row.RgsDescription ?? ""} {row.RgsShortDescription ?? ""}";
                        var embedding = await _embeddingService.GetEmbeddingAsync(textForEmbedding);
                        // Update the embedding directly in the database
                        await UpdateEmbeddingInDatabase(connectionString, row.Id, embedding);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                page++;
            }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            return await _embeddingService.GetEmbeddingAsync(text);
        }

        public async Task<List<PersistentBankStatementLine>> GetAllPersistentBankStatementLinesWithEmbeddingsAsync()
        {
            return await _context.PersistentBankStatementLines
                .Where(x => x.Embedding != null)
                .ToListAsync();
        }

        public async Task<List<PersistentBankStatementLine>> GetPersistentBankStatementLinesWithEmbeddingsPageAsync(int page, int pageSize)
        {
            return await _context.PersistentBankStatementLines
                .Where(x => x.Embedding != null)
                .OrderBy(x => x.Id)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>> VectorSearchInSqlAsync(string connectionString, float[] queryEmbedding)
        {
            var results = new List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>();

            string sql = @"
                SELECT TOP 20
                    Id,
                    Description,
                    Amount,
                    TransactionDate,
                    RgsCode,
                    RgsDescription,
                    RgsShortDescription,
                    CAST(Embedding AS VARCHAR(MAX)) AS EmbeddingString
                FROM dbo.persistentbankstatementline
                WHERE Embedding IS NOT NULL";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var embeddingString = reader.GetString(7);
                var embedding = JsonSerializer.Deserialize<float[]>(embeddingString);
                var similarity = CalculateCosineSimilarity(queryEmbedding, embedding);

                results.Add((
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    similarity
                ));
            }

            return results.OrderByDescending(r => r.Similarity).ToList();
        }

        private float CalculateCosineSimilarity(float[] vectorA, float[] vectorB)
        {
            if (vectorA.Length != vectorB.Length)
                return 0;

            float dotProduct = 0;
            float normA = 0;
            float normB = 0;

            for (int i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                normA += vectorA[i] * vectorA[i];
                normB += vectorB[i] * vectorB[i];
            }

            if (normA == 0 || normB == 0)
                return 0;

            return dotProduct / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
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
            cmd.CommandTimeout = 20; // 20 second timeout for database operations

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

        // Helper method to update embeddings in the database
        private async Task UpdateEmbeddingInDatabase(string connectionString, Guid id, float[] embedding)
        {
            string sql = "UPDATE dbo.persistentbankstatementline SET Embedding = @Embedding WHERE Id = @Id";
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@Embedding", JsonSerializer.Serialize(embedding));
            await conn.OpenAsync();
            await cmd.ExecuteNonQueryAsync();
        }
    }
}        