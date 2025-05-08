using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;
using System.Text.RegularExpressions;

class Program
{
    private static readonly HttpClient httpClient = new HttpClient();
    private const string DexScreenerApi = "https://api.dexscreener.com/latest/dex/pairs";
    private static readonly List<(string chain, string address)> Pairs = new()
    {
        ("ethereum", "0x1E49768714E438E789047f48FD386686a5707db2"),
        ("bsc", "0xC6585bc17b53792f281a9739579DD60535c1F9FB")
    };

    private static double thresholdPercentage = 5.0; // Default threshold
    private static long lastUpdateId = 0;

    static async Task Main()
    {
        var telegramToken = "7732503206:AAE3c1thGs8MDAv4tYGkt9fFadCxbfap4J4";
        var chatId = "2673923132";

        if (string.IsNullOrEmpty(telegramToken) || string.IsNullOrEmpty(chatId))
        {
            Console.WriteLine("Please set TELEGRAM_TOKEN and TELEGRAM_CHAT_ID environment variables");
            return;
        }

        // Start Telegram updates polling in background
        var cts = new CancellationTokenSource();
        var telegramTask = Task.Run(() => PollTelegramUpdates(telegramToken, cts.Token));

        bool prevAlertSent = false;
        var culture = CultureInfo.InvariantCulture;

        try
        {
            while (true)
            {
                try
                {
                    var prices = new List<double>();

                    foreach (var pair in Pairs)
                    {
                        var data = await GetPairData(pair.chain, pair.address);
                        var price = GetPrice(data, culture);
                        if (price.HasValue) prices.Add(price.Value);
                    }

                    if (prices.Count != 2)
                    {
                        Console.WriteLine("Warning: Didn't get both prices, retrying...");
                        continue;
                    }

                    var difference = CalculatePriceDifference(prices[0], prices[1]);
                    Console.WriteLine($"Current difference: {difference:F2}% (Threshold: {thresholdPercentage}%)");

                    if (difference >= thresholdPercentage && !prevAlertSent)
                    {
                        var message = $"🚨 Price difference alert: {difference:F2}% (Threshold: {thresholdPercentage}%)\n" +
                                     $"ETH: ${prices[0]:F8}\n" +
                                     $"BSC: ${prices[1]:F8}";

                        await SendTelegramAlert(telegramToken, chatId, message);
                        prevAlertSent = true;
                        Console.WriteLine("Alert sent!");
                    }
                    else if (difference < thresholdPercentage)
                    {
                        prevAlertSent = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            cts.Cancel();
            await telegramTask;
        }
    }

    private static async Task PollTelegramUpdates(string token, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{token}/getUpdates?offset={lastUpdateId + 1}";
                var response = await httpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("result", out var results))
                {
                    foreach (var update in results.EnumerateArray())
                    {
                        lastUpdateId = update.GetProperty("update_id").GetInt64();

                        if (update.TryGetProperty("message", out var message))
                        {
                            var chat = message.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                            var text = message.GetProperty("text").GetString();

                            if (text.StartsWith("/setthreshold "))
                            {
                                var parts = text.Split(' ');
                                if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var newThreshold))
                                {
                                    thresholdPercentage = newThreshold;
                                    await SendTelegramAlert(token, chat, $"✅ Threshold updated to {newThreshold}%");
                                    Console.WriteLine($"Threshold updated to {newThreshold}%");
                                }
                                else
                                {
                                    await SendTelegramAlert(token, chat, "❌ Invalid format. Use /setthreshold 5.0");
                                }
                            }
                            else if (text.StartsWith("/getthreshold"))
                            {
                                await SendTelegramAlert(token, chat, $"Current threshold: {thresholdPercentage}%");
                            }
                            else if (text.StartsWith("/currentdifference"))
                            {
                                var prices = new List<double>();

                                foreach (var pair in Pairs)
                                {
                                    var data = await GetPairData(pair.chain, pair.address);
                                    var culture = CultureInfo.InvariantCulture;
                                    var price = GetPrice(data, culture);
                                    if (price.HasValue) prices.Add(price.Value);
                                }

                                if (prices.Count != 2)
                                {
                                    Console.WriteLine("Warning: Didn't get both prices, retrying...");
                                    continue;
                                }
                                var difference = CalculatePriceDifference(prices[0], prices[1]);
                                var message_tg = $"Curret difference: {difference:F2}% (Threshold: {thresholdPercentage}%)\n" +
                                                 $"ETH: ${prices[0]:F8}\n" +
                                                 $"BSC: ${prices[1]:F8}";
                                await SendTelegramAlert(token, chat, message_tg);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram polling error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static async Task<JsonDocument> GetPairData(string chain, string pairAddress)
    {
        var url = $"{DexScreenerApi}/{chain}/{pairAddress}";
        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static double? GetPrice(JsonDocument data, CultureInfo culture)
    {
        try
        {
            if (data.RootElement.TryGetProperty("pair", out var pair) &&
                pair.TryGetProperty("priceUsd", out var price) &&
                price.ValueKind == JsonValueKind.String)
            {
                var priceStr = price.GetString();
                if (double.TryParse(priceStr, NumberStyles.Any, culture, out var result))
                {
                    return result;
                }
                Console.WriteLine($"Warning: Couldn't parse price value: {priceStr}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing price: {ex.Message}");
        }
        return null;
    }

    private static double CalculatePriceDifference(double price1, double price2)
    {
        return Math.Abs((price1 - price2) / ((price1 + price2) / 2)) * 100;
    }

    private static async Task SendTelegramAlert(string token, string chatId, string message)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", chatId),
                new KeyValuePair<string, string>("text", message)
            });

            var response = await httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Telegram send error: {ex.Message}");
        }
    }
}