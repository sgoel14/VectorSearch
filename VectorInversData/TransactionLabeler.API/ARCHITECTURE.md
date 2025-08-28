# Transaction Labeler API - Technical Architecture

## 🏗️ System Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Transaction Labeler API                                      │
│                                    (ASP.NET Core Web API)                                      │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                        API Layer                                               │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │ TransactionsController│  │   Middleware       │  │   CORS Policy      │  │   Swagger UI    │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • Chat Endpoints    │  │ • Exception Handling│  │ • Cross-Origin     │  │ • API Docs      │ │
│  │ • Financial APIs    │  │ • Request Logging   │  │ • Request Handling │  │ • Testing       │ │
│  │ • Vector Search     │  │ • Authentication    │  │ • Response Headers │  │ • Endpoints     │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Business Logic Layer                                        │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │ SemanticKernelService│  │   FinancialTools   │  │ TransactionService │  │ EmbeddingService│ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • AI Orchestration  │  │ • Financial Analysis│  │ • Data Access      │  │ • Vector        │ │
│  │ • Context Management│  │ • Function Calling  │  │ • Vector Search    │  │   Generation    │ │
│  │ • Chat Processing   │  │ • SQL Execution     │  │ • Business Logic   │  │ • OpenAI Client │ │
│  │ • Auto Summarization│  │ • Security Checks   │  │ • Aggregations     │  │ • 1536 Dims     │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Data Access Layer                                           │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │ ApplicationDbContext│  │ AzureAISearchChat   │  │   SqlConnection     │  │   Entity        │ │
│  │                     │  │ HistoryService      │  │                     │  │   Framework     │ │
│  │ • EF Core Context  │  │ • Vector Indexes    │  │ • Direct SQL        │  │ • Model Mapping │ │
│  │ • Model Validation │  │ • Chat Storage      │  │ • Vector Operations │  │ • Relationships │ │
│  │ • Migration Support│  │ • Context Summaries │  │ • Batch Processing  │  │ • Change Tracking│ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    External Services                                           │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Azure OpenAI      │  │   Azure AI Search   │  │   Azure SQL DB      │  │   Semantic      │ │
│  │                     │  │                     │  │                     │  │   Kernel        │ │
│  │ • GPT-4o Model     │  │ • Vector Indexes    │  │ • Transaction Data  │  │ • AI Framework  │ │
│  │ • Embedding Model  │  │ • HNSW Algorithm    │  │ • RGS Mappings      │  │ • Function      │ │
│  │ • Chat Completions │  │ • Cosine Similarity │  │ • Vector Fields     │  │   Calling       │ │
│  │ • 1536 Dim Vectors│  │ • Real-time Search  │  │ • Stored Procedures │  │ • Context Mgmt  │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 🔄 Data Flow Architecture

### 1. **User Query Processing Flow**

```
┌─────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   User     │───►│  API Controller │───►│ Semantic Kernel │───►│  AI Processing  │
│   Query    │    │                 │    │                 │    │                 │
└─────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘
                                                      │
                                                      ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Context       │◄───│  Vector Search  │◄───│  Embedding      │◄───│  Tool Selection │
│  Building      │    │  (Azure Search) │    │  Generation     │    │  & Execution    │
└─────────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘
         │
         ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Response      │◄───│  Data           │◄───│  Financial      │
│  Generation    │    │  Aggregation    │    │  Tools          │
└─────────────────┘    └─────────────────┘    └─────────────────┘
```

### 2. **Vector Search Flow**

```
┌─────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Text     │───►│  OpenAI         │───►│  Vector         │───►│  Azure AI       │
│   Input    │    │  Embedding      │    │  Generation     │    │  Search         │
└─────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘
                                                      │
                                                      ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Similarity     │◄───│  HNSW          │◄───│  Vector         │◄───│  Index          │
│  Scoring        │    │  Algorithm     │    │  Query          │    │  Search         │
└─────────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘
         │
         ▼
┌─────────────────┐    ┌─────────────────┐
│  Relevance      │───►│  Result         │
│  Filtering      │    │  Ranking        │
└─────────────────┘    └─────────────────┘
```

### 3. **Chat History Management Flow**

```
┌─────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Chat     │───►│  Message        │───►│  Embedding      │───►│  Azure AI       │
│   Message  │    │  Processing     │    │  Generation     │    │  Search Index   │
└─────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘
                                                      │
                                                      ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Context       │◄───│  Vector         │◄───│  Semantic      │◄───│  Summary        │
│  Building      │    │  Retrieval      │    │  Search         │    │  Generation     │
└─────────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘
         │
         ▼
┌─────────────────┐    ┌─────────────────┐
│  AI Response    │◄───│  Context       │
│  Generation     │    │  Integration    │
└─────────────────┘    └─────────────────┘
```

## 🗄️ Database Architecture

### **Azure SQL Database Schema**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Azure SQL Database                                          │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────────────────────────────────────────────────────────────────────────────┐ │
│  │                              inversbanktransaction                                        │ │
│  ├─────────────────────────────────────────────────────────────────────────────────────────────┤ │
│  │  Id (Guid, PK)                    │  Description (nvarchar)                               │ │
│  │  Amount (decimal)                 │  TransactionDate (datetime)                           │ │
│  │  BankAccountName (nvarchar)       │  BankAccountNumber (nvarchar)                          │ │
│  │  CustomerName (nvarchar)          │  RgsCode (nvarchar)                                    │ │
│  │  TransactionType (nvarchar)       │  CategoryEmbedding (vector)                            │ │
│  │  af_bij (nvarchar)                │  TransactionIdentifier_AccountNumber (nvarchar)        │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                                 │
│  ┌─────────────────────────────────────────────────────────────────────────────────────────────┐ │
│  │                                    rgsmapping                                             │ │
│  ├─────────────────────────────────────────────────────────────────────────────────────────────┤ │
│  │  RgsCode (nvarchar, PK)           │  RgsDescription (nvarchar)                             │ │
│  │  RgsShortDescription (nvarchar)   │                                                       │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                                 │
│  ┌─────────────────────────────────────────────────────────────────────────────────────────────┐ │
│  │                              persistentbankstatementline                                   │ │
│  ├─────────────────────────────────────────────────────────────────────────────────────────────┤ │
│  │  Id (Guid, PK)                    │  Description (nvarchar)                               │ │
│  │  Amount (decimal)                 │  TransactionDate (datetime)                           │ │
│  │  BankAccountName (nvarchar)       │  BankAccountNumber (nvarchar)                          │ │
│  │  TransactionType (nvarchar)       │  Embedding (vector)                                    │ │
│  │  RgsCode (nvarchar)               │  RgsDescription (nvarchar)                             │ │
│  │  RgsShortDescription (nvarchar)   │                                                       │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### **Azure AI Search Indexes**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Azure AI Search                                             │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────────────────────────────────────────────────────────────────────────────┐ │
│  │                              chat-messages-vector                                         │ │
│  ├─────────────────────────────────────────────────────────────────────────────────────────────┤ │
│  │  id (Edm.String, Key)           │  sessionId (Edm.String, Filterable)                      │ │
│  │  content (Edm.String, Searchable)│  role (Edm.String, Filterable)                          │ │
│  │  timestamp (Edm.String, Sortable)│  contentVector (Collection(Edm.Single), Vector)          │ │
│  │  vectorSearchProfile: "myVectorSearchProfile"                                              │ │
│  │  dimensions: 1536                                                                           │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                                 │
│  ┌─────────────────────────────────────────────────────────────────────────────────────────────┐ │
│  │                              chat-summaries-vector                                         │ │
│  ├─────────────────────────────────────────────────────────────────────────────────────────────┤ │
│  │  id (Edm.String, Key)           │  sessionId (Edm.String, Filterable)                      │ │
│  │  content (Edm.String, Searchable)│  type (Edm.String, Filterable)                          │ │
│  │  timestamp (Edm.String, Sortable)│  contentVector (Collection(Edm.Single), Vector)          │ │
│  │  vectorSearchProfile: "myVectorSearchProfile"                                              │ │
│  │  dimensions: 1536                                                                           │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                                 │
│  ┌─────────────────────────────────────────────────────────────────────────────────────────────┐ │
│  │                              Vector Search Configuration                                   │ │
│  ├─────────────────────────────────────────────────────────────────────────────────────────────┤ │
│  │  Algorithm: HNSW (Hierarchical Navigable Small World)                                     │ │
│  │  Similarity: Cosine                                                                        │
│  │  Dimensions: 1536                                                                          │
│  │  Profile: myVectorSearchProfile                                                            │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 🔧 Service Architecture

### **Dependency Injection Container**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Service Container                                           │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │ IEmbeddingService   │  │ ITransactionService │  │ IChatHistoryService │  │ ISemanticKernel │ │
│  │                     │  │                     │  │                     │  │ Service        │ │
│  │ • GetEmbeddingAsync │  │ • Vector Search     │  │ • Chat Storage      │  │ • AI Processing │ │
│  │ • OpenAI Client     │  │ • Business Logic    │  │ • Vector Indexes    │  │ • Context Mgmt  │ │
│  │ • 1536 Dimensions  │  │ • Data Access       │  │ • Context Summaries │  │ • Tool Calling  │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
│           │                        │                        │                        │          │
│           ▼                        ▼                        ▼                        ▼          │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │ EmbeddingService    │  │ TransactionService  │  │ AzureAISearchChat  │  │ SemanticKernel │ │
│  │                     │  │                     │  │ HistoryService      │  │ Service        │ │
│  │ • Azure OpenAI      │  │ • EF Core Context  │  │ • Azure Search      │  │ • Semantic     │ │
│  │ • Embedding Model   │  │ • SQL Operations   │  │ • Vector Storage    │  │   Kernel       │ │
│  │ • Vector Generation │  │ • Vector Search    │  │ • HNSW Algorithm    │  │ • AI Functions │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### **Service Lifetime Management**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Service Lifetimes                                          │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Singleton         │  │   Singleton         │  │   Singleton         │  │   Singleton     │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • EmbeddingService  │  │ • TransactionService│  │ • ChatHistoryService│  │ • SemanticKernel│ │
│  │ • OpenAI Client     │  │ • EF Core Context  │  │ • Azure Search      │  │ • AI Kernel     │ │
│  │ • Persistent        │  │ • Database          │  │ • Vector Indexes    │  │ • Function      │ │
│  │   Connections       │  │   Connections       │  │ • Chat Storage      │  │   Registry      │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Scoped            │  │   Transient         │  │   Transient         │  │   Transient     │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • DbContext         │  │ • FinancialTools    │  │ • Controllers       │  │ • HTTP Clients  │ │
│  │ • Request Scope     │  │ • Per Request       │  │ • Per Request       │  │ • Per Request   │ │
│  │ • Unit of Work      │  │ • Stateless         │  │ • Stateless         │  │ • Stateless     │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 🚀 Performance Architecture

### **Caching Strategy**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Caching Layers                                             │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   L1 Cache          │  │   L2 Cache          │  │   L3 Cache          │  │   L4 Cache      │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • In-Memory         │  │ • Azure AI Search   │  │ • Database          │  │ • File System   │ │
│  │ • Chat History      │  │ • Vector Indexes    │  │ • Stored Procedures │  │ • Static Assets │ │
│  │ • Context Summaries │  │ • Semantic Cache    │  │ • Query Results     │  │ • Configuration │ │
│  │ • Fast Access       │  │ • Relevance Scores  │  │ • Connection Pool   │  │ • Logs          │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### **Async Processing Pipeline**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Async Pipeline                                              │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐          │
│  │   HTTP     │───►│  Middleware     │───►│  Controller     │───►│  Service Layer  │          │
│  │   Request  │    │                 │    │                 │    │                 │          │
│  └─────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘          │
│                                                                                                 │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐      │
│  │  Database       │◄───│  Vector Search  │◄───│  Embedding      │◄───│  AI Processing  │      │
│  │  Operations     │    │  Operations     │    │  Generation     │    │  Operations     │      │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘      │
│                                                                                                 │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐      │
│  │  Response       │◄───│  Data           │◄───│  Aggregation    │◄───│  Formatting     │      │
│  │  Generation     │    │  Processing     │    │  & Analysis     │    │  & Serialization│      │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘    └─────────────────┘      │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 🔒 Security Architecture

### **Security Layers**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Security Layers                                             │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Application       │  │   Data Access       │  │   Network           │  │   Infrastructure│ │
│  │   Security          │  │   Security          │  │   Security          │  │   Security      │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • Input Validation │  │ • SQL Injection     │  │ • HTTPS/TLS         │  │ • Azure Security│ │
│  │ • CORS Policy      │  │   Prevention        │  │ • API Key           │  │ • Network       │ │
│  │ • Error Handling   │  │ • Parameterized     │  │   Authentication    │  │   Isolation     │ │
│  │ • Rate Limiting    │  │   Queries           │  │ • CORS Headers      │  │ • Firewall      │ │
│  │ • Authentication   │  │ • Read-Only Access  │  │ • Request           │  │ • DDoS          │ │
│  │ • Authorization    │  │ • Data Encryption   │  │   Validation        │  │   Protection    │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### **SQL Security Implementation**

```csharp
// Security checks in FinancialTools.ExecuteReadOnlySQLQuery
public async Task<string> ExecuteReadOnlySQLQuery(string sqlQuery)
{
    // 1. Only SELECT queries allowed
    var trimmedQuery = sqlQuery.Trim();
    if (!trimmedQuery.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
    {
        return "❌ SECURITY ERROR: Only SELECT queries are allowed.";
    }

    // 2. Block dangerous keywords
    var forbiddenKeywords = new[] { 
        "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "EXEC", "EXECUTE", "TRUNCATE" 
    };
    
    foreach (var keyword in forbiddenKeywords)
    {
        if (trimmedQuery.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return $"❌ SECURITY ERROR: The keyword '{keyword}' is not allowed.";
        }
    }

    // 3. Execute safe query
    return await _transactionService.ExecuteCustomSQLQueryAsync(sqlQuery, _connectionString);
}
```

## 📊 Monitoring & Observability

### **Logging Architecture**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Logging Strategy                                            │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Console Logging   │  │   Structured        │  │   Performance       │  │   Error         │ │
│  │                     │  │   Logging           │  │   Metrics           │  │   Tracking      │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • Vector Search     │  │ • JSON Format       │  │ • Response Times    │  │ • Exception     │ │
│  │ • AI Processing     │  │ • Log Levels        │  │ • Throughput        │  │   Details       │ │
│  │ • Database Ops      │  │ • Correlation IDs   │  │ • Resource Usage    │  │ • Stack Traces  │ │
│  │ • Chat Operations   │  │ • Context Info      │  │ • Memory Usage      │  │ • Error Codes   │ │
│  │ • Real-time Debug   │  │ • Structured Data   │  │ • CPU Usage        │  │ • User Context  │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### **Performance Monitoring Points**

```csharp
// Key performance monitoring points
public async Task<List<ChatMessageInfo>> GetRelevantChatHistoryAsync(string sessionId, string currentQuery, int maxResults = 20)
{
    var stopwatch = Stopwatch.StartNew();
    
    try
    {
        // 1. Embedding generation timing
        var embeddingStart = Stopwatch.StartNew();
        var queryEmbedding = await GetEmbeddingAsync(currentQuery);
        Console.WriteLine($"⏱️ Embedding generation: {embeddingStart.ElapsedMilliseconds}ms");
        
        // 2. Vector search timing
        var searchStart = Stopwatch.StartNew();
        var searchResponse = await PerformVectorSearch(queryEmbedding, sessionId, maxResults);
        Console.WriteLine($"⏱️ Vector search: {searchStart.ElapsedMilliseconds}ms");
        
        // 3. Result processing timing
        var processingStart = Stopwatch.StartNew();
        var results = ProcessSearchResults(searchResponse);
        Console.WriteLine($"⏱️ Result processing: {processingStart.ElapsedMilliseconds}ms");
        
        // 4. Total operation timing
        Console.WriteLine($"⏱️ Total operation: {stopwatch.ElapsedMilliseconds}ms");
        
        return results;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error in GetRelevantChatHistoryAsync: {ex.Message}");
        throw;
    }
}
```

## 🔮 Scalability Considerations

### **Horizontal Scaling Strategy**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Scaling Strategy                                            │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Application       │  │   Database          │  │   Search            │  │   AI Services   │ │
│  │   Scaling           │  │   Scaling           │  │   Scaling           │  │   Scaling       │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • Load Balancer     │  │ • Read Replicas     │  │ • Search Units      │  │ • Model        │ │
│  │ • Multiple Instances│  │ • Connection Pooling│  │ • Partitioning      │  │   Deployment   │ │
│  │ • Stateless Design  │  │ • Sharding          │  │ • Index Replication │  │ • Auto-scaling │ │
│  │ • Session Affinity  │  │ • Caching Layer     │  │ • Geographic        │  │ • Load         │ │
│  │ • Health Checks     │  │ • Backup Strategy   │  │   Distribution      │  │   Balancing    │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### **Performance Optimization Techniques**

1. **Vector Search Optimization**
   - HNSW algorithm for fast similarity search
   - Batch processing for embeddings
   - Connection pooling for database operations
   - Async/await throughout the stack

2. **Caching Strategy**
   - Azure AI Search for persistent storage
   - Efficient context building
   - Smart message truncation
   - Background summary generation

3. **Database Optimization**
   - Indexed vector fields
   - Efficient SQL queries
   - Connection timeout management
   - Batch operations for large datasets

## 🧪 Testing Strategy

### **Testing Layers**

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Testing Strategy                                            │
├─────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                 │
│  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐ │
│  │   Unit Tests        │  │   Integration       │  │   Performance       │  │   End-to-End    │ │
│  │                     │  │   Tests             │  │   Tests             │  │   Tests         │ │
│  │                     │  │                     │  │                     │  │                 │ │
│  │ • Service Methods   │  │ • Database          │  │ • Response Times    │  │ • User          │ │
│  │ • Business Logic    │  │   Operations        │  │ • Throughput        │  │   Scenarios     │ │
│  │ • Validation        │  │ • Azure Services    │  │ • Resource Usage    │  │ • API           │ │
│  │ • Edge Cases        │  │ • External APIs     │  │ • Load Testing      │  │   Workflows     │ │
│  │ • Mock Dependencies │  │ • Service           │  │ • Stress Testing    │  │ • Error         │ │
│  │                     │  │   Communication     │  │ • Scalability       │  │   Handling      │ │
│  └─────────────────────┘  └─────────────────────┘  └─────────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 📚 Additional Resources

### **Architecture Patterns Used**

1. **Clean Architecture** - Separation of concerns with clear layers
2. **Repository Pattern** - Data access abstraction
3. **Service Layer Pattern** - Business logic encapsulation
4. **Dependency Injection** - Loose coupling and testability
5. **Async/Await Pattern** - Non-blocking operations
6. **Vector Search Pattern** - Semantic similarity matching
7. **Context Management Pattern** - Conversation state management

### **Best Practices Implemented**

1. **Security First** - SQL injection prevention, input validation
2. **Performance Optimization** - Vector search, caching, async operations
3. **Error Handling** - Comprehensive error handling with logging
4. **Monitoring** - Performance metrics and debugging information
5. **Scalability** - Stateless design, connection pooling
6. **Maintainability** - Clear separation of concerns, dependency injection

---

**This architecture document provides a comprehensive technical overview of the Transaction Labeler API system, including data flows, security considerations, and scalability strategies.**
