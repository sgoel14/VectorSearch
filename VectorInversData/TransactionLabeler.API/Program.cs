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

// Add DbContext
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add services
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();

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

// Ensure database is created and seeded
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<ApplicationDbContext>();
    var embeddingService = services.GetRequiredService<IEmbeddingService>();
    
    context.Database.EnsureCreated();
    await SampleDataSeeder.SeedSampleDataAsync(context, embeddingService);
}

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
