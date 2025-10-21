using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Swan;
namespace Spotilove;

public class GeminiService
{
    string filePath;
    public GeminiService(string filePath)
    {
        this.filePath = filePath;
    }
    public static async Task<int?> CalculatePercentage(MusicProfile p1, MusicProfile p2)
    {
        Console.WriteLine("Tries to approach Gemini");
        try
        {
            string? GeminiApi = Environment.GetEnvironmentVariable("GeminiAPIKey");
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={GeminiApi}";
            string prompt = $@"You are an assistant that calculates music compatibility between two people based on their music taste.
Each person’s data includes:

Top genres

Top artists

Top songs

Task:

Compare the overlap between the two people’s top genres, artists, and songs.

Assign weights:

Genres = 30%

Artists = 40%

Songs = 30%

Return only a single number: the compatibility percentage (0–100). Do not return any explanation or text, just the percentage.

Input format example:
Person A:

Genres: {p1.FavoriteGenres}

Artists: {p1.FavoriteArtists}

Songs: {p1.FavoriteSongs}

Person B:

Genres: {p2.FavoriteGenres}

Artists: D{p2.FavoriteArtists}

Songs: {p2.FavoriteSongs}

Output format example:
78";

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
                return int.Parse(text);
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
            Console.WriteLine($"Exception in CalculatePercenatge: {ex.Message}");
            return null;
        }
    }
}