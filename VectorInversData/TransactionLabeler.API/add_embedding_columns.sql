-- Add new embedding columns to inversbanktransaction table
-- Run this script to add the specialized embedding columns with VECTOR data type

ALTER TABLE dbo.inversbanktransaction 
ADD ContentEmbedding VECTOR(1536) NULL,
    AmountEmbedding VECTOR(1536) NULL,
    DateEmbedding VECTOR(1536) NULL,
    CategoryEmbedding VECTOR(1536) NULL,
    CombinedEmbedding VECTOR(1536) NULL;

-- Add comments to document the purpose of each column
EXEC sp_addextendedproperty 
    @name = N'MS_Description', 
    @value = N'Vector embedding for content-based queries (description, RGS descriptions)', 
    @level0type = N'SCHEMA', @level0name = N'dbo', 
    @level1type = N'TABLE', @level1name = N'inversbanktransaction', 
    @level2type = N'COLUMN', @level2name = N'ContentEmbedding';

EXEC sp_addextendedproperty 
    @name = N'MS_Description', 
    @value = N'Vector embedding for amount-based queries (amount, money, payment)', 
    @level0type = N'SCHEMA', @level0name = N'dbo', 
    @level1type = N'TABLE', @level1name = N'inversbanktransaction', 
    @level2type = N'COLUMN', @level2name = N'AmountEmbedding';

EXEC sp_addextendedproperty 
    @name = N'MS_Description', 
    @value = N'Vector embedding for date-based queries (month, year, temporal)', 
    @level0type = N'SCHEMA', @level0name = N'dbo', 
    @level1type = N'TABLE', @level1name = N'inversbanktransaction', 
    @level2type = N'COLUMN', @level2name = N'DateEmbedding';

EXEC sp_addextendedproperty 
    @name = N'MS_Description', 
    @value = N'Vector embedding for category-based queries (RGS code, category, type)', 
    @level0type = N'SCHEMA', @level0name = N'dbo', 
    @level1type = N'TABLE', @level1name = N'inversbanktransaction', 
    @level2type = N'COLUMN', @level2name = N'CategoryEmbedding';

EXEC sp_addextendedproperty 
    @name = N'MS_Description', 
    @value = N'Combined vector embedding for general semantic search', 
    @level0type = N'SCHEMA', @level0name = N'dbo', 
    @level1type = N'TABLE', @level1name = N'inversbanktransaction', 
    @level2type = N'COLUMN', @level2name = N'CombinedEmbedding';

-- Note: VECTOR columns automatically support vector operations and indexing
-- No need for additional indexes as VECTOR columns are optimized for vector operations 