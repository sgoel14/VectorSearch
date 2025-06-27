using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransactionsController : ControllerBase
    {
        private readonly ITransactionService _transactionService;

        public TransactionsController(ITransactionService transactionService)
        {
            _transactionService = transactionService;
        }

        [HttpPost]
        public async Task<ActionResult<Transaction>> CreateTransaction(CreateTransactionDto dto)
        {
            var transaction = new Transaction
            {
                Description = dto.Description,
                Amount = dto.Amount,
                TransactionDate = dto.TransactionDate
                // Label and Embedding will be set by the service
            };
            var result = await _transactionService.ProcessNewTransactionAsync(transaction);
            return CreatedAtAction(nameof(GetTransaction), new { id = result.Id }, result);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Transaction>> GetTransaction(int id)
        {
            var transaction = await _transactionService.GetTransactionByIdAsync(id);
            if (transaction == null)
            {
                return NotFound();
            }
            return transaction;
        }

        [HttpGet("similar")]
        public async Task<ActionResult<TransactionWithSimilarityDto[]>> GetSimilarTransactions([FromQuery] string description)
        {
            var transactions = await _transactionService.GetSimilarTransactionsAsync(description);
            return transactions;
        }

        [HttpPut("{id}/label")]
        public async Task<IActionResult> UpdateLabel(int id, [FromBody] string newLabel)
        {
            var transaction = await _transactionService.GetTransactionByIdAsync(id);
            if (transaction == null)
            {
                return NotFound();
            }
            transaction.Label = newLabel;
            transaction.UpdatedAt = DateTime.UtcNow;
            await _transactionService.UpdateTransactionAsync(transaction);
            return Ok(transaction);
        }
    }
} 