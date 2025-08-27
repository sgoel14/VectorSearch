using TransactionLabeler.API.Models;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel.ChatCompletion;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    public class AzureAISearchChatHistoryService : IChatHistoryService
    {
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _chatMessagesIndex = "chat-messages-vector";
        private readonly string _chatSummariesIndex = "chat-summaries-vector";
        private readonly HttpClient _httpClient;
        private readonly IEmbeddingService _embeddingService;

        public AzureAISearchChatHistoryService(IConfiguration configuration, IEmbeddingService embeddingService)
        {
            _endpoint = configuration["AzureAISearch:Endpoint"] ?? throw new InvalidOperationException("Azure AI Search endpoint is missing");
            _apiKey = configuration["AzureAISearch:ApiKey"] ?? throw new InvalidOperationException("Azure AI Search API key is missing");
            
            _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
            
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);

            EnsureIndexesExistAsync().Wait(); // Ensure indexes are created on startup
        }

        private async Task EnsureIndexesExistAsync()
        {
            try
            {
                Console.WriteLine("🔍 Checking if Azure AI Search vector indexes exist...");
                
                var chatMessagesExists = await CheckIndexExistsAsync(_chatMessagesIndex);
                if (!chatMessagesExists)
                {
                    Console.WriteLine("📝 Creating chat-messages-vector index...");
                    await CreateChatMessagesVectorIndexAsync();
                }
                else
                {
                    Console.WriteLine("✅ chat-messages-vector index already exists");
                }

                var chatSummariesExists = await CheckIndexExistsAsync(_chatSummariesIndex);
                if (!chatSummariesExists)
                {
                    Console.WriteLine("📝 Creating chat-summaries-vector index...");
                    await CreateChatSummariesVectorIndexAsync();
                }
                else
                {
                    Console.WriteLine("✅ chat-summaries-vector index already exists");
                }

                Console.WriteLine("✅ All required vector indexes are ready!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error ensuring vector indexes exist: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> CheckIndexExistsAsync(string indexName)
        {
            try
            {
                var url = $"{_endpoint}/indexes/{indexName}?api-version=2024-07-01";
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task CreateChatMessagesVectorIndexAsync()
        {
            var indexDefinition = new
            {
                name = _chatMessagesIndex,
                fields = new object[]
                {
                    new { name = "id", type = "Edm.String", key = true, searchable = false, filterable = false, sortable = false, facetable = false, retrievable = true },
                    new { name = "sessionId", type = "Edm.String", key = false, searchable = true, filterable = true, sortable = false, facetable = false, retrievable = true },
                    new { name = "content", type = "Edm.String", key = false, searchable = true, filterable = false, sortable = false, facetable = false, retrievable = true },
                    new { name = "role", type = "Edm.String", key = false, searchable = true, filterable = true, sortable = false, facetable = false, retrievable = true },
                    new { name = "timestamp", type = "Edm.String", key = false, searchable = false, filterable = true, sortable = true, facetable = false, retrievable = true },
                    new { name = "contentVector", type = "Collection(Edm.Single)", searchable = true, filterable = false, sortable = false, facetable = false, retrievable = false, vectorSearchProfile = "myVectorSearchProfile", dimensions = 1536 }
                },
                vectorSearch = new
                {
                    algorithms = new object[]
                    {
                        new { name = "myVectorSearchProfile", kind = "hnsw" }
                    },
                    profiles = new object[]
                    {
                        new { name = "myVectorSearchProfile", algorithm = "myVectorSearchProfile" }
                    }
                }
            };

            var url = $"{_endpoint}/indexes/{_chatMessagesIndex}?api-version=2024-07-01";
            var content = new StringContent(JsonSerializer.Serialize(indexDefinition), Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to create chat-messages-vector index: {response.StatusCode} - {errorContent}");
            }

            Console.WriteLine("✅ chat-messages-vector index created successfully!");
        }

        private async Task CreateChatSummariesVectorIndexAsync()
        {
            var indexDefinition = new
            {
                name = _chatSummariesIndex,
                fields = new object[]
                {
                    new { name = "id", type = "Edm.String", key = true, searchable = false, filterable = false, sortable = false, facetable = false, retrievable = true },
                    new { name = "sessionId", type = "Edm.String", key = false, searchable = true, filterable = true, sortable = false, facetable = false, retrievable = true },
                    new { name = "content", type = "Edm.String", key = false, searchable = true, filterable = false, sortable = false, facetable = false, retrievable = true },
                    new { name = "type", type = "Edm.String", key = false, searchable = true, filterable = true, sortable = false, facetable = false, retrievable = true },
                    new { name = "timestamp", type = "Edm.String", key = false, searchable = false, filterable = true, sortable = true, facetable = false, retrievable = true },
                    new { name = "contentVector", type = "Collection(Edm.Single)", searchable = true, filterable = false, sortable = false, facetable = false, retrievable = false, vectorSearchProfile = "myVectorSearchProfile", dimensions = 1536 }
                },
                vectorSearch = new
                {
                    algorithms = new object[]
                    {
                        new { name = "myVectorSearchProfile", kind = "hnsw" }
                    },
                    profiles = new object[]
                    {
                        new { name = "myVectorSearchProfile", algorithm = "myVectorSearchProfile" }
                    }
                }
            };

            var url = $"{_endpoint}/indexes/{_chatSummariesIndex}?api-version=2024-07-01";
            var content = new StringContent(JsonSerializer.Serialize(indexDefinition), Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to create chat-summaries-vector index: {response.StatusCode} - {errorContent}");
            }

            Console.WriteLine("✅ chat-summaries-vector index created successfully!");
        }

        private async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                var embedding = await _embeddingService.GetEmbeddingAsync(text);
                return embedding;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting embedding: {ex.Message}");
            }
        }

        public async Task<List<ChatMessageInfo>> GetChatHistoryAsync(string sessionId)
        {
            try
            {
                Console.WriteLine($"🔍 Getting chat history for session {sessionId} from Azure AI Search vector index");
                
                // Escape single quotes in sessionId for OData filter
                var escapedSessionId = sessionId.Replace("'", "''");
                var searchQuery = new
                {
                    filter = $"sessionId eq '{escapedSessionId}'",
                    select = "id,content,role,timestamp,sessionId",
                    orderby = "timestamp asc",
                    top = 100
                };

                var searchUrl = $"{_endpoint}/indexes/{_chatMessagesIndex}/docs/search?api-version=2024-07-01";
                var searchContent = new StringContent(JsonSerializer.Serialize(searchQuery), Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(searchUrl, searchContent);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Azure AI Search query failed: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                    return new List<ChatMessageInfo>();
                }

                var searchResult = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"🔍 Raw Azure AI Search response: {searchResult}");
                
                var searchResponse = JsonSerializer.Deserialize<AzureSearchResponse>(searchResult);
                Console.WriteLine($"🔍 Deserialized response: Value count = {searchResponse?.Value?.Count ?? 0}");
                
                var messages = new List<ChatMessageInfo>();
                if (searchResponse?.Value != null)
                {
                    Console.WriteLine($"🔍 Processing {searchResponse.Value.Count} documents...");
                    foreach (var doc in searchResponse.Value)
                    {
                        try
                        {
                            Console.WriteLine($"🔍 Processing document: id={doc.Id}, role={doc.Role}, timestamp={doc.Timestamp}");
                            if (DateTime.TryParse(doc.Timestamp, out var timestamp))
                            {
                                // Map the stored role string to AuthorRole enum
                                AuthorRole role;
                                switch (doc.Role.ToLower())
                                {
                                    case "user":
                                        role = AuthorRole.User;
                                        break;
                                    case "assistant":
                                        role = AuthorRole.Assistant;
                                        break;
                                    case "system":
                                        role = AuthorRole.System;
                                        break;
                                    default:
                                        Console.WriteLine($"⚠️ Unknown role '{doc.Role}', defaulting to User");
                                        role = AuthorRole.User;
                                        break;
                                }
                                
                                var message = new ChatMessageInfo(role, doc.Content) { Timestamp = timestamp };
                                messages.Add(message);
                                Console.WriteLine($"✅ Successfully parsed message: {role} - {doc.Content.Substring(0, Math.Min(50, doc.Content.Length))}...");
                            }
                            else
                            {
                                Console.WriteLine($"⚠️ Could not parse timestamp for document {doc.Id}: {doc.Timestamp}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ Error parsing document {doc.Id}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ searchResponse.Value is null or empty");
                }

                var orderedMessages = messages.OrderBy(m => m.Timestamp).ToList();
                Console.WriteLine($"✅ Retrieved {orderedMessages.Count} messages for session {sessionId}");
                return orderedMessages;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error retrieving chat history: {ex.Message}");
                return new List<ChatMessageInfo>();
            }
        }

        public async Task AddMessageAsync(string sessionId, ChatMessageInfo message)
        {
            try
            {
                Console.WriteLine($"✅ Storing message in Azure AI Search vector index for session {sessionId}");
                
                // Get embedding for the message content
                var embedding = await GetEmbeddingAsync(message.Content);
                
                // Create document for Azure AI Search
                var document = new
                {
                    id = $"{sessionId}_{message.Timestamp:yyyyMMddHHmmss}_{Guid.NewGuid():N}",
                    sessionId = sessionId,
                    content = message.Content,
                    role = message.Role.ToString(),
                    timestamp = message.Timestamp.ToString("O"),
                    contentVector = embedding
                };

                //Console.WriteLine($"📝 Document to index: {JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true })}");

                // Index the document
                var indexUrl = $"{_endpoint}/indexes/{_chatMessagesIndex}/docs/index?api-version=2024-07-01";
                var indexRequest = new
                {
                    value = new[] { document }
                };

                var indexContent = new StringContent(JsonSerializer.Serialize(indexRequest), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(indexUrl, indexContent);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Failed to index message: {response.StatusCode} - {errorContent}");
                    throw new Exception($"Azure AI Search indexing failed: {response.StatusCode}");
                }

                Console.WriteLine($"✅ Message successfully stored in Azure AI Search vector index for session {sessionId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error storing message: {ex.Message}");
                throw;
            }
        }

        public async Task ClearChatHistoryAsync(string sessionId)
        {
            try
            {
                Console.WriteLine($"🗑️ Clearing chat history for session {sessionId} in Azure AI Search vector index");
                
                // First, get all documents for the session
                var messages = await GetChatHistoryAsync(sessionId);
                if (!messages.Any())
                {
                    Console.WriteLine($"ℹ️ No messages found for session {sessionId}");
                    return;
                }

                // Delete each message document
                var deleteUrl = $"{_endpoint}/indexes/{_chatMessagesIndex}/docs/index?api-version=2024-07-01";
                var deleteDocuments = messages.Select(m => new
                {
                    id = $"{sessionId}_{m.Timestamp:yyyyMMddHHmmss}_{Guid.NewGuid():N}",
                    searchAction = "delete"
                }).ToArray();

                var deleteRequest = new
                {
                    value = deleteDocuments
                };

                var deleteContent = new StringContent(JsonSerializer.Serialize(deleteRequest), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(deleteUrl, deleteContent);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Failed to delete messages: {response.StatusCode} - {errorContent}");
                    throw new Exception($"Azure AI Search deletion failed: {response.StatusCode}");
                }

                // Also clear the summary
                await ClearContextSummaryAsync(sessionId);
                
                Console.WriteLine($"✅ Successfully cleared {messages.Count} messages for session {sessionId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error clearing chat history: {ex.Message}");
                throw;
            }
        }

        public async Task<List<ChatMessageInfo>> SearchSimilarMessagesAsync(string sessionId, string query, int maxResults = 5)
        {
            try
            {
                Console.WriteLine($"🔍 Searching similar messages for session {sessionId} with query: {query}");
                
                // Get embedding for the search query
                var queryEmbedding = await GetEmbeddingAsync(query);
                
                // Escape single quotes in sessionId for OData filter
                var escapedSessionId = sessionId.Replace("'", "''");
                
                // Create vector search query
                var searchQuery = new
                {
                    vectorQueries = new[]
                    {
                        new
                        {
                            vector = queryEmbedding,
                            kNearestNeighborsCount = maxResults,
                            fields = "contentVector"
                        }
                    },
                    filter = $"sessionId eq '{escapedSessionId}'",
                    select = "id,content,role,timestamp,sessionId",
                    top = maxResults,
                    orderby = "timestamp desc"
                };

                var searchUrl = $"{_endpoint}/indexes/{_chatMessagesIndex}/docs/search?api-version=2024-07-01";
                var searchContent = new StringContent(JsonSerializer.Serialize(searchQuery), Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(searchUrl, searchContent);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Azure AI Search similar messages query failed: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                    return new List<ChatMessageInfo>();
                }

                var searchResult = await response.Content.ReadAsStringAsync();
                var searchResponse = JsonSerializer.Deserialize<AzureSearchResponse>(searchResult);
                
                var similarMessages = new List<ChatMessageInfo>();
                if (searchResponse?.Value != null)
                {
                    foreach (var doc in searchResponse.Value)
                    {
                        if (DateTime.TryParse(doc.Timestamp, out var timestamp))
                        {
                            // Map the stored role string to AuthorRole enum
                            AuthorRole role;
                            switch (doc.Role.ToLower())
                            {
                                case "user":
                                    role = AuthorRole.User;
                                    break;
                                case "assistant":
                                    role = AuthorRole.Assistant;
                                    break;
                                case "system":
                                    role = AuthorRole.System;
                                    break;
                                default:
                                    role = AuthorRole.User;
                                    break;
                            }
                            
                            var message = new ChatMessageInfo(role, doc.Content) { Timestamp = timestamp };
                            similarMessages.Add(message);
                        }
                    }
                }

                Console.WriteLine($"✅ Found {similarMessages.Count} similar messages for query: {query}");
                return similarMessages;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error searching similar messages: {ex.Message}");
                return new List<ChatMessageInfo>();
            }
        }

        public async Task<string> GetContextSummaryAsync(string sessionId)
        {
            try
            {
                Console.WriteLine($"🔍 Getting context summary for session {sessionId} from Azure AI Search vector index");
                
                // Escape single quotes in sessionId for OData filter
                var escapedSessionId = sessionId.Replace("'", "''");
                var searchQuery = new
                {
                    filter = $"(sessionId eq '{escapedSessionId}') and (type eq 'summary')",
                    select = "id,content,timestamp",
                    top = 1,
                    orderby = "timestamp desc"
                };

                var searchUrl = $"{_endpoint}/indexes/{_chatSummariesIndex}/docs/search?api-version=2024-07-01";
                var searchContent = new StringContent(JsonSerializer.Serialize(searchQuery), Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(searchUrl, searchContent);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Summary search failed: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                    return string.Empty;
                }

                var searchResult = await response.Content.ReadAsStringAsync();
                var searchResponse = JsonSerializer.Deserialize<AzureSearchResponse>(searchResult);
                
                if (searchResponse?.Value?.Any() == true)
                {
                    var summary = searchResponse.Value.First().Content;
                    Console.WriteLine($"✅ Retrieved context summary for session {sessionId}");
                    return summary ?? string.Empty;
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error retrieving context summary: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task UpdateContextSummaryAsync(string sessionId, string summary)
        {
            try
            {
                Console.WriteLine($"✅ Updating context summary for session {sessionId} in Azure AI Search vector index");
                
                // Get embedding for the summary
                var embedding = await GetEmbeddingAsync(summary);
                
                // Create or update summary document
                var document = new
                {
                    id = $"summary_{sessionId}",
                    sessionId = sessionId,
                    content = summary,
                    type = "summary",
                    timestamp = DateTime.UtcNow.ToString("O"),
                    contentVector = embedding
                };

                // Index the summary document
                var indexUrl = $"{_endpoint}/indexes/{_chatSummariesIndex}/docs/index?api-version=2024-07-01";
                var indexRequest = new
                {
                    value = new[] { document }
                };

                var indexContent = new StringContent(JsonSerializer.Serialize(indexRequest), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(indexUrl, indexContent);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Failed to index summary: {response.StatusCode} - {errorContent}");
                    throw new Exception($"Azure AI Search summary indexing failed: {response.StatusCode}");
                }

                Console.WriteLine($"✅ Context summary successfully updated for session {sessionId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error updating context summary: {ex.Message}");
                throw;
            }
        }

        private async Task ClearContextSummaryAsync(string sessionId)
        {
            try
            {
                var deleteUrl = $"{_endpoint}/indexes/{_chatSummariesIndex}/docs/index?api-version=2024-07-01";
                var deleteDocument = new
                {
                    id = $"summary_{sessionId}",
                    searchAction = "delete"
                };

                var deleteRequest = new
                {
                    value = new[] { deleteDocument }
                };

                var deleteContent = new StringContent(JsonSerializer.Serialize(deleteRequest), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(deleteUrl, deleteContent);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"⚠️ Failed to delete summary for session {sessionId}: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error clearing context summary: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // Azure Search response models
    public class AzureSearchResponse
    {
        [JsonPropertyName("value")]
        public List<AzureSearchDocument> Value { get; set; } = new();
    }

    public class AzureSearchDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
        
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;
        
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;
        
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;
        
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        
        // Azure AI Search metadata fields (ignored during deserialization)
        [JsonPropertyName("@search.score")]
        public double? SearchScore { get; set; }
        
        [JsonPropertyName("@odata.context")]
        public string? ODataContext { get; set; }
    }


}
