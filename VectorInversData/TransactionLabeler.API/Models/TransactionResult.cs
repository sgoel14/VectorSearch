using System;

namespace TransactionLabeler.API.Models
{
    public class TransactionResult
    {
        public Guid Id { get; set; }
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
        public DateTime? TransactionDate { get; set; }
        public string? RgsCode { get; set; }
        public string? RgsDescription { get; set; }
        public string? RgsShortDescription { get; set; }
        public string? BankAccountName { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? TransactionIdentifierAccountNumber { get; set; }
        public float Similarity { get; set; } = 1.0f; // For consistency with existing output, 1.0 for direct match
    }
}
