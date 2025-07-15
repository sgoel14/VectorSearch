using System;

namespace TransactionLabeler.API.Models
{
    public class CategoryExpenseResult
    {
        public string RgsDescription { get; set; }
        public string RgsCode { get; set; }
        public decimal TotalExpense { get; set; }
    }
} 