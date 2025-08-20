using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TransactionLabeler.API.Services
{
    /// <summary>
    /// Helper class for extracting various types of context from text content
    /// </summary>
    public static class ContextExtractor
    {
        /// <summary>
        /// Extracts customer names from content using pattern matching
        /// </summary>
        public static List<string> ExtractCustomerNames(string content)
        {
            var customers = new List<string>();
            
            // Look for patterns like "for [Customer]", "customer [Customer]", "for customer [Customer]"
            var patterns = new[] { "for ", "customer ", "for customer " };
            foreach (var pattern in patterns)
            {
                if (content.Contains(pattern))
                {
                    var parts = content.Split(new[] { pattern }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var words = part.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length > 0)
                        {
                            // Try to extract customer name - could be 1, 2, 3+ words
                            var customerWords = new List<string>();
                            foreach (var word in words)
                            {
                                if (word.Length > 2 && !IsCommonWord(word))
                                {
                                    customerWords.Add(word);
                                }
                                else if (customerWords.Count > 0)
                                {
                                    // Stop if we hit a common word after finding customer words
                                    break;
                                }
                            }
                            
                            if (customerWords.Count > 0)
                            {
                                var customer = string.Join(" ", customerWords);
                                if (customer.Length > 3)
                                {
                                    customers.Add(customer);
                                }
                            }
                        }
                    }
                }
            }
            
            return customers.Distinct().ToList();
        }

        /// <summary>
        /// Extracts categories from content using pattern matching
        /// </summary>
        public static List<string> ExtractCategories(string content)
        {
            var categories = new List<string>();
            
            // Look for patterns like "category [Category]", "for [Category]", "related to [Category]"
            var patterns = new[] { "category ", "for ", "related to ", "about ", "in ", "of " };
            foreach (var pattern in patterns)
            {
                if (content.Contains(pattern))
                {
                    var parts = content.Split(new[] { pattern }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var words = part.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length > 0)
                        {
                            // Try to extract category - could be 1, 2, 3+ words
                            var categoryWords = new List<string>();
                            foreach (var word in words)
                            {
                                if (word.Length > 2 && !IsCommonWord(word))
                                {
                                    categoryWords.Add(word);
                                }
                                else if (categoryWords.Count > 0)
                                {
                                    // Stop if we hit a common word after finding category words
                                    break;
                                }
                            }
                            
                            if (categoryWords.Count > 0)
                            {
                                var category = string.Join(" ", categoryWords);
                                if (category.Length > 3)
                                {
                                    categories.Add(category);
                                }
                            }
                        }
                    }
                }
            }
            
            return categories.Distinct().ToList();
        }

        /// <summary>
        /// Extracts time periods from content using regex patterns
        /// </summary>
        public static List<string> ExtractTimePeriods(string content)
        {
            var timePeriods = new List<string>();
            
            // Enhanced regex pattern for time periods
            var timePattern = @"\b(?:20[12]\d|Q[1-4]|quarter\s+[1-4]|january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec|today|yesterday|tomorrow|this\s+week|this\s+month|this\s+year|last\s+week|last\s+month|last\s+year|next\s+week|next\s+month|next\s+year)\b";
            
            var matches = Regex.Matches(content, timePattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var timePeriod = match.Value.Trim();
                if (!string.IsNullOrEmpty(timePeriod) && timePeriod.Length > 1)
                {
                    timePeriods.Add(timePeriod);
                }
            }
            
            return timePeriods.Distinct().ToList();
        }

        /// <summary>
        /// Extracts customer names from AI response content
        /// </summary>
        public static List<string> ExtractCustomerNamesFromResponse(string content)
        {
            var customers = new List<string>();
            
            // Look for customer names in AI responses (usually more structured)
            var patterns = new[] { "customer ", "for ", "in ", "related to " };
            foreach (var pattern in patterns)
            {
                if (content.Contains(pattern))
                {
                    var parts = content.Split(new[] { pattern }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var words = part.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length > 0)
                        {
                            var customerWords = new List<string>();
                            foreach (var word in words)
                            {
                                if (word.Length > 2 && !IsCommonWord(word) && !IsQuestionWord(word))
                                {
                                    customerWords.Add(word);
                                }
                                else if (customerWords.Count > 0)
                                {
                                    break;
                                }
                            }
                            
                            if (customerWords.Count > 0)
                            {
                                var customer = string.Join(" ", customerWords);
                                if (customer.Length > 3)
                                {
                                    customers.Add(customer);
                                }
                            }
                        }
                    }
                }
            }
            
            return customers.Distinct().ToList();
        }

        /// <summary>
        /// Extracts categories from AI response content
        /// </summary>
        public static List<string> ExtractCategoriesFromResponse(string content)
        {
            var categories = new List<string>();
            
            // Look for categories in AI responses (usually more structured)
            var patterns = new[] { "category ", "categories ", "for ", "related to ", "about " };
            foreach (var pattern in patterns)
            {
                if (content.Contains(pattern))
                {
                    var parts = content.Split(new[] { pattern }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var words = part.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length > 0)
                        {
                            var categoryWords = new List<string>();
                            foreach (var word in words)
                            {
                                if (word.Length > 2 && !IsCommonWord(word) && !IsQuestionWord(word))
                                {
                                    categoryWords.Add(word);
                                }
                                else if (categoryWords.Count > 0)
                                {
                                    break;
                                }
                            }
                            
                            if (categoryWords.Count > 0)
                            {
                                var category = string.Join(" ", categoryWords);
                                if (category.Length > 3)
                                {
                                    categories.Add(category);
                                }
                            }
                        }
                    }
                }
            }
            
            return categories.Distinct().ToList();
        }

        /// <summary>
        /// Extracts time periods from AI response content
        /// </summary>
        public static List<string> ExtractTimePeriodsFromResponse(string content)
        {
            var timePeriods = new List<string>();
            
            // Enhanced regex pattern for time periods in responses
            var timePattern = @"\b(?:20[12]\d|Q[1-4]|quarter\s+[1-4]|january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec|today|yesterday|tomorrow|this\s+week|this\s+month|this\s+year|last\s+week|last\s+month|last\s+year|next\s+week|next\s+month|next\s+year)\b";
            
            var matches = Regex.Matches(content, timePattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var timePeriod = match.Value.Trim();
                if (!string.IsNullOrEmpty(timePeriod) && timePeriod.Length > 1)
                {
                    timePeriods.Add(timePeriod);
                }
            }
            
            return timePeriods.Distinct().ToList();
        }

        /// <summary>
        /// Checks if a word is a common word that should be filtered out
        /// </summary>
        private static bool IsCommonWord(string word)
        {
            var commonWords = new[]
            {
                "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
                "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
                "will", "would", "could", "should", "may", "might", "can", "must", "shall",
                "this", "that", "these", "those", "i", "you", "he", "she", "it", "we", "they",
                "me", "him", "her", "us", "them", "my", "your", "his", "her", "its", "our", "their",
                "mine", "yours", "hers", "ours", "theirs", "myself", "yourself", "himself", "herself",
                "itself", "ourselves", "yourselves", "themselves",
                "what", "when", "where", "who", "whom", "which", "whose", "why", "how",
                "all", "any", "both", "each", "few", "more", "most", "other", "some", "such",
                "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very"
            };
            
            return commonWords.Contains(word.ToLower());
        }

        /// <summary>
        /// Checks if a word is a question word that should be filtered out
        /// </summary>
        private static bool IsQuestionWord(string word)
        {
            var questionWords = new[]
            {
                "what", "when", "where", "who", "whom", "which", "whose", "why", "how",
                "can", "could", "would", "should", "will", "may", "might", "must", "shall"
            };
            
            return questionWords.Contains(word.ToLower());
        }
    }
}
