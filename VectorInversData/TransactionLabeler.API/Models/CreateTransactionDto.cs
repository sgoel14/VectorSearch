namespace TransactionLabeler.API.Models
{
    public class CreateTransactionDto
    {
        public string Description { get; set; }
        public decimal Amount { get; set; }
        public DateTime TransactionDate { get; set; }
    }
} 