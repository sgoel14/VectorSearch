using System;

namespace TransactionLabeler.API.Models
{
    public class Transaction
    {
        public int Id { get; set; }
        public string Description { get; set; }
        public decimal Amount { get; set; }
        public DateTime TransactionDate { get; set; }
        public string Label { get; set; }
        public byte[] Embedding { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
} 