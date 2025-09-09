using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using TransactionLabeler.API.Data;
using TransactionLabeler.API.Models;

namespace TransactionLabeler.API.Services
{
    public interface ICsvImportService
    {
        Task<CsvImportResult> ImportTransactionsFromCsvAsync(Stream csvStream, string? customerName = null, bool skipValidation = false, bool generateEmbeddings = true);
    }

    public class CsvImportService : ICsvImportService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmbeddingService _embeddingService;
        private readonly ILogger<CsvImportService> _logger;

        public CsvImportService(ApplicationDbContext context, IEmbeddingService embeddingService, ILogger<CsvImportService> logger)
        {
            _context = context;
            _embeddingService = embeddingService;
            _logger = logger;
        }

        public async Task<CsvImportResult> ImportTransactionsFromCsvAsync(Stream csvStream, string? customerName = null, bool skipValidation = false, bool generateEmbeddings = true)
        {
            var result = new CsvImportResult
            {
                ImportTimestamp = DateTime.UtcNow
            };

            var startTime = DateTime.UtcNow;

            try
            {
                // Configure CSV reader
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    BadDataFound = null,
                    TrimOptions = TrimOptions.Trim
                };

                using var reader = new StreamReader(csvStream, Encoding.UTF8);
                using var csv = new CsvReader(reader, config);

                // Register the class map
                csv.Context.RegisterClassMap<CsvTransactionRowMap>();

                var transactions = new List<InversBankTransaction>();
                var errors = new List<string>();
                var warnings = new List<string>();
                int rowNumber = 1; // Start from 1 (header is row 0)

                await foreach (var csvRow in csv.GetRecordsAsync<CsvTransactionRow>())
                {
                    rowNumber++;
                    
                    try
                    {
                        // Validate the row
                        if (!skipValidation)
                        {
                            var validationErrors = ValidateCsvRow(csvRow, rowNumber);
                            if (validationErrors.Any())
                            {
                                errors.AddRange(validationErrors);
                                result.FailedRows++;
                                continue;
                            }
                        }

                        // Convert CSV row to InversBankTransaction
                        var transaction = ConvertCsvRowToTransaction(csvRow, customerName);
                        
                        // Generate embeddings if requested
                        if (generateEmbeddings)
                        {
                            await GenerateEmbeddingsForTransaction(transaction);
                        }

                        transactions.Add(transaction);
                    }
                    catch (Exception ex)
                    {
                        var error = $"Row {rowNumber}: {ex.Message}";
                        errors.Add(error);
                        result.FailedRows++;
                        _logger.LogWarning("Failed to process CSV row {RowNumber}: {Error}", rowNumber, ex.Message);
                    }
                }

                result.TotalRows = rowNumber - 1; // Subtract 1 for header row

                // Bulk insert transactions
                if (transactions.Any())
                {
                    await _context.InversBankTransactions.AddRangeAsync(transactions);
                    await _context.SaveChangesAsync();
                    result.SuccessfullyImported = transactions.Count;
                }

                result.Errors = errors;
                result.Warnings = warnings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing CSV data");
                result.Errors.Add($"Import failed: {ex.Message}");
            }
            finally
            {
                result.ProcessingTime = DateTime.UtcNow - startTime;
            }

            return result;
        }

        private List<string> ValidateCsvRow(CsvTransactionRow row, int rowNumber)
        {
            var errors = new List<string>();

            // Required field validations
            if (row.Amount == null || row.Amount == 0)
                errors.Add($"Row {rowNumber}: Amount is required and must be greater than 0");

            if (string.IsNullOrWhiteSpace(row.Description))
                errors.Add($"Row {rowNumber}: Description is required");

            if (row.TransactionDate == null)
                errors.Add($"Row {rowNumber}: TransactionDate is required");

            if (string.IsNullOrWhiteSpace(row.BankAccountNumber))
                errors.Add($"Row {rowNumber}: BankAccountNumber is required");

            // Date validation
            if (row.TransactionDate.HasValue && row.TransactionDate.Value > DateTime.UtcNow.AddDays(1))
                errors.Add($"Row {rowNumber}: TransactionDate cannot be in the future");

            if (row.TransactionDate.HasValue && row.TransactionDate.Value < DateTime.UtcNow.AddYears(-10))
                errors.Add($"Row {rowNumber}: TransactionDate cannot be more than 10 years old");

            // Amount validation
            if (row.Amount.HasValue && Math.Abs(row.Amount.Value) > 1000000)
                errors.Add($"Row {rowNumber}: Amount seems unusually large (>{Math.Abs(row.Amount.Value):C})");

            return errors;
        }

        private InversBankTransaction ConvertCsvRowToTransaction(CsvTransactionRow csvRow, string? customerName)
        {
            return new InversBankTransaction
            {
                Id = Guid.NewGuid(),
                Amount = csvRow.Amount,
                BankAccountName = csvRow.BankAccountName,
                BankAccountNumber = csvRow.BankAccountNumber,
                Description = csvRow.Description,
                TransactionDate = csvRow.TransactionDate,
                TransactionType = csvRow.TransactionType,
                TransactionIdentifierAccountNumber = csvRow.TransactionIdentifierAccountNumber,
                Code = csvRow.Code,
                AfBij = csvRow.AfBij,
                RgsCode = csvRow.RgsCode,
                NaceId = csvRow.NaceId,
                CategoryId = csvRow.CategoryId,
                CategoryName = csvRow.CategoryName,
                CustomerName = customerName
            };
        }

        private async Task GenerateEmbeddingsForTransaction(InversBankTransaction transaction)
        {
            try
            {
                // Create text for embedding generation
                var textForEmbedding = $"{transaction.Description ?? ""} {transaction.Amount} {transaction.TransactionDate} {transaction.CustomerName ?? ""} {transaction.BankAccountName ?? ""} {transaction.BankAccountNumber ?? ""} {transaction.TransactionType ?? ""} {transaction.RgsCode ?? ""} {transaction.CategoryName ?? ""}";
                
                // Generate embedding
                var embedding = await _embeddingService.GetEmbeddingAsync(textForEmbedding);
                
                // Store as JSON string (legacy format)
                transaction.Embedding = System.Text.Json.JsonSerializer.Serialize(embedding);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to generate embedding for transaction {TransactionId}: {Error}", transaction.Id, ex.Message);
                // Continue without embedding - don't fail the entire import
            }
        }
    }

    // Custom DateTime converter that handles multiple formats
    public class FlexibleDateTimeConverter : CsvHelper.TypeConversion.ITypeConverter
    {
        private static readonly string[] DateTimeFormats = new[]
        {
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss",
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "MM/dd/yyyy",
            "yyyy/MM/dd"
        };

        public object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Try to parse with various formats
            foreach (var format in DateTimeFormats)
            {
                if (DateTime.TryParseExact(text, format, null, System.Globalization.DateTimeStyles.None, out var result))
                {
                    return result;
                }
            }

            // Fallback to standard DateTime parsing
            if (DateTime.TryParse(text, out var fallbackResult))
            {
                return fallbackResult;
            }

            throw new FormatException($"Unable to parse '{text}' as DateTime");
        }

        public string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
        {
            if (value is DateTime dateTime)
            {
                return dateTime.ToString("yyyy-MM-dd");
            }
            return value?.ToString() ?? string.Empty;
        }
    }

    // CSV mapping configuration
    public class CsvTransactionRowMap : ClassMap<CsvTransactionRow>
    {
        public CsvTransactionRowMap()
        {
            Map(m => m.Amount).Name("amount");
            Map(m => m.BankAccountName).Name("bankaccountname");
            Map(m => m.BankAccountNumber).Name("bankaccountnumber");
            Map(m => m.Description).Name("description");
            Map(m => m.TransactionDate).Name("transactiondate").TypeConverter<FlexibleDateTimeConverter>();
            Map(m => m.TransactionType).Name("transactiontype");
            Map(m => m.TransactionIdentifierAccountNumber).Name("transactionidentifier_accountnumber");
            Map(m => m.Code).Name("code");
            Map(m => m.AfBij).Name("af_bij");
            Map(m => m.RgsCode).Name("rgsCode");
            Map(m => m.NaceId).Name("naceId");
            Map(m => m.CategoryId).Name("categoryId");
            Map(m => m.CategoryName).Name("categoryName");
        }
    }
}
