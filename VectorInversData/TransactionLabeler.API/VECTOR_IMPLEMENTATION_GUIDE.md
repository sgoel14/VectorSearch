# VECTOR-Based Multi-Embedding Implementation Guide

## Overview
This guide explains how to implement the new VECTOR-based multi-embedding approach for intelligent semantic search in your transaction database.

## Database Schema Changes

### 1. Add VECTOR Columns
Run the SQL script `add_embedding_columns.sql` to add the new VECTOR columns:

```sql
ALTER TABLE dbo.inversbanktransaction 
ADD ContentEmbedding VECTOR(1536) NULL,
    AmountEmbedding VECTOR(1536) NULL,
    DateEmbedding VECTOR(1536) NULL,
    CategoryEmbedding VECTOR(1536) NULL,
    CombinedEmbedding VECTOR(1536) NULL;
```

### 2. VECTOR Data Type Benefits
- **Native Vector Operations**: Direct support for `VECTOR_DISTANCE()` function
- **Optimized Performance**: Built-in vector indexing and optimization
- **Type Safety**: Ensures data integrity for vector operations
- **Scalability**: Better performance for large-scale vector searches

## Implementation Steps

### Step 1: Update Database Schema
1. Run the `add_embedding_columns.sql` script
2. Verify the columns were added correctly:
```sql
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH 
FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME = 'inversbanktransaction' 
AND COLUMN_NAME LIKE '%Embedding%';
```

### Step 2: Regenerate Embeddings
Use the new batch update method to generate specialized embeddings:

```bash
POST /api/transactions/update-all-invers-embeddings
```

This will:
- Generate 5 different embeddings for each transaction
- Store them in the appropriate VECTOR columns
- Use optimized text formatting for each query type

### Step 3: Test Intelligent Search
Use the new intelligent search endpoint:

```bash
POST /api/transactions/intelligent-vector-search
Content-Type: application/json

"Find transactions with highest amount in July"
```

## Query Types and Embeddings

### 1. Content Embedding
- **Purpose**: Description-based queries
- **Text Format**: `"{Description} {RgsDescription} {RgsShortDescription}"`
- **Example Queries**: "Peugeot car payment", "restaurant expenses"

### 2. Amount Embedding
- **Purpose**: Amount/money-based queries
- **Text Format**: `"amount {Amount} currency money payment transaction value financial"`
- **Example Queries**: "highest amount", "expensive transactions", "large payments"

### 3. Date Embedding
- **Purpose**: Temporal queries
- **Text Format**: `"date {yyyy-MM-dd} month {MMMM} year {yyyy} day {dd} weekday {dddd}"`
- **Example Queries**: "July transactions", "transactions from last month", "weekend expenses"

### 4. Category Embedding
- **Purpose**: Category/RGS-based queries
- **Text Format**: `"category {RgsCode} {RgsDescription} {RgsShortDescription} type {TransactionType}"`
- **Example Queries**: "salary transactions", "RGS code 1234", "category food"

### 5. Combined Embedding
- **Purpose**: General semantic search
- **Text Format**: All above formats combined
- **Example Queries**: General descriptions, mixed criteria

## Query Classification Logic

The system automatically classifies queries based on keywords:

```csharp
// Amount-based keywords
"amount", "highest", "largest", "money", "payment", "value", "expensive", "cheap", "cost"

// Date-based keywords  
"july", "august", "month", "year", "date", "when", "january", "february", etc.

// Category-based keywords
"category", "salary", "type", "rgs", "classification", "group"
```

## Performance Optimizations

### 1. VECTOR Column Benefits
- **Native Vector Operations**: No need for JSON parsing
- **Optimized Indexing**: Automatic vector index optimization
- **Faster Searches**: Direct vector distance calculations

### 2. Query-Specific Ordering
- **Amount Queries**: `ORDER BY t.Amount DESC, similarity ASC`
- **Date Queries**: `ORDER BY t.TransactionDate DESC, similarity ASC`
- **Category Queries**: `ORDER BY similarity ASC`

### 3. Batch Processing
- **Parallel Processing**: Up to 8 concurrent embedding generations
- **Page Size**: 200 records per batch
- **Error Handling**: Graceful failure handling per record

## Testing Examples

### Test Case 1: Amount-Based Query
```bash
POST /api/transactions/intelligent-vector-search
"Find transactions with highest amount in month of July"
```
**Expected Behavior**: Uses DateEmbedding, orders by date then amount

### Test Case 2: Category-Based Query
```bash
POST /api/transactions/intelligent-vector-search
"Find the transactions with category as salary only"
```
**Expected Behavior**: Uses CategoryEmbedding, focuses on RGS codes

### Test Case 3: Content-Based Query
```bash
POST /api/transactions/intelligent-vector-search
"Peugeot car payment transactions"
```
**Expected Behavior**: Uses ContentEmbedding, semantic matching on descriptions

## Monitoring and Maintenance

### 1. Embedding Generation Status
Check which records have embeddings:
```sql
SELECT 
    COUNT(*) as TotalRecords,
    COUNT(ContentEmbedding) as ContentEmbeddings,
    COUNT(AmountEmbedding) as AmountEmbeddings,
    COUNT(DateEmbedding) as DateEmbeddings,
    COUNT(CategoryEmbedding) as CategoryEmbeddings,
    COUNT(CombinedEmbedding) as CombinedEmbeddings
FROM dbo.inversbanktransaction;
```

### 2. Performance Monitoring
Monitor query performance:
```sql
-- Check for slow vector queries
SELECT 
    query_hash,
    COUNT(*) as execution_count,
    AVG(total_elapsed_time) as avg_elapsed_time
FROM sys.dm_exec_query_stats
WHERE sql_handle IN (
    SELECT sql_handle 
    FROM sys.dm_exec_sql_text(sql_handle) 
    WHERE text LIKE '%VECTOR_DISTANCE%'
)
GROUP BY query_hash
ORDER BY avg_elapsed_time DESC;
```

## Troubleshooting

### Common Issues

1. **VECTOR_DISTANCE Function Not Found**
   - Ensure you're using Azure SQL Database with vector support
   - Check that your database tier supports VECTOR operations

2. **Embedding Generation Fails**
   - Verify Azure OpenAI configuration
   - Check API rate limits and quotas
   - Monitor embedding service logs

3. **Poor Search Results**
   - Verify embeddings were generated correctly
   - Check query classification logic
   - Review embedding text formatting

### Debug Queries

```sql
-- Check embedding data
SELECT TOP 10 
    Id, 
    Description,
    LEN(ContentEmbedding) as ContentLength,
    LEN(AmountEmbedding) as AmountLength,
    LEN(DateEmbedding) as DateLength
FROM dbo.inversbanktransaction 
WHERE ContentEmbedding IS NOT NULL;

-- Test vector distance manually
SELECT TOP 5 
    Id, 
    Description,
    VECTOR_DISTANCE('cosine', ContentEmbedding, 
        CAST('[0.1,0.2,0.3,...]' AS VECTOR(1536))) as distance
FROM dbo.inversbanktransaction 
WHERE ContentEmbedding IS NOT NULL
ORDER BY distance ASC;
```

## Migration from Legacy Approach

If you have existing JSON-based embeddings:

1. **Backup existing data**
2. **Run the new schema changes**
3. **Regenerate embeddings using the new approach**
4. **Test thoroughly before switching**
5. **Update frontend to use new endpoints**

## Best Practices

1. **Regular Embedding Updates**: Regenerate embeddings when data changes significantly
2. **Monitor Performance**: Track query response times and optimize as needed
3. **Query Optimization**: Use specific embedding types for targeted searches
4. **Error Handling**: Implement robust error handling for embedding generation
5. **Testing**: Test with various query types to ensure proper classification 