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
            if (await context.Transactions.AnyAsync())
            {
                return;
            }

            var sampleTransactions = new[]
            {
                // Groceries
                new Transaction
                {
                    Description = "Grocery shopping at Walmart",
                    Amount = 125.50m,
                    TransactionDate = DateTime.UtcNow.AddDays(-1),
                    Label = "Groceries"
                },
                new Transaction
                {
                    Description = "Whole Foods Market purchase",
                    Amount = 85.25m,
                    TransactionDate = DateTime.UtcNow.AddDays(-3),
                    Label = "Groceries"
                },
                new Transaction
                {
                    Description = "Trader Joe's groceries",
                    Amount = 65.75m,
                    TransactionDate = DateTime.UtcNow.AddDays(-5),
                    Label = "Groceries"
                },

                // Housing
                new Transaction
                {
                    Description = "Monthly rent payment",
                    Amount = 1500.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-2),
                    Label = "Housing"
                },
                new Transaction
                {
                    Description = "Home insurance premium",
                    Amount = 120.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-15),
                    Label = "Housing"
                },
                new Transaction
                {
                    Description = "Property tax payment",
                    Amount = 2500.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-30),
                    Label = "Housing"
                },

                // Entertainment
                new Transaction
                {
                    Description = "Netflix subscription",
                    Amount = 15.99m,
                    TransactionDate = DateTime.UtcNow.AddDays(-3),
                    Label = "Entertainment"
                },
                new Transaction
                {
                    Description = "Spotify Premium subscription",
                    Amount = 9.99m,
                    TransactionDate = DateTime.UtcNow.AddDays(-4),
                    Label = "Entertainment"
                },
                new Transaction
                {
                    Description = "Movie tickets - AMC",
                    Amount = 24.50m,
                    TransactionDate = DateTime.UtcNow.AddDays(-10),
                    Label = "Entertainment"
                },
                new Transaction
                {
                    Description = "Concert tickets - Taylor Swift",
                    Amount = 250.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-20),
                    Label = "Entertainment"
                },

                // Transportation
                new Transaction
                {
                    Description = "Gas station - Shell",
                    Amount = 45.75m,
                    TransactionDate = DateTime.UtcNow.AddDays(-4),
                    Label = "Transportation"
                },
                new Transaction
                {
                    Description = "Uber ride to airport",
                    Amount = 35.50m,
                    TransactionDate = DateTime.UtcNow.AddDays(-8),
                    Label = "Transportation"
                },
                new Transaction
                {
                    Description = "Car maintenance - Oil change",
                    Amount = 75.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-12),
                    Label = "Transportation"
                },

                // Utilities
                new Transaction
                {
                    Description = "Electricity bill payment",
                    Amount = 85.25m,
                    TransactionDate = DateTime.UtcNow.AddDays(-5),
                    Label = "Utilities"
                },
                new Transaction
                {
                    Description = "Water bill payment",
                    Amount = 45.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-6),
                    Label = "Utilities"
                },
                new Transaction
                {
                    Description = "Internet bill - Comcast",
                    Amount = 65.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-7),
                    Label = "Utilities"
                },
                new Transaction
                {
                    Description = "Phone bill - AT&T",
                    Amount = 75.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-9),
                    Label = "Utilities"
                },

                // Dining
                new Transaction
                {
                    Description = "Dinner at Olive Garden",
                    Amount = 65.80m,
                    TransactionDate = DateTime.UtcNow.AddDays(-6),
                    Label = "Dining"
                },
                new Transaction
                {
                    Description = "Starbucks coffee",
                    Amount = 5.75m,
                    TransactionDate = DateTime.UtcNow.AddDays(-1),
                    Label = "Dining"
                },
                new Transaction
                {
                    Description = "Lunch at Chipotle",
                    Amount = 12.50m,
                    TransactionDate = DateTime.UtcNow.AddDays(-2),
                    Label = "Dining"
                },

                // Health & Fitness
                new Transaction
                {
                    Description = "Gym membership",
                    Amount = 29.99m,
                    TransactionDate = DateTime.UtcNow.AddDays(-7),
                    Label = "Health & Fitness"
                },
                new Transaction
                {
                    Description = "Doctor's visit - Copay",
                    Amount = 25.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-14),
                    Label = "Health & Fitness"
                },
                new Transaction
                {
                    Description = "Prescription medication",
                    Amount = 15.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-16),
                    Label = "Health & Fitness"
                },

                // Subscriptions
                new Transaction
                {
                    Description = "Amazon Prime subscription",
                    Amount = 14.99m,
                    TransactionDate = DateTime.UtcNow.AddDays(-8),
                    Label = "Subscriptions"
                },
                new Transaction
                {
                    Description = "Microsoft 365 subscription",
                    Amount = 69.99m,
                    TransactionDate = DateTime.UtcNow.AddDays(-25),
                    Label = "Subscriptions"
                },
                new Transaction
                {
                    Description = "Adobe Creative Cloud",
                    Amount = 52.99m,
                    TransactionDate = DateTime.UtcNow.AddDays(-28),
                    Label = "Subscriptions"
                }
            };

            foreach (var transaction in sampleTransactions)
            {
                var embedding = await embeddingService.GetEmbeddingAsync(transaction.Description);
                transaction.Embedding = embedding;
                transaction.CreatedAt = DateTime.UtcNow;
                context.Transactions.Add(transaction);
            }

            await context.SaveChangesAsync();
        }
    }
} 