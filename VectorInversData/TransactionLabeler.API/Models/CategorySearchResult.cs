namespace TransactionLabeler.API.Models
{
    public class CategorySearchResult
    {
        public string? RgsDescription { get; set; }
        public string? RgsCode { get; set; }
        public float Similarity { get; set; }
    }
} 