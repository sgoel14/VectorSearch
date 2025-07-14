using Microsoft.EntityFrameworkCore;
using TransactionLabeler.API.Models;

namespace TransactionLabeler.API.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<PersistentBankStatementLine> PersistentBankStatementLines { get; set; }
        public DbSet<InversBankTransaction> InversBankTransactions { get; set; }
        public DbSet<RgsMapping> RgsMappings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Transaction>()
                .Property(t => t.Embedding)
                .HasColumnType("varbinary(max)");

            modelBuilder.Entity<PersistentBankStatementLine>()
                .ToTable("persistentbankstatementline")
                .Property(p => p.Amount)
                .HasColumnType("decimal(18,2)");
            modelBuilder.Entity<PersistentBankStatementLine>()
                .Property(p => p.BalanceAfterTransaction)
                .HasColumnType("decimal(18,2)");

            modelBuilder.Entity<InversBankTransaction>()
                .ToTable("inversbanktransaction")
                .Property(t => t.Amount)
                .HasColumnType("decimal(19,5)");
            modelBuilder.Entity<InversBankTransaction>()
                .Property(t => t.Embedding)
                .HasColumnType("VECTOR(1536)");

            modelBuilder.Entity<RgsMapping>()
                .HasNoKey()
                .ToTable("rgsmapping");
        }
    }
} 