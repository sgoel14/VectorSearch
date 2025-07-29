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
            try
            {
                string? connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    return BadRequest(new { error = "Database connection string not configured" });
                }
                
                await _transactionService.UpdateAllPersistentBankStatementEmbeddingsAsync(connectionString);
                return Ok(new { status = "Batch embedding update triggered." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Database operation failed: {ex.Message}" });
            }
        }

        [HttpPost("update-all-invers-embeddings")]
        public async Task<IActionResult> UpdateAllInversBankTransactionEmbeddings()
        {
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            await _transactionService.UpdateAllInversBankTransactionEmbeddingsAsync(connectionString);
            return Ok(new { status = "Batch embedding update for inversbanktransaction triggered." });
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



        [HttpGet("health")]
        public async Task<IActionResult> HealthCheck()
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    return BadRequest(new { 
                        status = "unhealthy", 
                        error = "Database connection string not configured",
                        timestamp = DateTime.UtcNow
                    });
                }

                // Test database connection
                using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                await conn.OpenAsync();
                
                return Ok(new { 
                    status = "healthy", 
                    database = "connected",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { 
                    status = "unhealthy", 
                    error = $"Database connection failed: {ex.Message}",
                    timestamp = DateTime.UtcNow
                });
            }
        }

        [HttpPost("search-categories")]
        public async Task<IActionResult> SearchCategories([FromBody] CategorySearchRequest request)
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    return BadRequest(new { error = "Database connection string not configured" });
                }

                var results = await _transactionService.SearchCategoriesByVectorAsync(connectionString, request.CategoryQuery, request.TopCategories, request.CustomerName);
                
                return Ok(new { 
                    categories = results,
                    query = request.CategoryQuery,
                    topCategories = request.TopCategories
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("transactions-for-category")]
        public async Task<IActionResult> GetTransactionsForCategory([FromBody] CategoryTransactionRequest request)
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    return BadRequest(new { error = "Database connection string not configured" });
                }

                var results = await _transactionService.GetTopTransactionsForCategoryQueryAsync(
                    connectionString, 
                    request.CategoryQuery, 
                    request.StartDate, 
                    request.EndDate, 
                    request.Year, 
                    request.TopN, 
                    request.CustomerName, 
                    request.TopCategories);
                
                return Ok(new { 
                    transactions = results,
                    categoryQuery = request.CategoryQuery,
                    topN = request.TopN,
                    dateRange = new { startDate = request.StartDate, endDate = request.EndDate, year = request.Year },
                    customerName = request.CustomerName
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }



    public class CategorySearchRequest
    {
        public string CategoryQuery { get; set; } = "";
        public int TopCategories { get; set; } = 5;
        public string? CustomerName { get; set; }
    }

    public class CategoryTransactionRequest
    {
        public string CategoryQuery { get; set; } = "";
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? Year { get; set; }
        public int TopN { get; set; } = 10;
        public string? CustomerName { get; set; }
        public int TopCategories { get; set; } = 3;
    }


}  