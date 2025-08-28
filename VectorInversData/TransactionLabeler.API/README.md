# Transaction Labeler API - Intelligent Financial Analysis System

## 🎯 Project Overview

The Transaction Labeler API is an intelligent financial analysis system that combines Azure AI services, vector search capabilities, and semantic understanding to provide advanced transaction analysis and categorization. The system automatically processes bank transactions, generates embeddings for semantic search, and provides intelligent querying capabilities through natural language.

## 🏗️ System Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Transaction Labeler API                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────────────┐ │
│  │   Web Frontend  │    │   API Layer     │    │   Business Logic       │ │
│  │                 │    │                 │    │                         │ │
│  │ • HTML/CSS/JS   │◄──►│ • Controllers   │◄──►│ • Semantic Kernel      │ │
│  │ • Chat Interface│    │ • Endpoints     │    │ • Financial Tools      │ │
│  │ • Vector Search │    │ • Middleware    │    │ • Context Management   │ │
│  └─────────────────┘    └─────────────────┘    └─────────────────────────┘ │
│                                    │                                        │
│  ┌─────────────────────────────────┼─────────────────────────────────────┐ │
│  │                                 │                                     │ │
│  │  ┌─────────────────────────────┼─────────────────────────────────────┐│ │
│  │  │        Data Layer           │                                     ││ │
│  │  │                             │                                     ││ │
│  │  │  ┌─────────────────────┐    │  ┌─────────────────────────────┐    ││ │
│  │  │  │   Azure SQL DB      │    │  │     Azure AI Search         │    ││ │
│  │  │  │                     │    │  │                             │    ││ │
│  │  │  │ • Transactions      │    │  │ • Chat History (Vector)     │    ││ │
│  │  │  │ • RGS Mappings      │    │  │ • Context Summaries         │    ││ │
│  │  │  │ • Embeddings        │    │  │ • Semantic Search           │    ││ │
│  │  │  │ • Vector Search     │    │  │ • HNSW Algorithm            │    ││ │
│  │  └──┴─────────────────────┴────┴──┴─────────────────────────────┴────┘│ │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │                        External AI Services                            │ │
│  │                                                                         │ │
│  │  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────────┐ │ │
│  │  │ Azure OpenAI    │    │ Semantic Kernel │    │ Vector Embeddings   │ │
│  │  │                 │    │                 │    │                     │ │
│  │  │ • GPT-4o        │◄──►│ • AI Orchestration│◄──►│ • text-embedding-ada│ │
│  │  │ • Chat Completions│  │ • Function Calling│  │ • 1536 Dimensions   │ │
│  │  │ • Embeddings    │    │ • Context Management│ │ • Cosine Similarity │ │
│  └──┴─────────────────┴────┴─────────────────┴────┴─────────────────────┴─┘ │
└─────────────────────────────────────────────────────────────────────────────┘
```

## 🚀 Key Features

### 1. **Intelligent Transaction Analysis**
- Automatic transaction categorization using RGS codes
- Semantic search across transaction descriptions
- Vector-based similarity matching
- Natural language query processing

### 2. **Advanced Chat System**
- Persistent chat history with Azure AI Search
- Vector-enabled context retrieval
- Automatic context summarization
- Semantic relevance filtering

### 3. **Financial Tools & Functions**
- Top expense category analysis
- Transaction search by category
- Custom SQL query execution
- Spending pattern analysis
- Customer-specific reporting

### 4. **Vector Search Capabilities**
- HNSW algorithm for fast similarity search
- 1536-dimensional embeddings
- Cosine similarity scoring
- Real-time semantic matching

## 🛠️ Technology Stack

### **Backend Framework**
- **.NET 8** - Modern, high-performance framework
- **ASP.NET Core** - Web API framework
- **Entity Framework Core** - ORM for database operations

### **AI & Machine Learning**
- **Azure OpenAI** - GPT-4o for chat completions
- **Semantic Kernel** - AI orchestration framework
- **Vector Embeddings** - text-embedding-ada-002 model

### **Database & Search**
- **Azure SQL Database** - Primary data storage
- **Azure AI Search** - Vector search and chat history
- **Vector Search** - Native SQL Server vector operations

### **Cloud Services**
- **Azure OpenAI** - AI model hosting
- **Azure AI Search** - Search service with vector capabilities
- **Azure SQL** - Managed database service

## 📁 Project Structure

```
TransactionLabeler.API/
├── Controllers/                    # API endpoints
│   └── TransactionsController.cs   # Main API controller
├── Data/                          # Database context and models
│   ├── ApplicationDbContext.cs    # EF Core context
│   └── SampleDataSeeder.cs        # Initial data seeding
├── Models/                        # Data models and DTOs
│   ├── Transaction models         # Core business entities
│   ├── Chat models               # Chat system models
│   └── Azure Search models       # Search response models
├── Services/                      # Business logic services
│   ├── SemanticKernelService.cs  # AI orchestration
│   ├── FinancialTools.cs         # Financial analysis tools
│   ├── TransactionService.cs     # Data access layer
│   ├── EmbeddingService.cs       # Vector embedding service
│   └── AzureAISearchChatHistoryService.cs # Chat history with vectors
├── wwwroot/                      # Static web assets
│   ├── intelligent-chat.html     # Main chat interface
│   ├── vector-search.html        # Vector search interface
│   ├── app.js                    # Frontend JavaScript
│   └── styles.css                # Styling
├── Program.cs                     # Application entry point
├── appsettings.json              # Configuration
└── README.md                     # This documentation
```

## 🔧 Core Components

### 1. **SemanticKernelService** - AI Orchestration Engine

The `SemanticKernelService` is the heart of the system, orchestrating AI interactions and managing context.

**Key Responsibilities:**
- Processes natural language queries
- Manages chat history and context
- Orchestrates function calling
- Handles automatic context summarization

**Key Methods:**
```csharp
// Main query processing with AI intelligence
ProcessIntelligentQueryWithAdvancedFeaturesAsync()

// Context-aware question reframing
ReframeQuestionWithContextAsync()

// Automatic summary creation every 5 messages
CheckAndCreateAutomaticSummaryAsync()
```

**Context Management:**
- Automatically creates context summaries every 5 messages
- Uses vector search to find semantically relevant chat history
- Maintains conversation continuity across sessions

### 2. **FinancialTools** - AI-Powered Financial Analysis

The `FinancialTools` class provides Semantic Kernel functions for financial analysis.

**Available Functions:**
```csharp
// Get top expense categories
GetTopExpenseCategoriesFlexible()

// Find transactions by category
GetTopTransactionsForCategory()

// Search categories semantically
SearchCategories()

// Calculate category spending
GetCategorySpending()

// Execute custom SQL queries
ExecuteReadOnlySQLQuery()
```

**Security Features:**
- Only allows SELECT queries
- Blocks dangerous SQL keywords
- Parameterized query support
- Read-only database access

### 3. **AzureAISearchChatHistoryService** - Vector-Enabled Chat Storage

This service provides persistent, vector-enabled chat history storage using Azure AI Search.

**Indexes:**
- **chat-messages-vector**: Stores individual chat messages with embeddings
- **chat-summaries-vector**: Stores context summaries with embeddings

**Vector Search Features:**
- HNSW algorithm for fast similarity search
- 1536-dimensional embeddings
- Cosine similarity scoring
- Automatic relevance filtering (70%+ similarity threshold)

**Key Methods:**
```csharp
// Store messages with vector embeddings
AddMessageAsync()

// Retrieve semantically relevant chat history
GetRelevantChatHistoryAsync()

// Vector-based context summary retrieval
GetRelevantContextSummaryAsync()
```

### 4. **TransactionService** - Data Access Layer

The `TransactionService` handles all database operations and provides the data layer for financial analysis.

**Core Capabilities:**
- Vector search in SQL Server
- Category-based transaction retrieval
- Spending analysis and aggregation
- Custom SQL query execution

**Vector Search Implementation:**
```csharp
// Vector similarity search using SQL Server
VectorSearchInSqlAsync()

// Category search with vector embeddings
SearchCategoriesByVectorAsync()

// Transaction retrieval by category
GetTopTransactionsForCategoryQueryAsync()
```

## 🔄 Data Flow

### 1. **Query Processing Flow**
```
User Query → Semantic Kernel → Context Analysis → Tool Selection → Data Retrieval → Response Generation
```

### 2. **Vector Search Flow**
```
Text Input → Embedding Generation → Vector Search → Similarity Scoring → Result Filtering → Response
```

### 3. **Chat History Flow**
```
Message → Embedding → Azure AI Search → Vector Index → Retrieval → Context Building → AI Response
```

## 🚀 Getting Started

### Prerequisites
- .NET 8 SDK
- Azure subscription with:
  - Azure OpenAI service
  - Azure AI Search service
  - Azure SQL Database

### Configuration
Update `appsettings.json` with your Azure service credentials:

```json
{
  "AzureOpenAI": {
    "Endpoint": "your-openai-endpoint",
    "Key": "your-openai-key",
    "ChatDeploymentName": "gpt-4o",
    "EmbeddingDeploymentName": "text-embedding-ada-002"
  },
  "AzureAISearch": {
    "Endpoint": "your-search-endpoint",
    "ApiKey": "your-search-key"
  },
  "ConnectionStrings": {
    "DefaultConnection": "your-sql-connection-string"
  }
}
```

### Running the Application
```bash
cd TransactionLabeler.API
dotnet restore
dotnet run
```

The application will:
1. Create necessary Azure AI Search indexes
2. Seed sample data (if database is empty)
3. Start the web API server
4. Initialize the chat interface

## 📊 API Endpoints

### Chat & AI
- `POST /api/transactions/chat` - Process AI queries
- `GET /api/transactions/chat-history/{sessionId}` - Get chat history
- `DELETE /api/transactions/chat-history/{sessionId}` - Clear chat history
- `POST /api/transactions/create-summary/{sessionId}` - Create manual summary
- `GET /api/transactions/summary-stats/{sessionId}` - Get summary statistics

### Financial Analysis
- `GET /api/transactions/categories` - Get expense categories
- `GET /api/transactions/transactions` - Get transactions
- `GET /api/transactions/spending` - Get spending analysis

## 🔍 Vector Search Implementation

### Index Configuration
```json
{
  "vectorSearch": {
    "algorithms": [
      {
        "name": "myVectorSearchProfile",
        "kind": "hnsw"
      }
    ],
    "profiles": [
      {
        "name": "myVectorSearchProfile",
        "algorithm": "myVectorSearchProfile"
      }
    ]
  }
}
```

### Vector Field Definition
```json
{
  "name": "contentVector",
  "type": "Collection(Edm.Single)",
  "searchable": true,
  "vectorSearchProfile": "myVectorSearchProfile",
  "dimensions": 1536
}
```

### Search Query Structure
```json
{
  "vectorQueries": [
    {
      "vector": [0.1, 0.2, ...],
      "k": 20,
      "fields": "contentVector"
    }
  ],
  "filter": "sessionId eq 'session_123'",
  "select": "id,content,role,timestamp,sessionId"
}
```

## 🧠 AI Intelligence Features

### 1. **Context-Aware Question Reframing**
The system automatically reframes incomplete questions using chat history context:
- "check in 2024" → "check transactions for Nova Creations in 2024"
- "car repair" → "show me car repair transactions for Nova Creations"

### 2. **Automatic Context Summarization**
- Creates summaries every 5 messages automatically
- Stores summaries with vector embeddings for semantic retrieval
- Provides long-term conversation memory

### 3. **Intelligent Tool Selection**
- Automatically chooses appropriate financial tools
- Handles both financial and general knowledge queries
- Provides comprehensive responses for non-financial questions

### 4. **Semantic Understanding**
- Uses vector embeddings for semantic similarity
- Understands context across conversation turns
- Maintains conversation continuity

## 🔒 Security Features

### SQL Injection Prevention
- Only SELECT queries allowed
- Dangerous keywords blocked
- Parameterized queries used
- Input validation and sanitization

### API Security
- CORS configuration
- Input validation
- Error handling without information leakage
- Secure connection strings

## 📈 Performance Optimizations

### 1. **Vector Search Optimization**
- HNSW algorithm for fast similarity search
- Batch processing for embeddings
- Connection pooling for database operations
- Async/await throughout the stack

### 2. **Caching & Memory Management**
- Azure AI Search for persistent storage
- Efficient context building
- Smart message truncation
- Background summary generation

### 3. **Database Optimization**
- Indexed vector fields
- Efficient SQL queries
- Connection timeout management
- Batch operations for large datasets

## 🧪 Testing & Debugging

### Console Logging
The system provides extensive console logging for debugging:
- Vector search operations
- AI processing steps
- Database operations
- Error handling

### Monitoring Endpoints
- Summary statistics
- Chat history status
- Vector search performance
- Error tracking

## 🚧 Known Limitations

### 1. **API Version Compatibility**
- Azure AI Search API version 2024-07-01 required
- Some vector search features may vary by region
- Semantic Kernel preview packages may have breaking changes

### 2. **Performance Considerations**
- Vector search requires sufficient compute resources
- Large embedding datasets may impact search speed
- Real-time embedding generation adds latency

### 3. **Cost Considerations**
- Azure OpenAI API calls for each query
- Azure AI Search service costs
- Database compute units for vector operations

## 🔮 Future Enhancements

### 1. **Advanced Analytics**
- Machine learning for transaction categorization
- Predictive spending analysis
- Anomaly detection
- Trend analysis and forecasting

### 2. **Enhanced Vector Search**
- Multi-modal embeddings (text + numerical)
- Hierarchical vector search
- Dynamic similarity thresholds
- Personalized search profiles

### 3. **Integration Capabilities**
- Real-time transaction feeds
- Third-party financial APIs
- Export and reporting features
- Mobile application support

## 📚 Additional Resources

### Documentation
- [Azure AI Search Documentation](https://docs.microsoft.com/en-us/azure/search/)
- [Semantic Kernel Documentation](https://learn.microsoft.com/en-us/semantic-kernel/)
- [Azure OpenAI Documentation](https://docs.microsoft.com/en-us/azure/cognitive-services/openai/)

### Code Examples
- Vector search implementation
- AI function calling patterns
- Context management strategies
- Error handling best practices

## 🤝 Contributing

This project demonstrates advanced AI integration patterns and can serve as a reference for:
- Azure AI Search vector implementations
- Semantic Kernel orchestration
- Financial data analysis systems
- Vector-enabled chat applications

## 📄 License

This project is provided as a reference implementation for educational and development purposes.

---

**Built with ❤️ using .NET 8, Azure AI Services, and Semantic Kernel**
