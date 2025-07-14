using System;

namespace TransactionLabeler.API.Models
{
    public class InversBankTransaction
    {
        public Guid Id { get; set; }
        public decimal? Amount { get; set; }
        public string? BankAccountName { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? Description { get; set; }
        public DateTime? TransactionDate { get; set; }
        public string? TransactionType { get; set; }
        public string? TransactionIdentifierAccountNumber { get; set; }
        public string? Code { get; set; }
        public string? AfBij { get; set; }
        public string? RgsCode { get; set; }
        public string? NaceId { get; set; }
        public string? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public string? BusinessId { get; set; }
        
        // Multiple embedding columns for different query types
        public string? Embedding { get; set; } // Legacy - keep for backward compatibility (stored as JSON string)
        
        // New VECTOR columns - these will be mapped to VECTOR(1536) in the database
        // Entity Framework will handle these as strings for storage/retrieval
        public string? ContentEmbedding { get; set; } // VECTOR(1536) - For description-based queries
        public string? AmountEmbedding { get; set; } // VECTOR(1536) - For amount-based queries
        public string? DateEmbedding { get; set; } // VECTOR(1536) - For temporal queries
        public string? CategoryEmbedding { get; set; } // VECTOR(1536) - For category-based queries
        public string? CombinedEmbedding { get; set; } // VECTOR(1536) - For general semantic search
    }
} 