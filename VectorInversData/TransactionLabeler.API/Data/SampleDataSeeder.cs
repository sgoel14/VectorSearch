using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Data
{
    public static class SampleDataSeeder
    {
        public static async Task SeedSampleDataAsync(ApplicationDbContext context, IEmbeddingService embeddingService)
        {
            // Remove all context.Transactions and transaction seeding logic.
        }
    }
} 