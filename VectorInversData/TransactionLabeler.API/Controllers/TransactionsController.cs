using Microsoft.AspNetCore.Mvc;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransactionsController(ITransactionService transactionService, IConfiguration configuration) : ControllerBase
    {
        private readonly ITransactionService _transactionService = transactionService;
        private readonly IConfiguration _configuration = configuration;

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
        public async Task<IActionResult> UpdateAllInversBankTransactionEmbeddings(IConfiguration _configuration)
        {
            string connectionString = _configuration.GetConnectionString("DefaultConnection");
            await _transactionService.UpdateAllInversBankTransactionEmbeddingsAsync(connectionString);
            return Ok(new { status = "Batch embedding update for inversbanktransaction triggered." });
        }

        [HttpPost("intelligent-query-with-tools")]
        public async Task<IActionResult> IntelligentQueryWithTools([FromBody] string query)
        {
            try
            {
                string connectionString = _configuration.GetConnectionString("DefaultConnection");
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                var result = await semanticKernelService.ProcessIntelligentQueryWithAdvancedFeaturesAsync(connectionString, query, "legacy-migration");
                
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

        [HttpPost("intelligent-query-advanced")]
        public async Task<IActionResult> IntelligentQueryAdvanced([FromBody] AdvancedQueryRequest request)
        {
            try
            {
                string connectionString = _configuration.GetConnectionString("DefaultConnection");
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                var result = await semanticKernelService.ProcessIntelligentQueryWithAdvancedFeaturesAsync(connectionString, request.Query, request.SessionId);
                
                return Ok(new { 
                    query = request.Query,
                    response = result,
                    sessionId = request.SessionId ?? "default",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("chat-history/{sessionId}")]
        public async Task<ActionResult<object>> GetChatHistory(string sessionId)
        {
            try
            {
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                var chatHistory = await semanticKernelService.GetChatHistoryAsync(sessionId);
                return Ok(new
                {
                    sessionId,
                    chatHistory,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Error retrieving chat history: {ex.Message}" });
            }
        }

        [HttpPost("create-summary/{sessionId}")]
        public async Task<ActionResult<object>> CreateManualSummary(string sessionId)
        {
            try
            {
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                var summary = await semanticKernelService.CreateManualSummaryAsync(sessionId);
                
                return Ok(new
                {
                    sessionId,
                    summary,
                    message = "Manual context summary created and stored in Azure AI Search",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Error creating manual summary: {ex.Message}" });
            }
        }

        [HttpGet("summary-stats/{sessionId}")]
        public async Task<ActionResult<object>> GetSummaryStats(string sessionId)
        {
            try
            {
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                var stats = await semanticKernelService.GetSummaryStatsAsync(sessionId);
                
                return Ok(new
                {
                    sessionId,
                    stats,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Error getting summary stats: {ex.Message}" });
            }
        }

        [HttpDelete("chat-history/{sessionId}")]
        public async Task<IActionResult> ClearChatHistory(string sessionId)
        {
            try
            {
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                await semanticKernelService.ClearChatHistoryAsync(sessionId);
                
                return Ok(new { 
                    sessionId,
                    status = "Chat history cleared successfully",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("context-summary/{sessionId}")]
        public async Task<IActionResult> GetContextSummary(string sessionId)
        {
            try
            {
                var semanticKernelService = HttpContext.RequestServices.GetRequiredService<ISemanticKernelService>();
                var summary = await semanticKernelService.GetContextSummaryAsync(sessionId);
                
                return Ok(new { 
                    sessionId,
                    contextSummary = summary,
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

    public class AdvancedQueryRequest
    {
        public string Query { get; set; } = "";
        public string? SessionId { get; set; }
    }


}  