using System;
using System.Collections.Generic;
using System.Linq;

namespace TransactionLabeler.API
{
    public class TestFuzzyMatching
    {
        public static void TestCustomerMatching()
        {
            // Simulate the database results
            var databaseCustomers = new List<string> { "Nova Creations", "Golven Mobiliteit B.V." };
            
            // Test search term
            string searchTerm = "Noa Creations";
            
            // Simulate the fuzzy matching logic
            var similarCustomers = databaseCustomers
                .Where(c => 
                    c.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    searchTerm.Contains(c.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ||
                    c.Contains(searchTerm.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            Console.WriteLine($"Search term: {searchTerm}");
            Console.WriteLine($"Database customers: {string.Join(", ", databaseCustomers)}");
            Console.WriteLine($"Similar customers found: {string.Join(", ", similarCustomers)}");
            
            // Test SOUNDEX similarity
            var soundexResults = databaseCustomers
                .Where(c => GetSoundex(c) == GetSoundex(searchTerm))
                .ToList();
            
            Console.WriteLine($"SOUNDEX matches: {string.Join(", ", soundexResults)}");
        }
        
        private static string GetSoundex(string input)
        {
            // Simple SOUNDEX implementation
            if (string.IsNullOrEmpty(input)) return "0000";
            
            input = input.ToUpper();
            var result = new System.Text.StringBuilder();
            result.Append(input[0]);
            
            var soundexMap = new Dictionary<char, char>
            {
                {'B', '1'}, {'F', '1'}, {'P', '1'}, {'V', '1'},
                {'C', '2'}, {'G', '2'}, {'J', '2'}, {'K', '2'}, {'Q', '2'}, {'S', '2'}, {'X', '2'}, {'Z', '2'},
                {'D', '3'}, {'T', '3'},
                {'L', '4'},
                {'M', '5'}, {'N', '5'},
                {'R', '6'}
            };
            
            for (int i = 1; i < input.Length && result.Length < 4; i++)
            {
                char c = input[i];
                if (soundexMap.ContainsKey(c))
                {
                    char digit = soundexMap[c];
                    if (result[result.Length - 1] != digit)
                    {
                        result.Append(digit);
                    }
                }
            }
            
            while (result.Length < 4)
            {
                result.Append('0');
            }
            
            return result.ToString();
        }
    }
} 