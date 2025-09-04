using System.ComponentModel.DataAnnotations;

namespace TransactionLabeler.API.Models
{
    public class CsvTransactionRow
    {
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
    }

    public class CsvImportResult
    {
        public int TotalRows { get; set; }
        public int SuccessfullyImported { get; set; }
        public int FailedRows { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public TimeSpan ProcessingTime { get; set; }
        public DateTime ImportTimestamp { get; set; }
    }

    public class CsvImportRequest
    {
        [Required]
        public IFormFile CsvFile { get; set; } = null!;
        
        public string? CustomerName { get; set; }
        public bool SkipValidation { get; set; } = false;
        public bool GenerateEmbeddings { get; set; } = true;
    }
}
