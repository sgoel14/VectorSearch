using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using TransactionLabeler.API.Data;
using TransactionLabeler.API.Services;
using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Add DbContext with retry logic for Azure SQL Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlServerOptionsAction: sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
        }));

// Add services
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<ISemanticKernelService>(provider =>
    new SemanticKernelService(
        provider.GetRequiredService<ITransactionService>(),
        provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!,
        provider.GetRequiredService<IConfiguration>()));

// Configure Microsoft.Extensions.AI ChatClient for function calling
builder.Services.AddSingleton<IChatClient>(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    var azureClient = new AzureOpenAIClient(
        new Uri(configuration["AzureOpenAI:Endpoint"]!),
        new ApiKeyCredential(configuration["AzureOpenAI:Key"]!));
    return azureClient.AsChatClient(configuration["AzureOpenAI:ChatDeploymentName"]!);
});

builder.Services.AddScoped<FinancialTools>(provider =>
    new FinancialTools(
        provider.GetRequiredService<ITransactionService>(),
        provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

// Ensure database is created and seeded with retry logic
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<ApplicationDbContext>();
    var embeddingService = services.GetRequiredService<IEmbeddingService>();
    
    try
    {
        Console.WriteLine("Attempting to connect to database...");
        context.Database.EnsureCreated();
        Console.WriteLine("Database connection successful.");
        
        await SampleDataSeeder.SeedSampleDataAsync(context, embeddingService);
        Console.WriteLine("Sample data seeding completed.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database connection failed: {ex.Message}");
        Console.WriteLine("Application will start but database operations may fail.");
        Console.WriteLine("Please check your Azure SQL Database connection and try again.");
        
        // Continue running the app even if database initialization fails
        // This allows the API to start and provide error messages for database operations
    }
}

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
