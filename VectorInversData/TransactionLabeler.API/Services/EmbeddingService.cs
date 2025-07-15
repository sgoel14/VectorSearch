using System;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using System.ClientModel;

namespace TransactionLabeler.API.Services
{
    public interface IEmbeddingService
    {
        Task<float[]> GetEmbeddingAsync(string text);
    }

    public class EmbeddingService : IEmbeddingService
    {
        private readonly AzureOpenAIClient _client;
        private readonly string _deploymentName;

        public EmbeddingService(IConfiguration configuration)
        {
            var endpoint = configuration["AzureOpenAI:Endpoint"];
            var key = configuration["AzureOpenAI:Key"];
            _deploymentName = configuration["AzureOpenAI:EmbeddingDeploymentName"];

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(_deploymentName))
            {
                throw new ArgumentException("Azure OpenAI configuration is missing. Please check your appsettings.json file.");
            }

            _client = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(key));
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                var embeddingClient = _client.GetEmbeddingClient(_deploymentName);
                var response = await embeddingClient.GenerateEmbeddingAsync(text);
                return response.Value.ToFloats().ToArray();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting embedding: {ex.Message}", ex);
            }
        }
    }
}      