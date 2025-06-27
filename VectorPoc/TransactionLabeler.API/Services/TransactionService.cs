using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using TransactionLabeler.API.Data;
using TransactionLabeler.API.Models;

namespace TransactionLabeler.API.Services
{
    public interface ITransactionService
    {
        Task<Transaction> ProcessNewTransactionAsync(Transaction transaction);
        Task<Transaction> GetTransactionByIdAsync(int id);
        Task<TransactionWithSimilarityDto[]> GetSimilarTransactionsAsync(string description, int limit = 5);
        Task UpdateTransactionAsync(Transaction transaction);
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

        public async Task<Transaction> ProcessNewTransactionAsync(Transaction transaction)
        {
            // Generate embedding for the new transaction
            var embedding = await _embeddingService.GetEmbeddingAsync(transaction.Description);
            transaction.Embedding = embedding;
            
            // Find similar transactions using Azure SQL vector search
            var similarTransactions = await GetSimilarTransactionsAsync(transaction.Description);
            
            // Realistic similarity threshold for text embeddings
            float similarityThreshold = 0.35f;
            string assignedLabel = "Uncategorized";
            float highestSimilarity = 0f;
            
            if (similarTransactions.Any())
            {
                var similarities = similarTransactions
                    .Select(t => new {
                        t.Label,
                        t.Description,
                        Similarity = t.Similarity
                    })
                    .OrderByDescending(x => x.Similarity)
                    .ToList();
                
                highestSimilarity = similarities.First().Similarity;
                
                // Debug: Log all similarity scores
                Console.WriteLine($"Processing transaction: {transaction.Description}");
                Console.WriteLine("Similarity scores:");
                foreach (var sim in similarities.Take(5))
                {
                    Console.WriteLine($"  {sim.Similarity:F3} - '{sim.Description}' (Label: {sim.Label})");
                }
                
                if (highestSimilarity >= similarityThreshold)
                {
                    // Get the most common label among similar transactions
                    var labelGroups = similarities
                        .Where(s => s.Similarity >= similarityThreshold)
                        .GroupBy(x => x.Label)
                        .OrderByDescending(g => g.Count())
                        .ToList();
                    
                    if (labelGroups.Any())
                    {
                        assignedLabel = labelGroups.First().Key;
                        Console.WriteLine($"✅ Assigned label from embeddings: {assignedLabel}");
                    }
                }
                else
                {
                    Console.WriteLine($"❌ Similarity {highestSimilarity:F3} below threshold {similarityThreshold}");
                    // Try with a lower threshold for very similar categories
                    var lowerThreshold = 0.25f;
                    var lowerSimilarities = similarities.Where(s => s.Similarity >= lowerThreshold).ToList();
                    if (lowerSimilarities.Any())
                    {
                        var bestMatch = lowerSimilarities.First();
                        if (bestMatch.Similarity >= 0.30f) // Still reasonably similar
                        {
                            assignedLabel = bestMatch.Label;
                            Console.WriteLine($"✅ Assigned label from lower threshold: {assignedLabel} (similarity: {bestMatch.Similarity:F3})");
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("No similar transactions found in database");
            }
            transaction.Label = assignedLabel;
            transaction.CreatedAt = DateTime.UtcNow;
            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();
            return transaction;
        }

        public async Task<Transaction> GetTransactionByIdAsync(int id)
        {
            return await _context.Transactions.FindAsync(id);
        }

        public async Task<TransactionWithSimilarityDto[]> GetSimilarTransactionsAsync(string description, int limit = 5)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(description);
            
            // Use ADO.NET directly for the vector similarity search
            var connectionString = _context.Database.GetConnectionString();
            var results = new List<TransactionWithSimilarityDto>();
            
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                // Convert float array to string format for vector operations
                var vectorString = "[" + string.Join(",", queryEmbedding) + "]";
                
                var sql = $@"
                    DECLARE @queryVector AS vector(1536) = {vectorString};
                    
                    SELECT Id, Description, Amount, TransactionDate, Label, CreatedAt, UpdatedAt,
                           (1.0 - VECTOR_DISTANCE(Embedding, @queryVector, 'COSINE')) as Similarity
                    FROM Transactions 
                    WHERE Embedding IS NOT NULL
                    ORDER BY VECTOR_DISTANCE(Embedding, @queryVector, 'COSINE') ASC
                    OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY";
                
                using (var command = new SqlCommand(sql, connection))
                {
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add(new TransactionWithSimilarityDto
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                                Description = reader.GetString(reader.GetOrdinal("Description")),
                                Amount = reader.GetDecimal(reader.GetOrdinal("Amount")),
                                TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                                Label = reader.GetString(reader.GetOrdinal("Label")),
                                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                                UpdatedAt = reader.IsDBNull(reader.GetOrdinal("UpdatedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
                                Similarity = reader.GetFloat(reader.GetOrdinal("Similarity"))
                            });
                        }
                    }
                }
            }
            
            // Debug: Log similarity scores
            Console.WriteLine($"\n🔍 Similar transactions for: '{description}'");
            foreach (var item in results)
            {
                Console.WriteLine($"  {item.Similarity:F3} - '{item.Description}' (Label: {item.Label})");
            }
            
            return results.ToArray();
        }

        public async Task UpdateTransactionAsync(Transaction transaction)
        {
            _context.Transactions.Update(transaction);
            await _context.SaveChangesAsync();
        }

        private byte[] ConvertFloatArrayToBytes(float[] floats)
        {
            if (floats == null) return null;
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
} 