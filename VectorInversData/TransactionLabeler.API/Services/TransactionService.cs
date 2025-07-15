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
        Task<List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>> IntelligentVectorSearchAsync(string connectionString, string query);
        
        Task<List<TransactionResult>> FindDuplicatePaymentsAsync(string connectionString, decimal? amount = null, DateTime? transactionDate = null, string? description = null, string? bankAccountNumber = null, DateTime? startDate = null, DateTime? endDate = null, bool salaryOnly = false);
        Task<List<TransactionResult>> FindTransactionsInDateRangeAsync(string connectionString, DateTime startDate, DateTime endDate, decimal? minAmount = null, decimal? maxAmount = null, string? description = null, string? transactionType = null);
        Task<List<TransactionResult>> FindUnusualTransactionsAsync(string connectionString, string? categoryCode = null, double standardDeviations = 2.0, int minimumTransactions = 10);
        Task<string> ProcessIntelligentQueryWithToolsAsync(string connectionString, string query);

        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesForDateRangeAsync(string connectionString, DateTime startDate, DateTime endDate, string? customerName = null, int topN = 5);
        Task<List<CategoryExpenseResult>> GetTopExpenseCategoriesFlexibleAsync(string connectionString, DateTime? startDate, DateTime? endDate, int? year, string? customerName = null, int topN = 5);
    }

    public class TransactionService : ITransactionService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmbeddingService _embeddingService;
        private readonly IServiceProvider _serviceProvider;

        public TransactionService(ApplicationDbContext context, IEmbeddingService embeddingService, IServiceProvider serviceProvider)
        {
            _context = context;
            _embeddingService = embeddingService;
            _serviceProvider = serviceProvider;
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
                        await VectorSqlHelper.InsertEmbeddingAsync(connectionString, row.Id, embedding);
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
                                        from rgs in rgsJoin.DefaultIfEmpty()
                                        where t.ContentEmbedding == null || t.AmountEmbedding == null || t.DateEmbedding == null || t.CategoryEmbedding == null || t.CombinedEmbedding == null
                                        orderby t.Id
                                        select new { Transaction = t, RgsMapping = rgs })
                                        .Skip(page * pageSize)
                                        .Take(pageSize)
                                        .ToListAsync();

                if (joinedBatch.Count == 0)
                    break;

                using var semaphore = new SemaphoreSlim(maxParallelism);
                await Task.WhenAll(joinedBatch.Select(async joinedRow =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Generate specialized embeddings for different query types with explicit RGS mapping data
                        var embeddings = await GenerateSpecializedEmbeddingsAsync(joinedRow.Transaction, joinedRow.RgsMapping);
                        await VectorSqlHelper.InsertMultipleEmbeddingsAsync(connectionString, joinedRow.Transaction.Id, embeddings);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                page++;
            }
        }

        // Generate multiple specialized embeddings using both transaction and RGS mapping
        private async Task<Dictionary<string, float[]>> GenerateSpecializedEmbeddingsAsync(InversBankTransaction transaction, RgsMapping rgs)
        {
            var embeddings = new Dictionary<string, float[]>();
            // 1. Content Embedding (for description-based queries)
            // Uses: Transaction Description + RGS Description + RGS Short Description
            string contentText = $"{transaction.Description ?? ""} {rgs?.RgsDescription ?? ""} {rgs?.RgsShortDescription ?? ""}";
            embeddings["ContentEmbedding"] = await _embeddingService.GetEmbeddingAsync(contentText);

            // 2. Amount Embedding (for amount-based queries)
            string amountText = $"amount {transaction.Amount} currency money payment transaction value financial";
            embeddings["AmountEmbedding"] = await _embeddingService.GetEmbeddingAsync(amountText);

            // 3. Date Embedding (for temporal queries)
            string dateText = transaction.TransactionDate.HasValue 
                ? $"date {transaction.TransactionDate.Value:yyyy-MM-dd} month {transaction.TransactionDate.Value:MMMM} year {transaction.TransactionDate.Value:yyyy} day {transaction.TransactionDate.Value:dd} weekday {transaction.TransactionDate.Value:dddd}"
                : "date unknown";
            embeddings["DateEmbedding"] = await _embeddingService.GetEmbeddingAsync(dateText);

            // 4. Category Embedding (for category-based queries)
            // Uses: RGS Description + RGS Short Description + Transaction Type
            string categoryText = $"category {transaction?.CategoryName} with category description {rgs?.RgsDescription ?? ""} {rgs?.RgsShortDescription ?? ""}";
            embeddings["CategoryEmbedding"] = await _embeddingService.GetEmbeddingAsync(categoryText);

            // 5. Combined Embedding (for general semantic search)
            string combinedText = $"{contentText} {amountText} {dateText} {categoryText}";
            embeddings["CombinedEmbedding"] = await _embeddingService.GetEmbeddingAsync(combinedText);

            return embeddings;
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

        public async Task<List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>> VectorSearchInSqlAsync(string connectionString, float[] queryEmbedding)
        {
            string embeddingJson = "[" + string.Join(",", queryEmbedding.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
            
            // For VECTOR columns, we can directly use VECTOR_DISTANCE without casting the column
            string sql = $@"
                SELECT TOP 5 t.Id, t.Description, t.Amount, t.TransactionDate, t.rgsCode, r.rgsDescription, r.rgsShortDescription,
                    VECTOR_DISTANCE('cosine', t.embedding, CAST('{embeddingJson}' AS VECTOR({queryEmbedding.Length}))) AS similarity
                FROM inversbanktransaction t
                LEFT JOIN rgsmapping r ON t.rgsCode = r.rgsCode
                WHERE t.embedding IS NOT NULL
                ORDER BY similarity ASC";

            var results = new List<(Guid, string?, decimal?, DateTime?, string?, string?, string?, float)>();
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    (float)reader.GetDouble(7)
                ));
            }
            return results;
        }

        // New intelligent vector search method
        public async Task<List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>> IntelligentVectorSearchAsync(string connectionString, string query)
        {
            // 1. Classify the query to determine which embedding to use
            var queryType = ClassifyQuery(query);
            
            // 2. Get embedding for the query
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
            
            // 3. Determine which embedding column to search
            string embeddingColumn = GetEmbeddingColumnForQueryType(queryType);
            
            // 4. Perform vector search with the appropriate embedding
            return await PerformSpecializedVectorSearchAsync(connectionString, queryEmbedding, embeddingColumn, queryType);
        }

        // Classify the query type based on keywords and patterns
        private QueryType ClassifyQuery(string query)
        {
            var lowerQuery = query.ToLower();
            
            // Amount-based queries
            if (lowerQuery.Contains("amount") || lowerQuery.Contains("highest") || lowerQuery.Contains("largest") || 
                lowerQuery.Contains("money") || lowerQuery.Contains("payment") || lowerQuery.Contains("value") ||
                lowerQuery.Contains("expensive") || lowerQuery.Contains("cheap") || lowerQuery.Contains("cost"))
            {
                return QueryType.Amount;
            }
            
            // Date-based queries
            if (lowerQuery.Contains("july") || lowerQuery.Contains("august") || lowerQuery.Contains("month") || 
                lowerQuery.Contains("year") || lowerQuery.Contains("date") || lowerQuery.Contains("when") ||
                lowerQuery.Contains("january") || lowerQuery.Contains("february") || lowerQuery.Contains("march") ||
                lowerQuery.Contains("april") || lowerQuery.Contains("may") || lowerQuery.Contains("june") ||
                lowerQuery.Contains("september") || lowerQuery.Contains("october") || lowerQuery.Contains("november") ||
                lowerQuery.Contains("december") || lowerQuery.Contains("week") || lowerQuery.Contains("day"))
            {
                return QueryType.Date;
            }
            
            // Category-based queries
            if (lowerQuery.Contains("category") || lowerQuery.Contains("salary") || lowerQuery.Contains("type") ||
                lowerQuery.Contains("rgs") || lowerQuery.Contains("classification") || lowerQuery.Contains("group"))
            {
                return QueryType.Category;
            }
            
            // Content-based queries (default)
            return QueryType.Content;
        }

        // Get the appropriate embedding column for the query type
        private string GetEmbeddingColumnForQueryType(QueryType queryType)
        {
            return queryType switch
            {
                QueryType.Amount => "AmountEmbedding",
                QueryType.Date => "DateEmbedding", 
                QueryType.Category => "CategoryEmbedding",
                QueryType.Content => "ContentEmbedding",
                _ => "CombinedEmbedding"
            };
        }

        // Perform specialized vector search
        private async Task<List<(Guid Id, string? Description, decimal? Amount, DateTime? TransactionDate, string? RgsCode, string? RgsDescription, string? RgsShortDescription, float Similarity)>> PerformSpecializedVectorSearchAsync(string connectionString, float[] queryEmbedding, string embeddingColumn, QueryType queryType)
        {
            string embeddingJson = "[" + string.Join(",", queryEmbedding.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
            
            // Build SQL with appropriate ordering based on query type
            string orderByClause = queryType switch
            {
                QueryType.Amount => "ORDER BY t.Amount DESC, similarity ASC", // For amount queries, prioritize by amount first
                QueryType.Date => "ORDER BY t.TransactionDate DESC, similarity ASC", // For date queries, prioritize by date first
                _ => "ORDER BY similarity ASC" // Default vector similarity ordering
            };

            // For VECTOR columns, we can directly use VECTOR_DISTANCE without casting the column
            string sql = $@"
                SELECT TOP 10 t.Id, t.Description, t.Amount, t.TransactionDate, t.rgsCode, r.rgsDescription, r.rgsShortDescription,
                    VECTOR_DISTANCE('cosine', t.{embeddingColumn}, CAST('{embeddingJson}' AS VECTOR({queryEmbedding.Length}))) AS similarity
                FROM inversbanktransaction t
                LEFT JOIN rgsmapping r ON t.rgsCode = r.rgsCode
                WHERE t.{embeddingColumn} IS NOT NULL
                {orderByClause}";

            var results = new List<(Guid, string?, decimal?, DateTime?, string?, string?, string?, float)>();
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    (float)reader.GetDouble(7)
                ));
            }
            return results;
        }

        // Query type enumeration
        public enum QueryType
        {
            Content,
            Amount,
            Date,
            Category,
            Combined
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

        // Get salary-related transaction IDs using vector similarity
        private async Task<List<Guid>> GetSalaryTransactionIdsAsync(string connectionString, DateTime? startDate = null, DateTime? endDate = null)
        {
            // Create a salary-related query embedding
            string salaryQuery = "salary payroll wage compensation employee payment remuneration";
            var salaryEmbedding = await _embeddingService.GetEmbeddingAsync(salaryQuery);
            
            var whereConditions = new List<string>();
            var parameters = new List<SqlParameter>();

            if (startDate.HasValue)
            {
                whereConditions.Add("t.TransactionDate >= @StartDate");
                parameters.Add(new SqlParameter("@StartDate", startDate.Value.Date));
            }

            if (endDate.HasValue)
            {
                whereConditions.Add("t.TransactionDate <= @EndDate");
                parameters.Add(new SqlParameter("@EndDate", endDate.Value.Date.AddDays(1).AddTicks(-1)));
            }

            string whereClause = whereConditions.Count > 0 ? "WHERE " + string.Join(" AND ", whereConditions) : "";

            string embeddingJson = "[" + string.Join(",", salaryEmbedding.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";
            
            string sql = $@"
                SELECT TOP 100 t.Id
                FROM dbo.inversbanktransaction t
                LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                {whereClause}
                WHERE t.CategoryEmbedding IS NOT NULL
                ORDER BY VECTOR_DISTANCE('cosine', t.CategoryEmbedding, CAST('{embeddingJson}' AS VECTOR({salaryEmbedding.Length}))) ASC";

            var salaryIds = new List<Guid>();
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                salaryIds.Add(reader.GetGuid(0));
            }

            return salaryIds;
        }

        public async Task<List<TransactionResult>> FindDuplicatePaymentsAsync(string connectionString, decimal? amount = null, DateTime? transactionDate = null, string? description = null, string? bankAccountNumber = null, DateTime? startDate = null, DateTime? endDate = null, bool salaryOnly = false)
        {
            // If salaryOnly is requested, first get salary-related transaction IDs using vector similarity
            List<Guid> salaryTransactionIds = null;
            if (salaryOnly)
            {
                salaryTransactionIds = await GetSalaryTransactionIdsAsync(connectionString, startDate, endDate);
                if (!salaryTransactionIds.Any())
                {
                    return new List<TransactionResult>(); // No salary transactions found
                }
            }

            var results = new List<TransactionResult>();
            var whereConditions = new List<string>();
            var parameters = new List<SqlParameter>();

            if (amount.HasValue)
            {
                whereConditions.Add("t.Amount = @Amount");
                parameters.Add(new SqlParameter("@Amount", amount.Value));
            }

            if (transactionDate.HasValue)
            {
                whereConditions.Add("CAST(t.TransactionDate AS DATE) = CAST(@TransactionDate AS DATE)");
                parameters.Add(new SqlParameter("@TransactionDate", transactionDate.Value.Date));
            }

            if (startDate.HasValue)
            {
                whereConditions.Add("t.TransactionDate >= @StartDate");
                parameters.Add(new SqlParameter("@StartDate", startDate.Value.Date));
            }

            if (endDate.HasValue)
            {
                whereConditions.Add("t.TransactionDate <= @EndDate");
                parameters.Add(new SqlParameter("@EndDate", endDate.Value.Date.AddDays(1).AddTicks(-1)));
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                whereConditions.Add("t.Description LIKE @Description");
                parameters.Add(new SqlParameter("@Description", $"%{description}%"));
            }

            if (!string.IsNullOrWhiteSpace(bankAccountNumber))
            {
                whereConditions.Add("(t.BankAccountNumber = @BankAccountNumber OR t.transactionidentifier_accountnumber = @BankAccountNumber)");
                parameters.Add(new SqlParameter("@BankAccountNumber", bankAccountNumber));
            }

            // Add salary transaction ID filtering if we have salary-specific IDs
            if (salaryTransactionIds != null && salaryTransactionIds.Any())
            {
                var idList = string.Join(",", salaryTransactionIds.Select(id => $"'{id}'"));
                whereConditions.Add($"t.Id IN ({idList})");
            }

            string whereClause = whereConditions.Count > 0 ? "WHERE " + string.Join(" AND ", whereConditions) : "";

            string sql = $@"
                WITH DuplicateTransactions AS (
                    SELECT
                        t.Id,
                        t.Amount,
                        t.TransactionDate,
                        t.Description,
                        t.BankAccountName,
                        t.BankAccountNumber,
                        t.transactionidentifier_accountnumber,
                        t.RgsCode,
                        r.RgsDescription,
                        r.RgsShortDescription,
                        ROW_NUMBER() OVER (
                            PARTITION BY t.Amount, 
                                        COALESCE(r.RgsShortDescription, ''),
                                        CAST(t.TransactionDate AS DATE)
                            ORDER BY t.Id
                        ) AS rn,
                        COUNT(*) OVER (
                            PARTITION BY t.Amount, 
                                        COALESCE(r.RgsShortDescription, ''),
                                        CAST(t.TransactionDate AS DATE)
                        ) AS duplicate_count
                    FROM dbo.inversbanktransaction t
                    LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                    {whereClause}
                )
                SELECT
                    dt.Id,
                    dt.Description,
                    dt.Amount,
                    dt.TransactionDate,
                    dt.RgsCode,
                    dt.RgsDescription,
                    dt.RgsShortDescription,
                    dt.BankAccountName,
                    dt.BankAccountNumber,
                    dt.transactionidentifier_accountnumber,
                    1.0 AS Similarity
                FROM DuplicateTransactions dt
                WHERE dt.duplicate_count > 1
                ORDER BY dt.Amount DESC, dt.TransactionDate DESC, dt.rn";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(new TransactionResult
                {
                    Id = reader.GetGuid("Id"),
                    Description = reader.IsDBNull("Description") ? null : reader.GetString("Description"),
                    Amount = reader.IsDBNull("Amount") ? null : reader.GetDecimal("Amount"),
                    TransactionDate = reader.IsDBNull("TransactionDate") ? null : reader.GetDateTime("TransactionDate"),
                    RgsCode = reader.IsDBNull("RgsCode") ? null : reader.GetString("RgsCode"),
                    RgsDescription = reader.IsDBNull("RgsDescription") ? null : reader.GetString("RgsDescription"),
                    RgsShortDescription = reader.IsDBNull("RgsShortDescription") ? null : reader.GetString("RgsShortDescription"),
                    BankAccountName = reader.IsDBNull("BankAccountName") ? null : reader.GetString("BankAccountName"),
                    BankAccountNumber = reader.IsDBNull("BankAccountNumber") ? null : reader.GetString("BankAccountNumber"),
                    TransactionIdentifierAccountNumber = reader.IsDBNull("transactionidentifier_accountnumber") ? null : reader.GetString("transactionidentifier_accountnumber"),
                    Similarity = (float)reader.GetDouble("Similarity")
                });
            }

            return results;
        }



        public async Task<List<TransactionResult>> FindTransactionsInDateRangeAsync(string connectionString, DateTime startDate, DateTime endDate, decimal? minAmount = null, decimal? maxAmount = null, string? description = null, string? transactionType = null)
        {
            var results = new List<TransactionResult>();
            var whereConditions = new List<string>
            {
                "t.TransactionDate >= @StartDate",
                "t.TransactionDate <= @EndDate"
            };
            
            var parameters = new List<SqlParameter>
            {
                new SqlParameter("@StartDate", startDate.Date),
                new SqlParameter("@EndDate", endDate.Date.AddDays(1).AddTicks(-1))
            };

            if (minAmount.HasValue)
            {
                whereConditions.Add("t.Amount >= @MinAmount");
                parameters.Add(new SqlParameter("@MinAmount", minAmount.Value));
            }

            if (maxAmount.HasValue)
            {
                whereConditions.Add("t.Amount <= @MaxAmount");
                parameters.Add(new SqlParameter("@MaxAmount", maxAmount.Value));
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                whereConditions.Add("t.Description LIKE @Description");
                parameters.Add(new SqlParameter("@Description", $"%{description}%"));
            }

            if (!string.IsNullOrWhiteSpace(transactionType))
            {
                whereConditions.Add("t.TransactionType = @TransactionType");
                parameters.Add(new SqlParameter("@TransactionType", transactionType));
            }

            string whereClause = string.Join(" AND ", whereConditions);

            string sql = $@"
                SELECT TOP 50
                    t.Id,
                    t.Description,
                    t.Amount,
                    t.TransactionDate,
                    t.RgsCode,
                    r.RgsDescription,
                    r.RgsShortDescription,
                    t.BankAccountName,
                    t.BankAccountNumber,
                    t.transactionidentifier_accountnumber,
                    1.0 AS Similarity
                FROM dbo.inversbanktransaction t
                LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                WHERE {whereClause}
                ORDER BY t.TransactionDate DESC, t.Amount DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(new TransactionResult
                {
                    Id = reader.GetGuid("Id"),
                    Description = reader.IsDBNull("Description") ? null : reader.GetString("Description"),
                    Amount = reader.IsDBNull("Amount") ? null : reader.GetDecimal("Amount"),
                    TransactionDate = reader.IsDBNull("TransactionDate") ? null : reader.GetDateTime("TransactionDate"),
                    RgsCode = reader.IsDBNull("RgsCode") ? null : reader.GetString("RgsCode"),
                    RgsDescription = reader.IsDBNull("RgsDescription") ? null : reader.GetString("RgsDescription"),
                    RgsShortDescription = reader.IsDBNull("RgsShortDescription") ? null : reader.GetString("RgsShortDescription"),
                    BankAccountName = reader.IsDBNull("BankAccountName") ? null : reader.GetString("BankAccountName"),
                    BankAccountNumber = reader.IsDBNull("BankAccountNumber") ? null : reader.GetString("BankAccountNumber"),
                    TransactionIdentifierAccountNumber = reader.IsDBNull("transactionidentifier_accountnumber") ? null : reader.GetString("transactionidentifier_accountnumber"),
                    Similarity = (float)reader.GetDouble("Similarity")
                });
            }

            return results;
        }

        public async Task<List<TransactionResult>> FindUnusualTransactionsAsync(string connectionString, string? categoryCode = null, double standardDeviations = 2.0, int minimumTransactions = 10)
        {
            var results = new List<TransactionResult>();
            var whereConditions = new List<string>();
            var parameters = new List<SqlParameter>
            {
                new SqlParameter("@StandardDeviations", standardDeviations),
                new SqlParameter("@MinimumTransactions", minimumTransactions)
            };

            if (!string.IsNullOrWhiteSpace(categoryCode))
            {
                whereConditions.Add("t.RgsCode = @CategoryCode");
                parameters.Add(new SqlParameter("@CategoryCode", categoryCode));
            }

            string whereClause = whereConditions.Count > 0 ? "WHERE " + string.Join(" AND ", whereConditions) : "";

            string sql = $@"
                WITH TransactionStats AS (
                    SELECT 
                        AVG(CAST(t.Amount AS FLOAT)) AS mean_amount,
                        STDEV(CAST(t.Amount AS FLOAT)) AS stdev_amount,
                        COUNT(*) AS transaction_count
                    FROM dbo.inversbanktransaction t
                    {whereClause}
                    AND t.Amount IS NOT NULL
                ),
                OutlierTransactions AS (
                    SELECT 
                        t.Id,
                        t.Description,
                        t.Amount,
                        t.TransactionDate,
                        t.RgsCode,
                        r.RgsDescription,
                        r.RgsShortDescription,
                        t.BankAccountName,
                        t.BankAccountNumber,
                        t.transactionidentifier_accountnumber,
                        ABS(CAST(t.Amount AS FLOAT) - ts.mean_amount) / NULLIF(ts.stdev_amount, 0) AS z_score,
                        1.0 AS Similarity
                    FROM dbo.inversbanktransaction t
                    LEFT JOIN dbo.rgsmapping r ON t.RgsCode = r.RgsCode
                    CROSS JOIN TransactionStats ts
                    {whereClause}
                    AND t.Amount IS NOT NULL
                    AND ts.transaction_count >= @MinimumTransactions
                    AND ts.stdev_amount > 0
                )
                SELECT TOP 20
                    Id, Description, Amount, TransactionDate, RgsCode, RgsDescription, RgsShortDescription,
                    BankAccountName, BankAccountNumber, transactionidentifier_accountnumber, Similarity
                FROM OutlierTransactions
                WHERE z_score > @StandardDeviations
                ORDER BY z_score DESC";

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(new TransactionResult
                {
                    Id = reader.GetGuid("Id"),
                    Description = reader.IsDBNull("Description") ? null : reader.GetString("Description"),
                    Amount = reader.IsDBNull("Amount") ? null : reader.GetDecimal("Amount"),
                    TransactionDate = reader.IsDBNull("TransactionDate") ? null : reader.GetDateTime("TransactionDate"),
                    RgsCode = reader.IsDBNull("RgsCode") ? null : reader.GetString("RgsCode"),
                    RgsDescription = reader.IsDBNull("RgsDescription") ? null : reader.GetString("RgsDescription"),
                    RgsShortDescription = reader.IsDBNull("RgsShortDescription") ? null : reader.GetString("RgsShortDescription"),
                    BankAccountName = reader.IsDBNull("BankAccountName") ? null : reader.GetString("BankAccountName"),
                    BankAccountNumber = reader.IsDBNull("BankAccountNumber") ? null : reader.GetString("BankAccountNumber"),
                    TransactionIdentifierAccountNumber = reader.IsDBNull("transactionidentifier_accountnumber") ? null : reader.GetString("transactionidentifier_accountnumber"),
                    Similarity = (float)reader.GetDouble("Similarity")
                });
            }

            return results;
        }

        public async Task<string> ProcessIntelligentQueryWithToolsAsync(string connectionString, string query)
        {
            try
            {
                var financialTools = new FinancialTools(this, connectionString);
                
                // Create a ChatClient with function invocation enabled
                var chatClient = _serviceProvider.GetRequiredService<IChatClient>();
                
                // Create chat options with the available functions using AIFunctionFactory
                var chatOptions = new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.Create(
                            (decimal? amount, DateTime? transactionDate, string? description, string? bankAccountNumber, DateTime? startDate, DateTime? endDate, bool salaryOnly) =>
                                financialTools.FindDuplicatePayments(amount, transactionDate, description, bankAccountNumber, startDate, endDate, salaryOnly),
                            "FindDuplicatePayments",
                            "Finds potential duplicate bank transactions. Can be used for specific amount/date duplicates or salary-specific duplicates within date ranges. For salary duplicates, use salaryOnly=true with startDate/endDate. For specific duplicates, use amount and transactionDate. Useful for detecting double payments, duplicate invoices, or erroneous transactions. Use this for queries like 'Find double salary payments in January' or 'Check for duplicate payments of $1000 on March 15th'."
                        ),
                        AIFunctionFactory.Create(
                            (DateTime startDate, DateTime endDate, decimal? minAmount, decimal? maxAmount, string? description, string? transactionType) =>
                                financialTools.FindTransactionsInDateRange(startDate, endDate, minAmount, maxAmount, description, transactionType),
                            "FindTransactionsInDateRange",
                            "Finds transactions within a specific date range with optional filtering by amount, description, or transaction type. Useful for period analysis and spending pattern identification."
                        ),
                        AIFunctionFactory.Create(
                            (string? categoryCode, double standardDeviations, int minimumTransactions) =>
                                financialTools.FindUnusualTransactions(categoryCode, standardDeviations, minimumTransactions),
                            "FindUnusualTransactions",
                            "Identifies unusual or outlier transactions based on statistical analysis. Useful for fraud detection and identifying data entry errors."
                        ),
                        AIFunctionFactory.Create(
                            (DateTime? startDate, DateTime? endDate, int? year, string? customerName, int topN) => financialTools.GetTopExpenseCategoriesFlexible(startDate, endDate, year, customerName, topN),
                            "GetTopExpenseCategories",
                            "Returns the top N expense categories for a given date range, month, quarter, or year, optionally filtered by customer name, grouped by RgsDescription. Only expense transactions (AfBij = 'Af') are included. Use this tool for queries like 'What are my top 3 expenses for January 2024?', 'Show my biggest spending categories in Q2 2023', 'Top 5 expense categories in 2023', 'Top 4 categories for first half of 2024', 'Top expenses for Company ABC', 'Top 3 categories for customer XYZ in 2024'. If the user does not specify a date range or year, use the current year. If the user does not specify a number, use 5. If the user mentions a specific company or customer, extract the customer name. The result is suitable for further aggregation or summarization by an LLM if needed."
                        )
                    ]
                };
                
                // Create chat history with system prompt
                var chatHistory = new List<ChatMessage>
                {
                    new(ChatRole.System, @"
                        You are a financial analysis assistant. You can help users analyze transaction data using the available tools.
                        
                        CRITICAL RULE: Use the GetTopExpenseCategories tool for any query about top expenses, biggest spending categories, or top N expense categories for a year, month, quarter, custom date range, or specific customer.
                        
                        Extract the correct parameters from the user's query:
                        
                        Date ranges:
                        - 'first half of 2024' => startDate=2024-01-01, endDate=2024-06-30
                        - 'Q1 2023' => startDate=2023-01-01, endDate=2023-03-31
                        - 'March 2022' => startDate=2022-03-01, endDate=2022-03-31
                        - '2024' => year=2024
                        - If no date is specified, use the current year
                        
                        Customer filtering:
                        - 'Top expenses for Company ABC' => customerName='Company ABC'
                        - 'Top 3 categories for customer XYZ' => customerName='XYZ', topN=3
                        - 'Top expenses for ABC Corp in 2024' => customerName='ABC Corp', year=2024
                        
                        Always extract and use the topN parameter if the user specifies a number (e.g., 'top 3', 'top 5').
                        
                        CUSTOMER CLARIFICATION HANDLING:
                        If the tool returns a result with RgsDescription='CUSTOMER_CLARIFICATION_NEEDED', this means the customer name was not found exactly but similar names were found. The RgsCode field will contain the similar customer names separated by '|'.
                        
                        IMPORTANT: You MUST extract the actual customer names from the RgsCode field and list them exactly as they appear. Do NOT make up or invent company names.
                        
                        In this case, respond with:
                        'I found several companies with similar names to the one you mentioned. Please specify which one you meant:
                        • [Extract and list the actual customer names from RgsCode, separated by '|']
                        
                        Please provide the exact company name you want to analyze.'
                        
                        Example: If RgsCode contains ""Nova Creations|Golven Mobiliteit B.V."", then list:
                        • Nova Creations
                        • Golven Mobiliteit B.V.
                        
                        After making a function call and receiving the result, you MUST provide a final text response and STOP. Do not make any more function calls.
                        
                        Example response format:
                        'Here are your top 4 expenses for the first half of 2024:
                        • [Category] (RgsCode: [RgsCode]): €[Amount]
                        • [Category] (RgsCode: [RgsCode]): €[Amount]
                        • [Category] (RgsCode: [RgsCode]): €[Amount]
                        • [Category] (RgsCode: [RgsCode]): €[Amount]
                        
                        Total spending in these categories: €[Total]'
                        
                        Remember: Only ONE function call per conversation, then provide a final summary."),
                    new(ChatRole.User, query)
                };
                
                int maxIterations = 5; // Prevent infinite loops
                int iteration = 0;
                
                while (iteration < maxIterations)
                {
                    iteration++;
                    Console.WriteLine($"Function call iteration {iteration}");
                    
                    var response = await chatClient.CompleteAsync(chatHistory, chatOptions);
                    Console.WriteLine("LLM Raw Response: " + System.Text.Json.JsonSerializer.Serialize(response));
                    
                    // Check if we have a final text response
                    if (!string.IsNullOrWhiteSpace(response.Message.Text))
                    {
                        Console.WriteLine($"Final response generated after {iteration} iterations");
                        return response.Message.Text;
                    }

                    // Try to find a function call in the response
                    var functionCallContent = response.Choices
                        .SelectMany(choice => choice.Contents)
                        .FirstOrDefault(content => content?.GetType().Name.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase) == true);

                    if (functionCallContent != null)
                    {
                        // Use dynamic to access Name and Arguments
                        dynamic dynCall = functionCallContent;
                        string functionName = dynCall.Name;
                        object arguments = dynCall.Arguments;
                        
                        Console.WriteLine($"Executing function: {functionName}");
                        
                        // Find the tool by name using Metadata.Name (case-insensitive)
                        var tool = chatOptions.Tools.FirstOrDefault(t =>
                        {
                            var metadataProp = t.GetType().GetProperty("Metadata");
                            var metadata = metadataProp?.GetValue(t);
                            var nameProp = metadata?.GetType().GetProperty("Name");
                            var toolName = nameProp?.GetValue(metadata)?.ToString();
                            return string.Equals(toolName, functionName, StringComparison.OrdinalIgnoreCase);
                        });
                        
                        if (tool == null)
                            return $"Tool '{functionName}' not found.";
                        
                        // Try to invoke the tool
                        object toolResult;
                        var invokeAsync = tool.GetType().GetMethod("InvokeAsync");
                        if (invokeAsync != null)
                        {
                            // The AIFunctionFactory creates a wrapper that takes arguments as IEnumerable<KeyValuePair<string, object>>
                            if (arguments is IReadOnlyDictionary<string, object> dict)
                            {
                                // Convert the dictionary to the expected format
                                var argumentsEnumerable = dict.Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value));
                                var paramValues = new object[] { argumentsEnumerable, CancellationToken.None };
                                toolResult = await (Task<object>)invokeAsync.Invoke(tool, paramValues);
                            }
                            else
                            {
                                var paramValues = new object[] { arguments, CancellationToken.None };
                                toolResult = await (Task<object>)invokeAsync.Invoke(tool, paramValues);
                            }
                        }
                        else
                        {
                            var invoke = tool.GetType().GetMethod("Invoke");
                            
                            // Handle the same pattern for synchronous invoke
                            if (arguments is IReadOnlyDictionary<string, object> dict)
                            {
                                var argumentsEnumerable = dict.Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value));
                                var paramValues = new object[] { argumentsEnumerable };
                                toolResult = invoke.Invoke(tool, paramValues);
                            }
                            else
                            {
                                var paramValues = new object[] { arguments };
                                toolResult = invoke.Invoke(tool, paramValues);
                            }
                        }
                        
                        Console.WriteLine($"Tool result: {toolResult}");
                        
                        // Log the actual result structure for debugging
                        if (toolResult is List<CategoryExpenseResult> expenseResults && expenseResults.Any())
                        {
                            var firstResult = expenseResults.First();
                            Console.WriteLine($"First result - RgsDescription: '{firstResult.RgsDescription}', RgsCode: '{firstResult.RgsCode}'");
                        }
                        
                        // Add the tool result to the chat history as an assistant message with clear formatting
                        string formattedResult = $"Function {functionName} returned the following result:\n\n{toolResult}";
                        chatHistory.Add(new ChatMessage(ChatRole.Assistant, formattedResult));
                        
                        // Add a user message to prompt for final response
                        chatHistory.Add(new ChatMessage(ChatRole.User, "Please provide a clear summary of this data for the user."));
                        
                        // Continue the loop to let the LLM process the tool result
                        continue;
                    }
                    
                    // If we get here, there's no function call and no text response
                    Console.WriteLine("No function call or text response found");
                    break;
                }
                
                if (iteration >= maxIterations)
                {
                    return $"Maximum iterations ({maxIterations}) reached. The LLM kept making function calls without providing a final response.";
                }
                
                return "No response generated.";
            }
            catch (Exception ex)
            {
                return $"Error processing query: {ex.Message}. Please try a simpler query or use the vector search instead.";
            }
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

            // If customer name is provided, first check for exact match, then fuzzy match
            if (!string.IsNullOrWhiteSpace(customerName))
            {
                var similarCustomers = await FindSimilarCustomerNamesAsync(connectionString, customerName, rangeStart, rangeEnd);
                
                // If no exact match but we found similar names, return a special result indicating this
                if (!similarCustomers.Any(c => string.Equals(c, customerName, StringComparison.OrdinalIgnoreCase)) && similarCustomers.Any())
                {
                    // Log the similar customers found for debugging
                    Console.WriteLine($"Customer '{customerName}' not found exactly. Similar customers found: {string.Join(", ", similarCustomers)}");
                    
                    // Return a special result that indicates we need customer clarification
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

            return await GetTopExpenseCategoriesForDateRangeAsync(connectionString, rangeStart, rangeEnd, customerName, topN);
        }

        // New method to find similar customer names
        private static async Task<List<string>> FindSimilarCustomerNamesAsync(string connectionString, string searchCustomerName, DateTime startDate, DateTime endDate)
        {
            var results = new List<string>();
            
            // Use SQL Server's SOUNDEX and similarity functions for fuzzy matching
            string sql = @"
                SELECT TOP 10
                    t.CustomerName
                FROM dbo.inversbanktransaction t
                WHERE t.TransactionDate >= @StartDate
                    AND t.TransactionDate < @EndDate
                    AND t.CustomerName IS NOT NULL
                    AND t.CustomerName != ''
                    AND (
                        t.CustomerName LIKE @ExactMatch
                        OR t.CustomerName LIKE @StartsWith
                        OR SOUNDEX(t.CustomerName) = SOUNDEX(@SearchName)
                        OR t.CustomerName LIKE @Contains
                    )
                GROUP BY t.CustomerName
                ORDER BY 
                    CASE 
                        WHEN t.CustomerName LIKE @ExactMatch THEN 100
                        WHEN t.CustomerName LIKE @StartsWith THEN 80
                        WHEN SOUNDEX(t.CustomerName) = SOUNDEX(@SearchName) THEN 60
                        WHEN t.CustomerName LIKE @Contains THEN 40
                        ELSE 0
                    END DESC,
                    COUNT(*) DESC";

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
    }

    // Helper class for native VECTOR operations using ADO.NET
    public static class VectorSqlHelper
    {
        public static async Task InsertEmbeddingAsync(string connectionString, Guid id, float[] embedding)
        {
            // Build the embedding as a JSON array string with dot decimal separator
            string embeddingJson = "[" + string.Join(",", embedding.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";
            // Escape single quotes (shouldn't be present, but for safety)
            string safeEmbeddingJson = embeddingJson.Replace("'", "''");
            // Wrap in single quotes for SQL literal
            string sql = $"UPDATE dbo.inversbanktransaction SET embedding = '{safeEmbeddingJson}' WHERE id = @id";
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            await conn.OpenAsync();
            await cmd.ExecuteNonQueryAsync();
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

        // Method to insert a single VECTOR embedding
        public static async Task InsertVectorEmbeddingAsync(string connectionString, Guid id, string columnName, float[] embedding)
        {
            string embeddingJson = "[" + string.Join(",", embedding.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";
            string safeEmbeddingJson = embeddingJson.Replace("'", "''");
            
            // Use CAST to convert JSON array to VECTOR type
            string sql = $"UPDATE dbo.inversbanktransaction SET {columnName} = CAST('{safeEmbeddingJson}' AS VECTOR({embedding.Length})) WHERE id = @id";
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            await conn.OpenAsync();
            await cmd.ExecuteNonQueryAsync();
        }
    }
}        