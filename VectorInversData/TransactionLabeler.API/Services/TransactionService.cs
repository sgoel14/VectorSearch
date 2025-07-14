using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
                        await VectorSqlHelper.InsertMultipleEmbeddingsAsync(connectionString, row.Id, embeddings);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                page++;
            } while (batch.Count == pageSize);
        }

        // Generate multiple specialized embeddings
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