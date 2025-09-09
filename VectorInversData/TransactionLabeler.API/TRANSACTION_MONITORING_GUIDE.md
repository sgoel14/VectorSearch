# 🚨 Transaction Monitoring & Anomaly Detection Guide

## 🎯 Overview

This guide explains how to use the new **Flexible Transaction Monitoring Tools** that can handle multiple scenarios including the three specific use cases you mentioned. Instead of creating separate tools for each scenario, we've designed **3 flexible tools** that can be configured to handle various transaction monitoring needs.

## 🛠️ Available Tools

### 1. **`AnalyzeCounterpartyActivity`** - Unknown Counterparty Detection
**Purpose:** Detects new/unknown counterparties by comparing current vs historical periods.

**Parameters:**
- `currentPeriodDays` (default: 30) - How many days back to analyze for current activity
- `historicalPeriodDays` (default: 30) - How many days back to analyze for historical activity
- `customerName` (optional) - Filter by specific customer
- `minAmount` / `maxAmount` (optional) - Filter by transaction amount range
- `transactionType` (optional) - Filter by 'Af' (expenses) or 'Bij' (income)

### 2. **`AnalyzeTransactionAnomalies`** - Amount Anomaly Detection
**Purpose:** Detects unusual transaction amounts by comparing current vs historical patterns.

**Parameters:**
- `periodDays` (default: 30) - How many days back to analyze
- `customerName` (optional) - Filter by specific customer
- `thresholdMultiplier` (default: 2.0) - How many times above/below normal to flag as anomaly
- `comparisonMethod` (default: "amount") - Method for comparison
- `transactionType` (optional) - Filter by transaction type

### 3. **`GetTransactionProfiles`** - Historical Pattern Analysis
**Purpose:** Gets transaction profiles and historical statistics for counterparties.

**Parameters:**
- `periodMonths` (default: 15) - How many months of history to analyze
- `customerName` (optional) - Filter by specific customer
- `counterpartyAccount` (optional) - Filter by specific bank account
- `transactionType` (optional) - Filter by transaction type
- `includeStatistics` (default: true) - Include detailed statistical analysis

## 🔍 Scenario 1: Display Transactions to/from Unknown Counterparty Accounts

**User Query:** "Display transactions to or from unknown counterparty accounts"

**Tool to Use:** `AnalyzeCounterpartyActivity`

**Recommended Configuration:**
```csharp
// Default behavior (last 30 days vs previous 30 days)
await AnalyzeCounterpartyActivity(
    currentPeriodDays: 30,      // Last 30 days
    historicalPeriodDays: 30,   // 30-60 days ago
    customerName: null,         // All customers
    minAmount: null,            // All amounts
    maxAmount: null,            // All amounts
    transactionType: null       // Both expenses and income
);
```

**What This Does:**
1. Gets all counterparties from 30-60 days ago (known counterparties)
2. Gets all counterparties from last 30 days (current period)
3. Identifies counterparties in current period that were NOT in historical period
4. Returns detailed transaction information for these unknown counterparties

**Alternative Configurations:**
```csharp
// For specific customer with amount filtering
await AnalyzeCounterpartyActivity(
    currentPeriodDays: 30,
    historicalPeriodDays: 60,   // Longer historical period
    customerName: "Nova Creations",
    minAmount: 100,             // Only transactions above €100
    maxAmount: null,
    transactionType: "Af"       // Only expenses
);

// For different time periods
await AnalyzeCounterpartyActivity(
    currentPeriodDays: 7,       // Last week
    historicalPeriodDays: 30,   // Previous month
    customerName: null,
    minAmount: null,
    maxAmount: null,
    transactionType: null
);
```

## 🚨 Scenario 2: Are There Payments/Receipts Much Larger Than Usual?

**User Query:** "Are there payments or receipts from known accounts that are much larger than usual?"

**Tool to Use:** `AnalyzeTransactionAnomalies`

**Recommended Configuration:**
```csharp
// Default behavior (2x normal threshold)
await AnalyzeTransactionAnomalies(
    periodDays: 30,             // Last 30 days
    customerName: null,         // All customers
    thresholdMultiplier: 2.0,   // Flag if 2x above/below normal
    comparisonMethod: "amount", // Amount-based comparison
    transactionType: null       // Both expenses and income
);
```

**What This Does:**
1. Gets historical transaction profiles for all counterparties (15 months of history)
2. Compares current period transactions against historical patterns
3. Flags transactions that are significantly above/below normal ranges
4. Provides detailed analysis with anomaly classification

**Alternative Configurations:**
```csharp
// More sensitive detection (1.5x threshold)
await AnalyzeTransactionAnomalies(
    periodDays: 30,
    customerName: "Nova Creations",
    thresholdMultiplier: 1.5,   // More sensitive
    comparisonMethod: "amount",
    transactionType: "Af"       // Only expenses
);

// For specific time periods
await AnalyzeTransactionAnomalies(
    periodDays: 7,              // Last week
    customerName: null,
    thresholdMultiplier: 3.0,   // 3x threshold for weekly analysis
    comparisonMethod: "amount",
    transactionType: null
);
```

## 🚨 Scenario 3: Large Payments/Receipts from Unknown Accounts?

**User Query:** "Are there large payments or receipts from unknown accounts?"

**Approach:** Combine both tools for comprehensive analysis

**Step 1: Find Unknown Counterparties**
```csharp
var unknownCounterparties = await AnalyzeCounterpartyActivity(
    currentPeriodDays: 30,
    historicalPeriodDays: 30,
    customerName: null,
    minAmount: 1000,            // Focus on large transactions
    maxAmount: null,
    transactionType: null
);
```

**Step 2: Analyze Their Transaction Patterns**
```csharp
var transactionProfiles = await GetTransactionProfiles(
    periodMonths: 15,
    customerName: null,
    counterpartyAccount: null,  // Will analyze all counterparties
    transactionType: null,
    includeStatistics: true     // Get detailed statistics
);
```

**Step 3: Detect Anomalies in Unknown Counterparties**
```csharp
var anomalies = await AnalyzeTransactionAnomalies(
    periodDays: 30,
    customerName: null,
    thresholdMultiplier: 2.0,
    comparisonMethod: "amount",
    transactionType: null
);
```

## 🎯 Advanced Use Cases

### **Custom Time Periods**
```csharp
// Analyze last quarter vs previous quarter
await AnalyzeCounterpartyActivity(
    currentPeriodDays: 90,      // Last quarter
    historicalPeriodDays: 90,   // Previous quarter
    customerName: null,
    minAmount: null,
    maxAmount: null,
    transactionType: null
);
```

### **Amount-Based Filtering**
```csharp
// Focus on high-value transactions
await AnalyzeCounterpartyActivity(
    currentPeriodDays: 30,
    historicalPeriodDays: 30,
    customerName: null,
    minAmount: 5000,            // Only transactions above €5000
    maxAmount: null,
    transactionType: null
);
```

### **Customer-Specific Analysis**
```csharp
// Analyze specific customer's counterparties
await AnalyzeTransactionAnomalies(
    periodDays: 30,
    customerName: "Nova Creations",
    thresholdMultiplier: 1.5,
    comparisonMethod: "amount",
    transactionType: "Af"       // Only expenses
);
```

### **Statistical Analysis**
```csharp
// Get detailed transaction profiles with statistics
await GetTransactionProfiles(
    periodMonths: 24,           // 2 years of history
    customerName: null,
    counterpartyAccount: null,
    transactionType: null,
    includeStatistics: true     // Include standard deviation, percentiles
);
```

## 🔧 Tool Selection Guidelines

### **Use `AnalyzeCounterpartyActivity` When:**
- User asks about "unknown" or "new" counterparties
- User wants to compare current vs historical periods
- User asks "who are we doing business with now that we weren't before?"

### **Use `AnalyzeTransactionAnomalies` When:**
- User asks about "unusual" or "large" transactions
- User wants to detect suspicious patterns
- User asks "are there transactions much higher/lower than normal?"

### **Use `GetTransactionProfiles` When:**
- User wants historical transaction patterns
- User asks about "normal" transaction amounts
- User wants to understand counterparty behavior over time

### **Use `ExecuteReadOnlySQLQuery` When:**
- User asks for complex custom analysis
- User wants specific SQL-based queries
- User needs analysis that doesn't fit the standard tools

## 📊 Output Format

All tools return formatted markdown tables with:
- **Clear headers** explaining what the analysis shows
- **Summary statistics** at the top
- **Detailed results** in table format
- **Analysis insights** explaining what the results mean
- **Visual indicators** (🚨 for high anomalies, ⚠️ for warnings, ✅ for normal)

## 🚀 Benefits of This Approach

### **1. Flexibility**
- Single tool can handle multiple scenarios
- Configurable parameters for different use cases
- Easy to extend for new requirements

### **2. Consistency**
- All tools use the same output format
- Consistent parameter naming and behavior
- Unified error handling and logging

### **3. Maintainability**
- Fewer tools to maintain
- Centralized logic for similar operations
- Easy to add new features

### **4. User Experience**
- AI can easily understand when to use each tool
- Clear parameter descriptions
- Intuitive tool selection

## 🔮 Future Enhancements

These tools are designed to be easily extended with:
- **Additional comparison methods** (frequency, timing, etc.)
- **Machine learning-based anomaly detection**
- **Real-time monitoring capabilities**
- **Custom threshold rules**
- **Integration with external fraud detection systems**

---

## 📋 Important Notes

### **Database Tables Used**
- **`inversbanktransaction`**: Main transaction table containing all financial transactions
- **`rgsmapping`**: RGS code mapping table for category descriptions
- **Note**: We no longer use `persistentbankstatementline` table

### **Table Structure**
- **`inversbanktransaction`**: Contains transaction details, amounts, dates, counterparty information, and RGS codes
- **`rgsmapping`**: Contains RGS code descriptions for proper categorization

---

**🎯 These flexible tools provide a comprehensive solution for transaction monitoring while maintaining simplicity and extensibility for future requirements.**
