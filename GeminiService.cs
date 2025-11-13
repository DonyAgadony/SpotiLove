using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Swan;

namespace Spotilove;

public class GeminiService
{

    public static async Task<int?> CalculatePercentage(MusicProfile p1, MusicProfile p2)
    {
        Console.WriteLine("Tries to approach Gemini");
        try
        {
            string? GeminiApi = Environment.GetEnvironmentVariable("GeminiAPIKey");
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={GeminiApi}";

            // IMPROVED PROMPT - More explicit about output format
            string prompt = $@"Calculate music compatibility between two people.

Person A:
Genres: {p1.FavoriteGenres}
Artists: {p1.FavoriteArtists}
Songs: {p1.FavoriteSongs}

Person B:
Genres: {p2.FavoriteGenres}
Artists: {p2.FavoriteArtists}
Songs: {p2.FavoriteSongs}

Use these weights:
- Genres: 30%
- Artists: 40%
- Songs: 30%

IMPORTANT: Return ONLY a single integer number between 0 and 100. 
Do not include any explanation, code, markdown, or other text.
Example valid responses: 78 or 45 or 92
Invalid responses: 78% or '78' or ```78``` or any explanation";

            var requestBody = new
            {
                contents = new object[]
                {
                    new {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = prompt }
                        }
                    }
                },
            };

            using HttpClient client = new HttpClient();
            var content = new StringContent(requestBody.ToJson(), Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response = await client.PostAsync(url, content);
            string responseString = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Response from Gemini: {responseString}");

            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(responseString);
                var text = document.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString()
                    ?.Trim();

                if (string.IsNullOrEmpty(text))
                {
                    Console.WriteLine("Received empty response text");
                    return null;
                }

                Console.WriteLine($"Extracted response: '{text}'");

                // IMPROVED PARSING - Extract number from various formats
                return ExtractNumber(text);
            }
            else
            {
                Console.WriteLine($"Error: {response.StatusCode}");
                Console.WriteLine($"Response content: {responseString}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception in CalculatePercentage: {ex.Message}");
            return null;
        }
    }
        
    /// Extracts a number from text that might contain markdown, code blocks, or other formatting
    private static int? ExtractNumber(string text)
    {
        try
        {
            // Remove common markdown and formatting
            text = text.Replace("```python", "")
                      .Replace("```", "")
                      .Replace("`", "")
                      .Replace("%", "")
                      .Replace("*", "")
                      .Trim();

            // Try to parse directly first
            if (int.TryParse(text, out int directResult))
            {
                return Math.Clamp(directResult, 0, 100);
            }

            // Use regex to find the first number in the text
            var match = Regex.Match(text, @"\b(\d{1,3})\b");
            if (match.Success)
            {
                int result = int.Parse(match.Groups[1].Value);
                return Math.Clamp(result, 0, 100);
            }

            Console.WriteLine($"Could not extract number from: {text}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting number: {ex.Message}");
            return null;
        }
    }
}