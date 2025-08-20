using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SemanticKernel.ChatCompletion;
using TransactionLabeler.API.Models;
using TransactionLabeler.API.Services;

namespace TransactionLabeler.API.Services
{
    /// <summary>
    /// Helper class for reframing questions with context awareness
    /// </summary>
    public static class QuestionReframer
    {
        /// <summary>
        /// Reframes a question with context from chat history
        /// </summary>
        public static string ReframeQuestionWithContext(string question, List<ChatMessageInfo> chatHistory)
        {
            Console.WriteLine($"🔄 ReframeQuestionWithContext called with question: '{question}'");
            Console.WriteLine($"📊 Chat history contains {chatHistory.Count} messages:");
            foreach (var msg in chatHistory)
            {
                Console.WriteLine($"   {msg.Role}: {msg.Content.Substring(0, Math.Min(100, msg.Content.Length))}...");
            }

            var lowerQuestion = question.ToLower();
            var reframedQuestion = question;

            // Check if this is a follow-up question that needs context
            if (IsFollowUpQuestion(lowerQuestion))
            {
                Console.WriteLine($"✅ Question '{question}' identified as follow-up question");
                
                // Check if this is a financial follow-up question that needs financial context reframing
                if (IsFinancialFollowUpQuestion(lowerQuestion))
                {
                    Console.WriteLine($"💰 Question identified as financial follow-up, applying financial context reframing");
                    reframedQuestion = ReframeFinancialQuestion(question, chatHistory);
                }
                else
                {
                    Console.WriteLine($"💬 Question identified as general follow-up, applying general context reframing");
                    // For general questions, we could add basic context reframing here if needed
                    // For now, just return the original question
                }
            }
            else
            {
                Console.WriteLine($"❌ Question '{question}' NOT identified as follow-up question");
            }

            Console.WriteLine($"🔄 Final reframed question: '{reframedQuestion}'");
            return reframedQuestion;
        }

        /// <summary>
        /// Determines if a question is a follow-up question
        /// </summary>
        public static bool IsFollowUpQuestion(string question)
        {
            // General knowledge follow-up patterns
            var generalFollowUpPatterns = new[]
            {
                "can you recommend",
                "what about",
                "how about",
                "tell me more",
                "explain",
                "describe",
                "what is",
                "who is",
                "where is",
                "when is",
                "why is",
                "how is",
                "recommend",
                "suggest",
                "provide",
                "give me",
                "show me"
            };

            // Financial follow-up patterns
            var financialFollowUpPatterns = new[]
            {
                "check in",
                "try",
                "what about",
                "how about",
                "search in",
                "look in",
                "find in",
                "get in",
                "show in",
                "list in",
                "transactions in",
                "categories in",
                "expenses in",
                "costs in",
                "spending in"
            };

            // Category change patterns - these indicate the user wants to change categories
            var categoryChangePatterns = new[]
            {
                "for category",
                "category",
                "transactions for",
                "expenses for",
                "costs for",
                "spending for",
                "give transactions for",
                "show transactions for",
                "get transactions for",
                "find transactions for",
                "search transactions for"
            };

            // Check if it's a general follow-up
            if (generalFollowUpPatterns.Any(pattern => question.Contains(pattern)))
            {
                return true;
            }

            // Check if it's a financial follow-up (time period change, customer change, category change)
            if (financialFollowUpPatterns.Any(pattern => question.Contains(pattern)))
            {
                return true;
            }

            // Check if it's a category change question
            if (categoryChangePatterns.Any(pattern => question.ToLower().Contains(pattern)))
            {
                return true;
            }

            // Check for standalone time periods, customer names, or categories that suggest follow-up
            var standalonePatterns = new[]
            {
                "2024", "2025", "2023", "2022", "2021", "2020", "2019", "2018", "2017", "2016", "2015",
                "q1", "q2", "q3", "q4", "quarter 1", "quarter 2", "quarter 3", "quarter 4",
                "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
                "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"
            };

            if (standalonePatterns.Any(pattern => question.ToLower().Contains(pattern)))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines if a question is a financial follow-up question
        /// </summary>
        public static bool IsFinancialFollowUpQuestion(string question)
        {
            // Financial follow-up patterns that suggest context changes
            var financialPatterns = new[]
            {
                "check in", "try", "search in", "look in", "find in", "get in", "show in", "list in",
                "transactions in", "categories in", "expenses in", "costs in", "spending in",
                "what about", "how about", "can you try", "can you check", "can you search"
            };

            // Category change patterns - these indicate the user wants to change categories
            var categoryChangePatterns = new[]
            {
                "for category",
                "category",
                "transactions for",
                "expenses for",
                "costs for",
                "spending for",
                "give transactions for",
                "show transactions for",
                "get transactions for",
                "find transactions for",
                "search transactions for"
            };

            // Check for standalone time periods, customer names, or categories
            var standalonePatterns = new[]
            {
                "2024", "2025", "2023", "2022", "2021", "2020", "2019", "2018", "2017", "2016", "2015",
                "q1", "q2", "q3", "q4", "quarter 1", "quarter 2", "quarter 3", "quarter 4",
                "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
                "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"
            };

            return financialPatterns.Any(pattern => question.Contains(pattern)) ||
                   categoryChangePatterns.Any(pattern => question.ToLower().Contains(pattern)) ||
                   standalonePatterns.Any(pattern => question.ToLower().Contains(pattern));
        }

        /// <summary>
        /// Reframes a financial question with context from chat history
        /// </summary>
        public static string ReframeFinancialQuestion(string question, List<ChatMessageInfo> chatHistory)
        {
            Console.WriteLine($"💰 Reframing financial question: '{question}'");
            
            // Extract financial context from recent chat history
            var financialContext = ExtractFinancialContextFromHistory(chatHistory);
            Console.WriteLine($"💰 Extracted financial context - Customer: [{string.Join(", ", financialContext.Customers)}], Category: [{string.Join(", ", financialContext.Categories)}], Time: [{string.Join(", ", financialContext.TimePeriods)}], QueryType: [{string.Join(", ", financialContext.QueryTypes)}]");
            
            var reframedQuestion = question;
            
            // If this is a standalone time period, customer, or category, build a complete question
            if (IsStandaloneFinancialContext(question))
            {
                reframedQuestion = BuildCompleteFinancialQuestion(question, financialContext);
                Console.WriteLine($"💰 Built complete financial question: '{reframedQuestion}'");
            }
            else
            {
                // Add missing context to the existing question using pattern matching instead of hardcoded strings
                if (financialContext.Customers.Any() && !ContainsAnyPattern(question, GetCustomerPatterns()))
                {
                    var customer = financialContext.Customers.First();
                    reframedQuestion = AddFinancialContext(reframedQuestion, customer, "customer");
                    Console.WriteLine($"💰 Added customer context '{customer}': '{reframedQuestion}'");
                }

                if (financialContext.Categories.Any() && !ContainsAnyPattern(question, GetCategoryPatterns()))
                {
                    var category = financialContext.Categories.First();
                    reframedQuestion = AddFinancialContext(reframedQuestion, category, "category");
                    Console.WriteLine($"💰 Added category context '{category}': '{reframedQuestion}'");
                }

                if (financialContext.QueryTypes.Any() && !ContainsAnyPattern(question, GetQueryTypePatterns()))
                {
                    var queryType = financialContext.QueryTypes.First();
                    reframedQuestion = AddFinancialContext(reframedQuestion, queryType, "query type");
                    Console.WriteLine($"💰 Added query type context '{queryType}': '{reframedQuestion}'");
                }

                if (financialContext.TimePeriods.Any() && !ContainsAnyPattern(question, GetTimePeriodPatterns()))
                {
                    var timePeriod = financialContext.TimePeriods.First();
                    reframedQuestion = AddFinancialContext(reframedQuestion, timePeriod, "time period");
                    Console.WriteLine($"💰 Added time period context '{timePeriod}': '{reframedQuestion}'");
                }
            }

            return reframedQuestion;
        }

        /// <summary>
        /// Checks if a string contains any of the provided patterns
        /// </summary>
        private static bool ContainsAnyPattern(string text, string[] patterns)
        {
            return patterns.Any(pattern => text.ToLower().Contains(pattern.ToLower()));
        }

        /// <summary>
        /// Gets customer-related patterns for pattern matching
        /// </summary>
        private static string[] GetCustomerPatterns()
        {
            return new[]
            {
                "customer", "client", "company", "business", "organization", "enterprise",
                "klant", "bedrijf", "onderneming", "organisatie", // Dutch
                "kunde", "unternehmen", "organisation", "firma", // German
                "cliente", "empresa", "negocio", "organización", // Spanish
                "client", "entreprise", "affaire", "organisation" // French
            };
        }

        /// <summary>
        /// Gets category-related patterns for pattern matching
        /// </summary>
        private static string[] GetCategoryPatterns()
        {
            return new[]
            {
                "category", "categorie", "kategorie", "categoría", "catégorie",
                "type", "tipo", "typ", "type", "soort", "art", "clase", "classe",
                "group", "groep", "gruppe", "grupo", "groupe"
            };
        }

        /// <summary>
        /// Gets query type patterns for pattern matching
        /// </summary>
        private static string[] GetQueryTypePatterns()
        {
            return new[]
            {
                "transaction", "transactie", "transaktion", "transacción", "transaction",
                "expense", "uitgave", "ausgabe", "gasto", "dépense",
                "cost", "kosten", "kosten", "costo", "coût",
                "spending", "uitgaven", "ausgaben", "gastos", "dépenses"
            };
        }

        /// <summary>
        /// Gets time period patterns for pattern matching
        /// </summary>
        private static string[] GetTimePeriodPatterns()
        {
            return new[]
            {
                "year", "jaar", "jahr", "año", "année",
                "month", "maand", "monat", "mes", "mois",
                "quarter", "kwartaal", "quartal", "trimestre", "trimestre",
                "week", "week", "woche", "semana", "semaine"
            };
        }

        /// <summary>
        /// Determines if a question represents a standalone financial context element
        /// </summary>
        private static bool IsStandaloneFinancialContext(string question)
        {
            var standaloneElements = new[]
            {
                "2024", "2025", "2023", "2022", "2021", "2020", "2019", "2018", "2017", "2016", "2015",
                "q1", "q2", "q3", "q4", "quarter 1", "quarter 2", "quarter 3", "quarter 4",
                "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
                "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"
            };

            var trimmedQuestion = question.Trim().ToLower();
            return standaloneElements.Any(element => trimmedQuestion == element.ToLower());
        }

        /// <summary>
        /// Builds a complete financial question from a standalone element and context
        /// </summary>
        private static string BuildCompleteFinancialQuestion(string standaloneElement, FinancialContext context)
        {
            var questionBuilder = new List<string>();
            questionBuilder.Add("can you get transactions related to");

            if (context.Categories.Any())
            {
                questionBuilder.Add(context.Categories.First());
            }
            else
            {
                questionBuilder.Add("all categories");
            }

            if (context.Customers.Any())
            {
                questionBuilder.Add($"for {context.Customers.First()}");
            }

            if (context.TimePeriods.Any())
            {
                questionBuilder.Add($"in {context.TimePeriods.First()}");
            }

            return string.Join(" ", questionBuilder) + "?";
        }

        /// <summary>
        /// Adds financial context to a question if it's not already present
        /// </summary>
        private static string AddFinancialContext(string question, string context, string contextType)
        {
            // Don't add context if it's already mentioned in the question
            if (question.ToLower().Contains(context.ToLower()))
            {
                return question;
            }

            // Add context in a natural way
            if (question.EndsWith("?"))
            {
                question = question.TrimEnd('?');
                return $"{question} for {context}?";
            }
            else
            {
                return $"{question} for {context}";
            }
        }

        /// <summary>
        /// Extracts financial context from chat history
        /// </summary>
        private static FinancialContext ExtractFinancialContextFromHistory(List<ChatMessageInfo> chatHistory)
        {
            var context = new FinancialContext();
            
            // Analyze last 6 messages (user and assistant) for context
            var recentMessages = chatHistory.TakeLast(6).ToList();
            
            foreach (var message in recentMessages)
            {
                if (message.Role == AuthorRole.User)
                {
                    ExtractCustomerNamesFromQuery(message.Content, context);
                    ExtractCategoriesFromQuery(message.Content, context);
                    ExtractTimePeriods(message.Content, context);
                    ExtractQueryTypesFromQuery(message.Content, context);
                }
                else if (message.Role == AuthorRole.Assistant)
                {
                    ExtractCustomerNamesFromResponse(message.Content, context);
                    ExtractCategoriesFromResponse(message.Content, context);
                    ExtractTimePeriodsFromResponse(message.Content, context);
                    ExtractQueryTypesFromResponse(message.Content, context);
                }
            }
            
            return context;
        }

        /// <summary>
        /// Extracts customer names from a query and adds them to the financial context
        /// </summary>
        private static void ExtractCustomerNamesFromQuery(string query, FinancialContext context)
        {
            var customers = ContextExtractor.ExtractCustomerNames(query);
            foreach (var customer in customers)
            {
                if (!context.Customers.Contains(customer))
                {
                    context.Customers.Add(customer);
                }
            }
        }

        /// <summary>
        /// Extracts categories from a query and adds them to the financial context
        /// </summary>
        private static void ExtractCategoriesFromQuery(string query, FinancialContext context)
        {
            var categories = ContextExtractor.ExtractCategories(query);
            foreach (var category in categories)
            {
                if (!context.Categories.Contains(category))
                {
                    context.Categories.Add(category);
                }
            }
        }

        /// <summary>
        /// Extracts time periods from a query and adds them to the financial context
        /// </summary>
        private static void ExtractTimePeriods(string query, FinancialContext context)
        {
            var timePeriods = ContextExtractor.ExtractTimePeriods(query);
            foreach (var timePeriod in timePeriods)
            {
                if (!context.TimePeriods.Contains(timePeriod))
                {
                    context.TimePeriods.Add(timePeriod);
                }
            }
        }

        /// <summary>
        /// Extracts query types from a query and adds them to the financial context
        /// </summary>
        private static void ExtractQueryTypesFromQuery(string query, FinancialContext context)
        {
            var lowerQuery = query.ToLower();
            var queryTypes = new List<string>();
            
            if (lowerQuery.Contains("transaction") || lowerQuery.Contains("transactie"))
                queryTypes.Add("transactions");
            if (lowerQuery.Contains("category") || lowerQuery.Contains("categorie"))
                queryTypes.Add("categories");
            if (lowerQuery.Contains("expense") || lowerQuery.Contains("uitgave") || lowerQuery.Contains("kosten"))
                queryTypes.Add("expenses");
            if (lowerQuery.Contains("cost") || lowerQuery.Contains("kosten"))
                queryTypes.Add("costs");
            if (lowerQuery.Contains("spending") || lowerQuery.Contains("uitgaven"))
                queryTypes.Add("spending");
            
            foreach (var queryType in queryTypes)
            {
                if (!context.QueryTypes.Contains(queryType))
                {
                    context.QueryTypes.Add(queryType);
                }
            }
        }

        /// <summary>
        /// Extracts customer names from a response and adds them to the financial context
        /// </summary>
        private static void ExtractCustomerNamesFromResponse(string response, FinancialContext context)
        {
            var customers = ContextExtractor.ExtractCustomerNamesFromResponse(response);
            foreach (var customer in customers)
            {
                if (!context.Customers.Contains(customer))
                {
                    context.Customers.Add(customer);
                }
            }
        }

        /// <summary>
        /// Extracts categories from a response and adds them to the financial context
        /// </summary>
        private static void ExtractCategoriesFromResponse(string response, FinancialContext context)
        {
            var categories = ContextExtractor.ExtractCategoriesFromResponse(response);
            foreach (var category in categories)
            {
                if (!context.Categories.Contains(category))
                {
                    context.Categories.Add(category);
                }
            }
        }

        /// <summary>
        /// Extracts time periods from a response and adds them to the financial context
        /// </summary>
        private static void ExtractTimePeriodsFromResponse(string response, FinancialContext context)
        {
            var timePeriods = ContextExtractor.ExtractTimePeriodsFromResponse(response);
            foreach (var timePeriod in timePeriods)
            {
                if (!context.TimePeriods.Contains(timePeriod))
                {
                    context.TimePeriods.Add(timePeriod);
                }
            }
        }

        /// <summary>
        /// Extracts query types from a response and adds them to the financial context
        /// </summary>
        private static void ExtractQueryTypesFromResponse(string response, FinancialContext context)
        {
            var lowerResponse = response.ToLower();
            var queryTypes = new List<string>();
            
            if (lowerResponse.Contains("transaction") || lowerResponse.Contains("transactie"))
                queryTypes.Add("transactions");
            if (lowerResponse.Contains("category") || lowerResponse.Contains("categorie"))
                queryTypes.Add("categories");
            if (lowerResponse.Contains("expense") || lowerResponse.Contains("uitgave") || lowerResponse.Contains("kosten"))
                queryTypes.Add("expenses");
            if (lowerResponse.Contains("cost") || lowerResponse.Contains("kosten"))
                queryTypes.Add("costs");
            if (lowerResponse.Contains("spending") || lowerResponse.Contains("uitgaven"))
                queryTypes.Add("spending");
            
            foreach (var queryType in queryTypes)
            {
                if (!context.QueryTypes.Contains(queryType))
                {
                    context.QueryTypes.Add(queryType);
                }
            }
        }

        /// <summary>
        /// Adds context to a question in a natural way
        /// </summary>
        public static string AddContextToQuestion(string question, string context)
        {
            // Don't add context if it's already mentioned in the question
            if (question.ToLower().Contains(context.ToLower()))
            {
                return question;
            }

            // Add context in a natural way
            if (question.EndsWith("?"))
            {
                question = question.TrimEnd('?');
                return $"{question} for {context}?";
            }
            else
            {
                return $"{question} for {context}";
            }
        }
    }

    /// <summary>
    /// Represents financial context extracted from chat history
    /// </summary>
    public class FinancialContext
    {
        public List<string> Customers { get; set; } = new();
        public List<string> Categories { get; set; } = new();
        public List<string> TimePeriods { get; set; } = new();
        public List<string> QueryTypes { get; set; } = new();
    }
}
