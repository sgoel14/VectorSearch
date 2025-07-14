using System;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;

namespace TransactionLabeler.API.Services
{
    public interface IEmbeddingService
    {
        Task<float[]> GetEmbeddingAsync(string text);
    }

    public class EmbeddingService : IEmbeddingService
    {
        private readonly OpenAIClient _client;
        private readonly string _deploymentName;

        public EmbeddingService(IConfiguration configuration)
        {
            var endpoint = configuration["AzureOpenAI:Endpoint"];
            var key = configuration["AzureOpenAI:Key"];
            _deploymentName = configuration["AzureOpenAI:DeploymentName"];

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(_deploymentName))
            {
                throw new ArgumentException("Azure OpenAI configuration is missing. Please check your appsettings.json file.");
            }

            _client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                var response = await _client.GetEmbeddingsAsync(_deploymentName, new EmbeddingsOptions(text));
                return [.. response.Value.Data[0].Embedding];
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting embedding: {ex.Message}", ex);
            }
        }
    }
} 