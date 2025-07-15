using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Collections.Generic;

namespace TransactionLabeler.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransactionsController : ControllerBase
    {
        private readonly ITransactionService _transactionService;
        private readonly IConfiguration _configuration;

        public TransactionsController(ITransactionService transactionService, IConfiguration configuration)
        {
            _transactionService = transactionService;
            _configuration = configuration;
        }

        [HttpPost("update-all-persistent-embeddings")]
        public async Task<IActionResult> UpdateAllPersistentBankStatementEmbeddings()
        {
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            await _transactionService.UpdateAllPersistentBankStatementEmbeddingsAsync(connectionString);
            return Ok(new { status = "Batch embedding update triggered." });
        }

        [HttpPost("update-all-invers-embeddings")]
        public async Task<IActionResult> UpdateAllInversBankTransactionEmbeddings()
        {
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            await _transactionService.UpdateAllInversBankTransactionEmbeddingsAsync(connectionString);
            return Ok(new { status = "Batch embedding update for inversbanktransaction triggered." });
        }

        [HttpPost("vector-search")]
        public async Task<IActionResult> VectorSearch([FromBody] string query)
        {
            // 1. Get embedding for the input text
            var embedding = await _transactionService.GetEmbeddingAsync(query);

            // 2. Use SQL-based vector search with VECTOR_DISTANCE
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            var sqlResults = await _transactionService.VectorSearchInSqlAsync(connectionString, embedding);

            // 3. Convert to the expected format and convert distance to similarity
            var finalResults = sqlResults.Select(r => new {
                Id = r.Id,
                Description = r.Description,
                Amount = r.Amount,
                TransactionDate = r.TransactionDate,
                RgsCode = r.RgsCode,
                RgsDescription = r.RgsDescription,
                RgsShortDescription = r.RgsShortDescription,
                Similarity = 1.0f - Math.Min(r.Similarity, 1.0f) // Convert distance to similarity (1 - distance)
            });

            return Ok(finalResults);
        }

        [HttpPost("vector-search-sql")]
        public async Task<IActionResult> VectorSearchSql([FromBody] string query)
        {
            // 1. Get embedding for the input text
            var embedding = await _transactionService.GetEmbeddingAsync(query);

            // 2. Use SQL-based vector search with VECTOR_DISTANCE
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            var sqlResults = await _transactionService.VectorSearchInSqlAsync(connectionString, embedding);

            // 3. Return raw results with distance values
            var finalResults = sqlResults.Select(r => new {
                Id = r.Id,
                Description = r.Description,
                Amount = r.Amount,
                TransactionDate = r.TransactionDate,
                Distance = r.Similarity, // Raw distance value from VECTOR_DISTANCE
                Similarity = 1.0f - Math.Min(r.Similarity, 1.0f) // Converted to similarity
            });

            return Ok(finalResults);
        }

        [HttpPost("intelligent-vector-search")]
        public async Task<IActionResult> IntelligentVectorSearch([FromBody] string query)
        {
            // Use the new intelligent vector search that classifies queries and uses appropriate embeddings
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            var sqlResults = await _transactionService.IntelligentVectorSearchAsync(connectionString, query);

            // Convert to the expected format and convert distance to similarity
            var finalResults = sqlResults.Select(r => new {
                Id = r.Id,
                Description = r.Description,
                Amount = r.Amount,
                TransactionDate = r.TransactionDate,
                RgsCode = r.RgsCode,
                RgsDescription = r.RgsDescription,
                RgsShortDescription = r.RgsShortDescription,
                Similarity = 1.0f - Math.Min(r.Similarity, 1.0f) // Convert distance to similarity (1 - distance)
            });

            return Ok(finalResults);
        }

        [HttpPost("intelligent-query-with-tools")]
        public async Task<IActionResult> IntelligentQueryWithTools([FromBody] string query)
        {
            try
            {
                string connectionString = _configuration.GetConnectionString("DefaultConnection");
                var result = await _transactionService.ProcessIntelligentQueryWithToolsAsync(connectionString, query);
                
                return Ok(new { 
                    query = query,
                    response = result,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}  