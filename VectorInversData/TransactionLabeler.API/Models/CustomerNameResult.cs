using System;

namespace TransactionLabeler.API.Models
{
    public class CustomerNameResult
    {
        public string CustomerName { get; set; } = string.Empty;
        public int TransactionCount { get; set; }
        public decimal TotalAmount { get; set; }
        public int SimilarityScore { get; set; }
    }
} 