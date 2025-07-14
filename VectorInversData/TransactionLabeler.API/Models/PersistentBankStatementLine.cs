using System;

namespace TransactionLabeler.API.Models
{
    public class PersistentBankStatementLine
    {
        public Guid Id { get; set; }
        public decimal? Amount { get; set; }
        public string? BankAccountName { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? BatchId { get; set; }
        public string? Description { get; set; }
        public string? ReturnCode { get; set; }
        public DateTime? TransactionDate { get; set; }
        public string? TransactionReference { get; set; }
        public int? PersistentBankStatement_Order { get; set; }
        public string? TransactionType { get; set; }
        public string? TransactionIdentifier_AccountNumber { get; set; }
        public long? TransactionIdentifier_InternalReferenceNumber { get; set; }
        public string? TransactionIdentifier_TransactionNumber { get; set; }
        public int? TransactionIdentifier_Year { get; set; }
        public string? Currency { get; set; }
        public string? BankId { get; set; }
        public bool? IsForeignCurrency { get; set; }
        public bool? IsHighRiskCountry { get; set; }
        public string? CounterpartyBic { get; set; }
        public decimal? BalanceAfterTransaction { get; set; }
        public string? Embedding { get; set; } // VECTOR column, stored as JSON string for now
    }
} 