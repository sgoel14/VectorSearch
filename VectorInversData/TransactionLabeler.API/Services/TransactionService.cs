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
        

        
        // New category-based transaction search methods
        Task<List<CategorySearchResult>> SearchCategoriesByVectorAsync(string connectionString, string categoryQuery, int topCategories = 5, string? customerName = null);
        Task<List<TransactionResult>> GetTopTransactionsForCategoriesAsync(string connectionString, List<string> rgsCodes, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null);
        Task<List<TransactionResult>> GetTopTransactionsForCategoryQueryAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null, int topCategories = 3);
        
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
            List<Guid>? salaryTransactionIds = null;
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
                            (decimal? amount, object? transactionDate, string? description, string? bankAccountNumber, object? startDate, object? endDate, bool salaryOnly) =>
                                financialTools.FindDuplicatePayments(amount, transactionDate, description, bankAccountNumber, startDate, endDate, salaryOnly),
                            "FindDuplicatePayments",
                            "Finds potential duplicate bank transactions. Can be used for specific amount/date duplicates or salary-specific duplicates within date ranges. For salary duplicates, use salaryOnly=true with startDate/endDate. For specific duplicates, use amount and transactionDate. Useful for detecting double payments, duplicate invoices, or erroneous transactions. Use this for queries like 'Find double salary payments in January' or 'Check for duplicate payments of $1000 on March 15th'."
                        ),
                        AIFunctionFactory.Create(
                            (object startDate, object endDate, decimal? minAmount, decimal? maxAmount, string? description, string? transactionType) =>
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
                            (object? startDate, object? endDate, int? year, string? customerName, int topN) => financialTools.GetTopExpenseCategoriesFlexible(startDate, endDate, year, customerName, topN),
                            "GetTopExpenseCategories",
                            "Returns the top N expense categories for a given date range, month, quarter, or year, optionally filtered by customer name, grouped by RgsDescription. Only expense transactions (AfBij = 'Af') are included. Use this tool for queries like 'What are my top 3 expenses for January 2024?', 'Show my biggest spending categories in Q2 2023', 'Top 5 expense categories in 2023', 'Top 4 categories for first half of 2024', 'Top expenses for Company ABC', 'Top 3 categories for customer XYZ in 2024'. If the user does not specify a date range or year, use the current year. If the user does not specify a number, use 5. If the user mentions a specific company or customer, extract the customer name. The result is suitable for further aggregation or summarization by an LLM if needed."
                        ),
                        AIFunctionFactory.Create(
                            (string categoryQuery, object? startDate, object? endDate, int? year, int topN, string? customerName, int topCategories) => financialTools.GetTopTransactionsForCategory(categoryQuery, startDate, endDate, year, topN, customerName, topCategories),
                            "GetTopTransactionsForCategory",
                            "Finds top transactions for ANY specific category using vector search. First searches for relevant categories using semantic similarity, then returns the top transactions for those categories. Use for queries like 'Top 10 transactions for marketing costs', 'Show me office expenditure expenses', 'Top 5 transactions for tax related costs', 'Show me travel expenses', 'Show me restaurant expenses', 'Show me utility expenses', 'Show me advertising costs', 'Show me legal expenses', 'Show me insurance costs', etc. Extract the category type from the user's query. If no number specified, use 10. Only include year parameter if explicitly mentioned in the query."
                        ),
                        AIFunctionFactory.Create(
                            (string categoryQuery, int topCategories, string? customerName) => financialTools.SearchCategories(categoryQuery, topCategories, customerName),
                            "SearchCategories",
                            "Searches for relevant categories using vector similarity. Returns categories that match the semantic meaning of the query. Use this tool when the user wants to understand what categories are available or to debug category searches. Can be filtered by customer name. Examples: 'travel categories', 'food categories', 'office expense categories', 'travel categories for Company ABC'."
                        )
                    ]
                };

                // Create chat history with system prompt
                var chatHistory = new List<ChatMessage>
                {
                    new(ChatRole.System, @"
                        You are a financial analysis assistant. You can help users analyze transaction data using the available tools.
                        
                        SEMANTIC UNDERSTANDING RULES:
                        - Understand that ANY query asking for transactions, transaction lists, or transaction details should use GetTopTransactionsForCategory
                        - If the user mentions a category (like 'staff drink and food', 'car repair', 'marketing', etc.) and wants transactions, use GetTopTransactionsForCategory
                        - If the user asks for 'top N transactions', 'list of transactions', 'show me transactions', 'transactions for [category]', use GetTopTransactionsForCategory
                        - If the user asks for expense categories or spending summaries, use GetTopExpenseCategories
                        - If the user wants to explore available categories, use SearchCategories
                        
                        TOOL SELECTION RULES:
                        1. Use GetTopExpenseCategories for queries about 'top expenses', 'biggest spending categories', 'top N expense categories', 'expense categories for year/month/quarter', 'spending summary'
                        2. Use GetTopTransactionsForCategory for ANY query that semantically relates to getting transaction details, including:
                           - 'show me top 10 transactions with category staff drink and food'
                           - 'can you show me top 10 transactions for category staff drink and food'
                           - 'transactions for staff drink and food'
                           - 'show me staff drink and food transactions'
                           - 'list transactions for [category]'
                           - 'top N transactions for [any category]'
                           - 'transactions for [any category]'
                           - 'show me [category] transactions'
                           - 'get transactions for [category]'
                           - 'find transactions for [category]'
                           - 'display transactions for [category]'
                           - 'retrieve transactions for [category]'
                        3. Use SearchCategories for queries about 'what categories are available', 'list all categories', 'show me categories', 'explore categories'
                        
                        PARAMETER EXTRACTION:
                        - Extract customer names: 'for customer X', 'customer X', 'for X', 'Nova Creations', etc.
                        - Extract numbers for topN: 'top 5', 'top 10', '5 transactions', '10 transactions', 'top 10 transactions'
                        - Extract dates: '2024', '2025', 'Q1 2024', 'January 2024', 'first half of 2024', 'year 2025'
                        - Extract categories: 'staff drink and food', 'car repair', 'marketing', 'travel', 'office expenses', etc.
                        - For category extraction, be flexible: 'category staff drink and food' → 'staff drink and food', 'transactions for marketing' → 'marketing'
                        
                        EXAMPLES:
                        - 'can you show me top 10 transactions with category staff drink and food for customer Nova Creations for year 2025' → GetTopTransactionsForCategory(categoryQuery='staff drink and food', topN=10, customerName='Nova Creations', year=2025, topCategories=3)
                        - 'can you show me top 10 transactions for category staff drink and food for customer Nova Creations for year 2025' → GetTopTransactionsForCategory(categoryQuery='staff drink and food', topN=10, customerName='Nova Creations', year=2025, topCategories=3)
                        - 'can you show transactions for category staff drink and food' → GetTopTransactionsForCategory(categoryQuery='staff drink and food', topN=10, topCategories=3)
                        - 'can you show me staff drink and food transactions for year 2025' → GetTopTransactionsForCategory(categoryQuery='staff drink and food', topN=10, year=2025, topCategories=3)
                        - 'can you show me staff drink and food transactions for year 2025 for Nova Creations' → GetTopTransactionsForCategory(categoryQuery='staff drink and food', topN=10, customerName='Nova Creations', year=2025, topCategories=3)
                        - 'list transactions for marketing' → GetTopTransactionsForCategory(categoryQuery='marketing', topN=10, topCategories=3)
                        - 'get transactions for travel expenses' → GetTopTransactionsForCategory(categoryQuery='travel expenses', topN=10, topCategories=3)
                        - 'find transactions for office supplies' → GetTopTransactionsForCategory(categoryQuery='office supplies', topN=10, topCategories=3)
                        - 'Top 5 transactions for car repair costs for customer Nova Creations' → GetTopTransactionsForCategory(categoryQuery='car repair', topN=5, customerName='Nova Creations', topCategories=3)
                        - 'Show me marketing expenses' → GetTopTransactionsForCategory(categoryQuery='marketing', topN=10, topCategories=3)
                        - 'Top expenses for 2024' → GetTopExpenseCategories(year=2024, topN=5)
                        - 'What categories are available?' → SearchCategories(categoryQuery='', topCategories=50)
                        - 'Search all categories for Nova Creations' → SearchCategories(categoryQuery='all', topCategories=50, customerName='Nova Creations')
                        
                        PARAMETER RULES:
                        - Always include topCategories=3 for GetTopTransactionsForCategory calls
                        - Always include topN parameter for transaction queries (default to 10 if not specified)
                        - Always include customerName when specified in the query
                        - Only include year parameter if explicitly mentioned in the query (e.g., 'in 2024', 'for 2023', 'this year', 'year 2025')
                        - Do NOT automatically add year filters unless the user specifically asks for a particular year
                        - For category queries, extract the category name from phrases like 'category staff drink and food' → 'staff drink and food'
                        
                        RESPONSE FORMAT:
                        - For SearchCategories results: Show the actual RGS codes and descriptions in a clear list format. Do not apologize or say there was an issue.
                        - For GetTopExpenseCategories results: Show the actual category names and amounts
                        - For GetTopTransactionsForCategory results: Show the actual transaction details including description, amount, date, and RGS code
                        - For other results: Provide a clear summary of the results
                        - If no results found, explain that no transactions match the criteria
                        
                        CRITICAL RULES:
                        - Make only ONE function call per query, then provide a final response with the actual data
                        - Never apologize or say there was an issue - just show the data
                        - For category searches, always show the RGS codes and descriptions clearly
                        - For transaction searches, always show the actual transaction details
                        - If you receive transaction data, format it clearly showing: Description, Amount, Date, RGS Code, and Category
                        - DO NOT make multiple function calls when you already have results - provide the final response immediately
                        - NEVER say ""no transactions found"" if the function returned actual transaction data
                        - ALWAYS show the actual transaction details when they are provided by the function
                        - If the function returns an array of transactions, display each transaction with full details
                        - Understand semantic intent: if someone asks for transactions (in any form), use GetTopTransactionsForCategory
                        - Be intelligent about natural language: recognize that 'list transactions', 'get transactions', 'find transactions', 'show transactions' all mean the same thing
                        - IMPORTANT: When you receive category data from SearchCategories, display the actual categories that were returned. Do not say ""no transactions found"" when categories were successfully returned.
                        - IMPORTANT: When you receive transaction data from GetTopTransactionsForCategory, display the actual transactions that were returned. Do not say ""no transactions found"" when transactions were successfully returned.
                        - IMPORTANT: When you receive expense data from GetTopExpenseCategories, display the actual expense categories that were returned. Do not say ""no transactions found"" when expense data was successfully returned.
                        "),
                    new(ChatRole.User, query)
                };

                int maxIterations = 5; // Prevent infinite loops
                int iteration = 0;

                while (iteration < maxIterations)
                {
                    iteration++;
                    Console.WriteLine($"Function call iteration {iteration}");

                    // Add timeout handling
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // 30 second timeout
                    var response = await chatClient.CompleteAsync(chatHistory, chatOptions, cts.Token);
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
                        Console.WriteLine($"Function arguments: {JsonSerializer.Serialize(arguments)}");

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
                        {
                            Console.WriteLine($"Tool '{functionName}' not found. Available tools: {string.Join(", ", chatOptions.Tools.Select(t =>
                            {
                                var metadataProp = t.GetType().GetProperty("Metadata");
                                var metadata = metadataProp?.GetValue(t);
                                var nameProp = metadata?.GetType().GetProperty("Name");
                                return nameProp?.GetValue(metadata)?.ToString() ?? "unknown";
                            }))}");
                            return $"Tool '{functionName}' not found.";
                        }

                        // Try to invoke the tool
                        object toolResult;
                        try
                        {
                            // Add timeout for tool execution
                            using var toolCts = new CancellationTokenSource(TimeSpan.FromSeconds(25)); // 25 second timeout for tool execution
                            
                            var invokeAsync = tool.GetType().GetMethod("InvokeAsync");
                            if (invokeAsync != null)
                            {
                                // The AIFunctionFactory creates a wrapper that takes arguments as IEnumerable<KeyValuePair<string, object>>
                                if (arguments is IReadOnlyDictionary<string, object> dict)
                                {
                                    // Convert the dictionary to the expected format
                                    var argumentsEnumerable = dict.Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value));
                                    var paramValues = new object[] { argumentsEnumerable, toolCts.Token };
                                    toolResult = await (Task<object>)invokeAsync.Invoke(tool, paramValues);
                                }
                                else
                                {
                                    var paramValues = new object[] { arguments, toolCts.Token };
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

                            Console.WriteLine($"Tool result type: {toolResult?.GetType().Name}");
                            Console.WriteLine($"Tool result count: {(toolResult is IEnumerable<object> enumerable ? enumerable.Count() : "N/A")}");
                            Console.WriteLine($"Tool result: {JsonSerializer.Serialize(toolResult)}");

                            // Additional logging for debugging
                            if (toolResult is List<CategorySearchResult> categorySearchResults)
                            {
                                Console.WriteLine($"Category search returned {categorySearchResults.Count} results");
                                if (categorySearchResults.Any())
                                {
                                    Console.WriteLine($"First category: {categorySearchResults.First().RgsCode} - {categorySearchResults.First().RgsDescription}");
                                }
                            }

                            // Log the actual result structure for debugging
                            if (toolResult is List<CategoryExpenseResult> expenseResults && expenseResults.Any())
                            {
                                var firstResult = expenseResults.First();
                                Console.WriteLine($"First result - RgsDescription: '{firstResult.RgsDescription}', RgsCode: '{firstResult.RgsCode}'");
                            }
                            else if (toolResult is List<TransactionResult> transactionResults && transactionResults.Any())
                            {
                                var firstResult = transactionResults.First();
                                Console.WriteLine($"First transaction - Description: '{firstResult.Description}', Amount: '{firstResult.Amount}', RgsCode: '{firstResult.RgsCode}'");
                            }
                            else if (toolResult is List<CategorySearchResult> categoryResults && categoryResults.Any())
                            {
                                var firstResult = categoryResults.First();
                                Console.WriteLine($"First category - RgsDescription: '{firstResult.RgsDescription}', RgsCode: '{firstResult.RgsCode}', Similarity: '{firstResult.Similarity}'");
                            }

                            // Add the tool result to the chat history as an assistant message with clear formatting
                            string formattedResult = FormatToolResult(functionName, toolResult);
                            chatHistory.Add(new ChatMessage(ChatRole.Assistant, formattedResult));
                            
                            // Check if we have results - if so, prompt for final response immediately
                            bool hasResults = false;
                            if (toolResult is List<TransactionResult> transactionResultsCheck && transactionResultsCheck.Any())
                            {
                                hasResults = true;
                                Console.WriteLine($"Detected {transactionResultsCheck.Count} transaction results in List<TransactionResult>");
                            }
                            else if (toolResult is List<CategorySearchResult> categoryResultsCheck && categoryResultsCheck.Any())
                            {
                                hasResults = true;
                                Console.WriteLine($"Detected {categoryResultsCheck.Count} category results in List<CategorySearchResult>");
                            }
                            else if (toolResult is List<CategoryExpenseResult> expenseResultsCheck && expenseResultsCheck.Any())
                            {
                                hasResults = true;
                                Console.WriteLine($"Detected {expenseResultsCheck.Count} expense results in List<CategoryExpenseResult>");
                            }
                            else if (toolResult.GetType().Name == "JsonElement")
                            {
                                // Check if JsonElement contains transaction data
                                var jsonElement = (System.Text.Json.JsonElement)toolResult;
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array && jsonElement.GetArrayLength() > 0)
                                {
                                    hasResults = true;
                                    Console.WriteLine($"Detected {jsonElement.GetArrayLength()} results in JsonElement array");
                                }
                            }
                            
                            Console.WriteLine($"hasResults = {hasResults}");
                            
                            if (hasResults)
                            {
                                // We have results, prompt for final response
                                if (functionName == "SearchCategories")
                                {
                                    chatHistory.Add(new ChatMessage(ChatRole.User, "You have the category data. Provide a final response showing the actual RGS codes and descriptions that were returned. Do not make any more function calls - just show the category data."));
                                }
                                else if (functionName == "GetTopTransactionsForCategory")
                                {
                                    chatHistory.Add(new ChatMessage(ChatRole.User, "You have the transaction data. Provide a final response showing the actual transaction details including description, amount, date, RGS code, and category. Do not make any more function calls - just show the transaction data."));
                                }
                                else if (functionName == "GetTopExpenseCategories")
                                {
                                    chatHistory.Add(new ChatMessage(ChatRole.User, "You have the expense category data. Provide a final response showing the actual category names and amounts that were returned. Do not make any more function calls - just show the expense data."));
                                }
                                else
                                {
                                    chatHistory.Add(new ChatMessage(ChatRole.User, "You have the data. Provide a final response showing the actual details that were returned. Do not make any more function calls - just show the data."));
                                }
                            }
                            else
                            {
                                // No results found, allow another attempt
                                chatHistory.Add(new ChatMessage(ChatRole.User, "No results found. Please try a different approach or provide a helpful response explaining that no data matches the criteria."));
                            }
                            
                            // Continue the loop to let the LLM process the tool result
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error invoking tool {functionName}: {ex.Message}");
                            
                            // Check if it's a timeout error
                            if (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || 
                                ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("Execution Timeout Expired", StringComparison.OrdinalIgnoreCase))
                            {
                                return $"The operation timed out while executing {functionName}. This might be due to a complex query or database performance issues. Please try a simpler query or specify a customer name if not provided.";
                            }
                            
                            return $"Error executing {functionName}: {ex.Message}. Please try a simpler query.";
                        }

                        // If we get here, there's no function call and no text response
                        Console.WriteLine("No function call or text response found");
                        break;
                    }
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

        // Helper method to format tool results for better readability
        private string FormatToolResult(string functionName, object toolResult)
        {
            Console.WriteLine($"FormatToolResult called with functionName='{functionName}', toolResult type='{toolResult?.GetType().Name}'");
            
            if (toolResult is List<CategorySearchResult> categoryResults)
            {
                if (!categoryResults.Any())
                {
                    return $"Function {functionName} returned no categories. No categories found matching the criteria.";
                }
                
                var formatted = $"Function {functionName} returned {categoryResults.Count} categories:\n\n";
                foreach (var category in categoryResults)
                {
                    formatted += $"• RGS Code: {category.RgsCode}\n";
                    formatted += $"  Description: {category.RgsDescription}\n";
                    formatted += $"  Similarity: {category.Similarity:F3}\n\n";
                }
                return formatted;
            }
            else if (toolResult is List<CategoryExpenseResult> expenseResults)
            {
                var formatted = $"Function {functionName} returned {expenseResults.Count} expense categories:\n\n";
                foreach (var expense in expenseResults)
                {
                    formatted += $"• RGS Code: {expense.RgsCode}\n";
                    formatted += $"  Description: {expense.RgsDescription}\n";
                    formatted += $"  Total Amount: {expense.TotalExpense:C}\n\n";
                }
                return formatted;
            }
            else if (toolResult is List<TransactionResult> transactionResults)
            {
                if (!transactionResults.Any())
                {
                    return $"Function {functionName} returned no transactions. No transactions found matching the criteria.";
                }
                
                var formatted = $"Function {functionName} returned {transactionResults.Count} transactions:\n\n";
                foreach (var transaction in transactionResults)
                {
                    formatted += $"• Description: {transaction.Description}\n";
                    formatted += $"  Amount: {transaction.Amount:C}\n";
                    formatted += $"  Date: {transaction.TransactionDate:yyyy-MM-dd}\n";
                    formatted += $"  RGS Code: {transaction.RgsCode}\n";
                    formatted += $"  Category: {transaction.RgsDescription}\n";
                    if (!string.IsNullOrEmpty(transaction.BankAccountName))
                    {
                        formatted += $"  Bank Account: {transaction.BankAccountName}\n";
                    }
                    formatted += "\n";
                }
                return formatted;
            }
            else if (toolResult.GetType().Name == "JsonElement")
            {
                // Handle JsonElement case - this is what we're getting from the LLM
                Console.WriteLine($"Handling JsonElement toolResult for function {functionName}");
                var jsonElement = (System.Text.Json.JsonElement)toolResult;
                
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var arrayLength = jsonElement.GetArrayLength();
                    Console.WriteLine($"JsonElement is an array with {arrayLength} items");
                    
                    if (arrayLength == 0)
                    {
                        return $"Function {functionName} returned no results. No transactions found matching the criteria.";
                    }
                    
                    // Check if this is category data (has rgsDescription and rgsCode but no description/amount)
                    var firstItem = jsonElement.EnumerateArray().First();
                    if (firstItem.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        bool hasRgsDescription = firstItem.TryGetProperty("rgsDescription", out _);
                        bool hasDescription = firstItem.TryGetProperty("description", out _);
                        
                        if (hasRgsDescription && !hasDescription)
                        {
                            // This is category data
                            var formatted = $"Function {functionName} returned {arrayLength} categories:\n\n";
                            foreach (var item in jsonElement.EnumerateArray())
                            {
                                if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    var rgsCode = item.TryGetProperty("rgsCode", out var rgsCodeProp) ? rgsCodeProp.GetString() : "N/A";
                                    var rgsDescription = item.TryGetProperty("rgsDescription", out var rgsDescProp) ? rgsDescProp.GetString() : "N/A";
                                    var similarity = item.TryGetProperty("similarity", out var simProp) ? simProp.GetDouble() : 0.0;
                                    
                                    formatted += $"• RGS Code: {rgsCode}\n";
                                    formatted += $"  Description: {rgsDescription}\n";
                                    formatted += $"  Similarity: {similarity:F3}\n\n";
                                }
                            }
                            return formatted;
                        }
                        else
                        {
                            // This is transaction data
                            var formatted = $"Function {functionName} returned {arrayLength} transactions:\n\n";
                            foreach (var item in jsonElement.EnumerateArray())
                            {
                                if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    var description = item.TryGetProperty("description", out var descProp) ? descProp.GetString() : "N/A";
                                    var amount = item.TryGetProperty("amount", out var amountProp) ? amountProp.GetDecimal() : 0;
                                    var transactionDate = item.TryGetProperty("transactionDate", out var dateProp) ? dateProp.GetString() : "N/A";
                                    var rgsCode = item.TryGetProperty("rgsCode", out var rgsProp) ? rgsProp.GetString() : "N/A";
                                    var rgsDescription = item.TryGetProperty("rgsDescription", out var rgsDescProp) ? rgsDescProp.GetString() : "N/A";
                                    var bankAccountName = item.TryGetProperty("bankAccountName", out var bankProp) ? bankProp.GetString() : "";
                                    
                                    formatted += $"• Description: {description}\n";
                                    formatted += $"  Amount: {amount:C}\n";
                                    formatted += $"  Date: {transactionDate}\n";
                                    formatted += $"  RGS Code: {rgsCode}\n";
                                    formatted += $"  Category: {rgsDescription}\n";
                                    if (!string.IsNullOrEmpty(bankAccountName))
                                    {
                                        formatted += $"  Bank Account: {bankAccountName}\n";
                                    }
                                    formatted += "\n";
                                }
                            }
                            return formatted;
                        }
                    }
                    else
                    {
                        // Not an object, return generic format
                        return $"Function {functionName} returned {arrayLength} items in an array.";
                    }
                }
                else
                {
                    return $"Function {functionName} returned: {jsonElement}";
                }
            }
            else
            {
                Console.WriteLine($"Unknown toolResult type: {toolResult?.GetType().Name}");
                return $"Function {functionName} returned the following result:\n\n{toolResult}";
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
            // For "all categories" queries, get all available categories from rgsmapping table
            // This ensures we get ALL categories in the system, not just those with transactions
            if (string.IsNullOrWhiteSpace(customerName) || customerName.ToLower().Contains("all"))
            {
                string sql = $@"
                    SELECT TOP (@TopCategories)
                        r.rgsDescription AS RgsDescription,
                        r.rgsCode AS RgsCode,
                        1.0 AS similarity
                    FROM dbo.rgsmapping r
                    WHERE r.rgsDescription IS NOT NULL
                    ORDER BY r.rgsDescription ASC";

                using var conn = new SqlConnection(connectionString);
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@TopCategories", topCategories);
                
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
                string sql = $@"
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
                cmd.Parameters.AddWithValue("@TopCategories", topCategories);
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
            if (!rgsCodes.Any())
            {
                return [];
            }

            Console.WriteLine($"GetTopTransactionsForCategoriesAsync called with: rgsCodes=[{string.Join(",", rgsCodes)}], topN={topN}, customerName='{customerName}', year={year}");

            var results = new List<TransactionResult>();
            var whereConditions = new List<string>();
            var parameters = new List<SqlParameter>();

            // Add RGS codes filter
            var rgsCodeParams = new List<string>();
            for (int i = 0; i < rgsCodes.Count; i++)
            {
                var paramName = $"@RgsCode{i}";
                rgsCodeParams.Add(paramName);
                parameters.Add(new SqlParameter(paramName, rgsCodes[i]));
            }
            whereConditions.Add($"t.RgsCode IN ({string.Join(",", rgsCodeParams)})");

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

            if (year.HasValue)
            {
                whereConditions.Add("YEAR(t.TransactionDate) = @Year");
                parameters.Add(new SqlParameter("@Year", year.Value));
            }

            if (!string.IsNullOrWhiteSpace(customerName))
            {
                whereConditions.Add("t.CustomerName = @CustomerName");
                parameters.Add(new SqlParameter("@CustomerName", customerName));
            }

            string whereClause = "WHERE " + string.Join(" AND ", whereConditions);

            string sql = $@"
                SELECT TOP (@TopN)
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
                {whereClause}
                ORDER BY t.Amount DESC, t.TransactionDate DESC";

            Console.WriteLine($"Generated SQL: {sql}");
            Console.WriteLine($"Parameters: {string.Join(", ", parameters.Select(p => $"{p.ParameterName}={p.Value}"))}");

            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 20; // 20 second timeout for database operations
            
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }
            cmd.Parameters.AddWithValue("@TopN", topN);

            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            int resultCount = 0;
            while (await reader.ReadAsync())
            {
                resultCount++;
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
                    Similarity = (float)Convert.ToDouble(reader["Similarity"])
                });
            }

            Console.WriteLine($"Query returned {resultCount} transactions");
            return results;
        }

        public async Task<List<TransactionResult>> GetTopTransactionsForCategoryQueryAsync(string connectionString, string categoryQuery, DateTime? startDate = null, DateTime? endDate = null, int? year = null, int topN = 10, string? customerName = null, int topCategories = 3)
        {
            // Ensure topCategories is at least 1 to avoid empty results
            if (topCategories <= 0)
            {
                topCategories = 3; // Default to 3 if 0 or negative
            }
            
            Console.WriteLine($"GetTopTransactionsForCategoryQueryAsync called with: categoryQuery='{categoryQuery}', topN={topN}, topCategories={topCategories}, customerName='{customerName}', year={year}");
            
            // This method works for ANY category mentioned in the query (marketing, office expenditure, tax related, travel, restaurant, utilities, advertising, legal, insurance, etc.)
            // First, search for relevant categories using vector search
            var relevantCategories = await SearchCategoriesByVectorAsync(connectionString, categoryQuery, topCategories, customerName);
            
            Console.WriteLine($"Found {relevantCategories.Count} relevant categories for '{categoryQuery}'");
            foreach (var category in relevantCategories)
            {
                Console.WriteLine($"  - {category.RgsCode}: {category.RgsDescription} (similarity: {category.Similarity:F3})");
            }
            
            if (!relevantCategories.Any())
            {
                Console.WriteLine("No relevant categories found, returning empty result");
                return new List<TransactionResult>();
            }

            // Extract RGS codes from the found categories
            var rgsCodes = relevantCategories.Select(c => c.RgsCode).Where(c => !string.IsNullOrEmpty(c)).ToList();
            Console.WriteLine($"Extracted {rgsCodes.Count} RGS codes: {string.Join(", ", rgsCodes)}");
            
            // Get top transactions for these categories
            return await GetTopTransactionsForCategoriesAsync(connectionString, rgsCodes, startDate, endDate, year, topN, customerName);
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