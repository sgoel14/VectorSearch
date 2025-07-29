namespace TransactionLabeler.API.Models
{
    public class CategorySpendingResult
    {
        public string CategoryQuery { get; set; } = "";
        public decimal TotalSpending { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? CustomerName { get; set; }
        public List<CategorySpendingBreakdown> Breakdown { get; set; } = new List<CategorySpendingBreakdown>();
        public int TransactionCount { get; set; }
    }

    public class CategorySpendingBreakdown
    {
        public string RgsCode { get; set; } = "";
        public string RgsDescription { get; set; } = "";
        public string RgsShortDescription { get; set; } = "";
        public decimal Amount { get; set; }
        public int TransactionCount { get; set; }
    }
} 