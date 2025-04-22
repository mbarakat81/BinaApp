using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace Binance
{
    internal class CryptoDataCollector
    {
        private static readonly HttpClient client = new HttpClient();
        private static string token = "IQ"; // Default token
        private static string tokenContract = "0x579cea1889991f68acc35ff5c3dd0621ff29b0c9"; // IQ token contract address (Ethereum)

        static async Task GetTokenPrice()
        {
            Console.WriteLine("\nFetching current price data...");
            try
            {
                // Using CoinGecko API for price data
                string url = $"https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&ids={token.ToLower()}&order=market_cap_desc";
                var response = await client.GetStringAsync(url);
                var data = JsonConvert.DeserializeObject<List<dynamic>>(response);

                if (data.Count > 0)
                {
                    Console.WriteLine($"Current Price: ${data[0].current_price}");
                    Console.WriteLine($"24h Change: {data[0].price_change_percentage_24h}%");
                    Console.WriteLine($"Market Cap: ${data[0].market_cap}");
                    Console.WriteLine($"24h Volume: ${data[0].total_volume}");
                }
                else
                {
                    Console.WriteLine("Token not found on CoinGecko.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch price data: {ex.Message}");
            }
        }

        static async Task GetOrderBookDepth()
        {
            Console.WriteLine("\nFetching order book depth...");
            try
            {
                // Using Binance API as an example
                string symbol = $"{token}USDT".ToUpper();
                string url = $"https://api.binance.com/api/v3/depth?symbol={symbol}&limit=500";
                var response = await client.GetStringAsync(url);
                var data = JsonConvert.DeserializeObject<JObject>(response);

                var bids = data["bids"];
                var asks = data["asks"];

                // Calculate total volume at different price levels
                double bidVolume = 0;
                double askVolume = 0;

                Console.WriteLine("Order Book Summary:");
                Console.WriteLine("Bid Levels (Buy Orders):");
                for (int i = 0; i < Math.Min(5, bids.Count()); i++)
                {
                    var price = double.Parse(bids[i][0].ToString());
                    var amount = double.Parse(bids[i][1].ToString());
                    bidVolume += amount;
                    Console.WriteLine($"  Price: ${price:F8} - Amount: {amount:F2} {token}");
                }

                Console.WriteLine("Ask Levels (Sell Orders):");
                for (int i = 0; i < Math.Min(5, asks.Count()); i++)
                {
                    var price = double.Parse(asks[i][0].ToString());
                    var amount = double.Parse(asks[i][1].ToString());
                    askVolume += amount;
                    Console.WriteLine($"  Price: ${price:F8} - Amount: {amount:F2} {token}");
                }

                // Calculate buy/sell ratio
                double buyToSellRatio = bidVolume / askVolume;
                Console.WriteLine($"Buy/Sell Ratio (top 5 levels): {buyToSellRatio:F2}");

                // Detect potential manipulation patterns
                AnalyzeOrderBook(bids.ToArray(), asks.ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch order book: {ex.Message}");
            }
        }

        static void AnalyzeOrderBook(JToken[] bids, JToken[] asks)
        {
            // Look for suspicious patterns in order book
            try
            {
                // Check for walls (abnormally large orders)
                var suspiciousWalls = new List<string>();

                double avgBidSize = bids.Take(10).Average(b => double.Parse(b[1].ToString()));
                double avgAskSize = asks.Take(10).Average(a => double.Parse(a[1].ToString()));

                for (int i = 0; i < Math.Min(10, bids.Length); i++)
                {
                    double size = double.Parse(bids[i][1].ToString());
                    if (size > avgBidSize * 5)
                    {
                        suspiciousWalls.Add($"Large bid wall at ${bids[i][0]} ({size:F2} {token})");
                    }
                }

                for (int i = 0; i < Math.Min(10, asks.Length); i++)
                {
                    double size = double.Parse(asks[i][1].ToString());
                    if (size > avgAskSize * 5)
                    {
                        suspiciousWalls.Add($"Large ask wall at ${asks[i][0]} ({size:F2} {token})");
                    }
                }

                // Check for spoof orders (many small orders at similar prices)
                // This is a simplified detection mechanism
                var bidClusters = GetPriceClusters(bids.Take(50).ToArray());
                var askClusters = GetPriceClusters(asks.Take(50).ToArray());

                if (suspiciousWalls.Count > 0)
                {
                    Console.WriteLine("\nPotentially suspicious order book patterns:");
                    foreach (var wall in suspiciousWalls)
                    {
                        Console.WriteLine($"  {wall}");
                    }
                }

                Console.WriteLine($"\nBid cluster count: {bidClusters.Count} (potential spoofing if > 5)");
                Console.WriteLine($"Ask cluster count: {askClusters.Count} (potential spoofing if > 5)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing order book: {ex.Message}");
            }
        }

        static List<List<JToken>> GetPriceClusters(JToken[] orders, double clusterThreshold = 0.001)
        {
            var clusters = new List<List<JToken>>();
            var currentCluster = new List<JToken>();

            if (orders.Length > 0)
            {
                currentCluster.Add(orders[0]);
                double clusterPrice = double.Parse(orders[0][0].ToString());

                for (int i = 1; i < orders.Length; i++)
                {
                    double price = double.Parse(orders[i][0].ToString());
                    if (Math.Abs(price - clusterPrice) / clusterPrice < clusterThreshold)
                    {
                        currentCluster.Add(orders[i]);
                    }
                    else
                    {
                        if (currentCluster.Count >= 3) // Consider it a cluster if 3+ orders
                        {
                            clusters.Add(currentCluster);
                        }
                        currentCluster = new List<JToken> { orders[i] };
                        clusterPrice = price;
                    }
                }

                // Don't forget to add the last cluster
                if (currentCluster.Count >= 3)
                {
                    clusters.Add(currentCluster);
                }
            }

            return clusters;
        }

        static async Task GetTopWallets()
        {
            Console.WriteLine("\nFetching top wallet distribution...");
            try
            {
                // Using Ethplorer API for Ethereum tokens
                string url = $"https://api.ethplorer.io/getTopTokenHolders/{tokenContract}?apiKey=freekey&limit=10";
                var response = await client.GetStringAsync(url);
                var data = JsonConvert.DeserializeObject<JObject>(response);

                var holders = data["holders"];

                Console.WriteLine("Top 10 Token Holders:");
                double totalPercentage = 0;

                for (int i = 0; i < holders.Count(); i++)
                {
                    var address = holders[i]["address"].ToString();
                    var balance = Convert.ToDouble(holders[i]["balance"].ToString());
                    var share = Convert.ToDouble(holders[i]["share"].ToString()) * 100;
                    totalPercentage += share;

                    Console.WriteLine($"  {i + 1}. Address: {address.Substring(0, 8)}...{address.Substring(address.Length - 6)}");
                    Console.WriteLine($"     Balance: {balance:N0} {token}");
                    Console.WriteLine($"     Share: {share:F2}%");
                }

                Console.WriteLine($"Top 10 holders control: {totalPercentage:F2}% of supply");

                // Suspicious concentration warning
                if (totalPercentage > 70)
                {
                    Console.WriteLine("WARNING: High token concentration detected!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch wallet distribution: {ex.Message}");
            }
        }

        static async Task GetSocialSentiment()
        {
            Console.WriteLine("\nAnalyzing social sentiment...");
            try
            {
                // Using CryptoCompare Social Stats API
                string url = $"https://min-api.cryptocompare.com/data/social/coin/latest?coinId={token.ToUpper()}";
                var response = await client.GetStringAsync(url);
                var data = JsonConvert.DeserializeObject<JObject>(response);

                var reddit = data["Data"]["Reddit"];
                var twitter = data["Data"]["Twitter"];

                Console.WriteLine("Social Media Activity:");
                Console.WriteLine($"  Reddit Active Users: {reddit["active_users"]}");
                Console.WriteLine($"  Reddit Posts Per Hour: {reddit["posts_per_hour"]}");
                Console.WriteLine($"  Reddit Comments Per Hour: {reddit["comments_per_hour"]}");
                Console.WriteLine($"  Twitter Followers: {twitter["followers"]}");
                Console.WriteLine($"  Twitter Statuses: {twitter["statuses"]}");

                // Check for abnormal social activity
                double postsPerFollower = Convert.ToDouble(reddit["posts_per_hour"]) /
                                         (Convert.ToDouble(reddit["active_users"]) + 1);

                if (postsPerFollower > 0.1) // Arbitrary threshold
                {
                    Console.WriteLine("WARNING: Unusually high posting activity detected! Potential coordinated action.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch social sentiment: {ex.Message}");
            }
        }

        static async Task GetExchangeFlows()
        {
            Console.WriteLine("\nAnalyzing exchange inflows/outflows...");
            try
            {
                // Note: This would typically require a paid API service like IntoTheBlock or Glassnode
                // Using a placeholder API for demonstration
                string url = $"https://api.example.com/token-flows/{token.ToLower()}"; // Placeholder URL

                // Simulating response for demonstration purposes
                // In a real implementation, you would parse the actual API response
                Console.WriteLine("Exchange Flow Data (Last 24h):");
                Console.WriteLine("  Inflow to Exchanges: 2,450,000 IQ");
                Console.WriteLine("  Outflow from Exchanges: 1,750,000 IQ");
                Console.WriteLine("  Net Flow: -700,000 IQ (negative means more tokens entering exchanges)");

                // Interpretation
                Console.WriteLine("\nInterpretation:");
                Console.WriteLine("  Negative net flow suggests selling pressure as tokens move to exchanges.");
                Console.WriteLine("  This pattern often precedes further price declines.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch exchange flows: {ex.Message}");
            }
        }

        static async Task GetVolumeAnalysis()
        {
            Console.WriteLine("\nAnalyzing trading volume patterns...");
            try
            {
                // Using CoinGecko API for historical data
                string url = $"https://api.coingecko.com/api/v3/coins/{token.ToLower()}/market_chart?vs_currency=usd&days=14";
                var response = await client.GetStringAsync(url);
                var data = JsonConvert.DeserializeObject<JObject>(response);

                var volumes = data["total_volumes"].ToArray();
                var prices = data["prices"].ToArray();

                // Extract just the volume values and calculate statistics
                var volumeValues = volumes.Select(v => Convert.ToDouble(v[1])).ToArray();
                double avgVolume = volumeValues.Average();
                double maxVolume = volumeValues.Max();
                double latestVolume = volumeValues.Last();

                Console.WriteLine("Volume Analysis (14-day period):");
                Console.WriteLine($"  Average Daily Volume: ${avgVolume:N0}");
                Console.WriteLine($"  Maximum Daily Volume: ${maxVolume:N0}");
                Console.WriteLine($"  Latest Volume: ${latestVolume:N0}");
                Console.WriteLine($"  Latest vs Average: {(latestVolume / avgVolume):P2}");

                // Check for suspicious volume patterns
                if (latestVolume > avgVolume * 3)
                {
                    Console.WriteLine("WARNING: Abnormally high trading volume detected!");
                }

                // Calculate price-volume correlation
                var priceValues = prices.Select(p => Convert.ToDouble(p[1])).ToArray();
                double correlation = CalculateCorrelation(priceValues, volumeValues);

                Console.WriteLine($"  Price-Volume Correlation: {correlation:F2}");
                Console.WriteLine($"  Interpretation: {InterpretCorrelation(correlation)}");

                // Detect wash trading patterns
                DetectWashTrading(volumeValues, priceValues);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to analyze trading volume: {ex.Message}");
            }
        }

        static void DetectWashTrading(double[] volumes, double[] prices)
        {
            try
            {
                // Calculate volume volatility vs price volatility
                double volumeStdDev = StandardDeviation(volumes);
                double priceStdDev = StandardDeviation(prices);

                double volumeCV = volumeStdDev / volumes.Average();
                double priceCV = priceStdDev / prices.Average();

                double volumeToPriceVolatilityRatio = volumeCV / priceCV;

                Console.WriteLine($"  Volume/Price Volatility Ratio: {volumeToPriceVolatilityRatio:F2}");

                if (volumeToPriceVolatilityRatio > 3.0)
                {
                    Console.WriteLine("WARNING: High volume volatility relative to price movement detected!");
                    Console.WriteLine("         This is a common indicator of potential wash trading.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in wash trading detection: {ex.Message}");
            }
        }

        static double StandardDeviation(double[] values)
        {
            double avg = values.Average();
            double sumOfSquaresOfDifferences = values.Select(val => (val - avg) * (val - avg)).Sum();
            double sd = Math.Sqrt(sumOfSquaresOfDifferences / values.Length);
            return sd;
        }

        static string InterpretCorrelation(double correlation)
        {
            if (correlation > 0.7)
                return "Strong positive correlation - volume increases with price (often seen in legitimate growth)";
            else if (correlation > 0.3)
                return "Moderate positive correlation";
            else if (correlation > -0.3)
                return "Weak correlation - volume and price moving independently";
            else if (correlation > -0.7)
                return "Moderate negative correlation";
            else
                return "Strong negative correlation - volume increases as price falls (often seen in panic selling)";
        }

        static async Task GetMarketCorrelation()
        {
            Console.WriteLine("\nCalculating market correlations...");
            try
            {
                // Get token historical price data
                string tokenUrl = $"https://api.coingecko.com/api/v3/coins/{token.ToLower()}/market_chart?vs_currency=usd&days=30";
                var tokenResponse = await client.GetStringAsync(tokenUrl);
                var tokenData = JsonConvert.DeserializeObject<JObject>(tokenResponse);
                var tokenPrices = tokenData["prices"].ToArray().Select(p => Convert.ToDouble(p[1])).ToArray();

                // Get Bitcoin price data for comparison
                string btcUrl = "https://api.coingecko.com/api/v3/coins/bitcoin/market_chart?vs_currency=usd&days=30";
                var btcResponse = await client.GetStringAsync(btcUrl);
                var btcData = JsonConvert.DeserializeObject<JObject>(btcResponse);
                var btcPrices = btcData["prices"].ToArray().Select(p => Convert.ToDouble(p[1])).ToArray();

                // Calculate correlation coefficient
                double correlation = CalculateCorrelation(tokenPrices, btcPrices);

                Console.WriteLine($"Correlation with Bitcoin (30 days): {correlation:F2}");

                if (Math.Abs(correlation) < 0.3)
                {
                    Console.WriteLine("Low correlation with Bitcoin suggests independent price movement.");
                    Console.WriteLine("This could indicate token-specific activity rather than market-wide trends.");
                }
                else if (correlation > 0.7)
                {
                    Console.WriteLine("High correlation with Bitcoin suggests this token follows the overall market.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to calculate market correlation: {ex.Message}");
            }
        }

        static double CalculateCorrelation(double[] x, double[] y)
        {
            // Make sure arrays are the same length
            int n = Math.Min(x.Length, y.Length);

            // Compute means
            double meanX = x.Take(n).Average();
            double meanY = y.Take(n).Average();

            // Compute sum of squares and sum of products
            double sumXY = 0;
            double sumX2 = 0;
            double sumY2 = 0;

            for (int i = 0; i < n; i++)
            {
                double xDiff = x[i] - meanX;
                double yDiff = y[i] - meanY;
                sumXY += xDiff * yDiff;
                sumX2 += xDiff * xDiff;
                sumY2 += yDiff * yDiff;
            }

            // Compute correlation coefficient
            if (sumX2 == 0 || sumY2 == 0)
                return 0;
            else
                return sumXY / Math.Sqrt(sumX2 * sumY2);
        }

        // Additional helper method to connect to exchange WebSocket for real-time order book updates
        static async Task ConnectToExchangeWebSocket()
        {
            using (var ws = new ClientWebSocket())
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMinutes(5)); // Timeout after 5 minutes

                Console.WriteLine("Connecting to exchange WebSocket...");
                await ws.ConnectAsync(new Uri($"wss://stream.binance.com:9443/ws/{token.ToLower()}usdt@depth"), cts.Token);

                var buffer = new byte[8192];

                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        // Process real-time order book updates
                        Console.WriteLine($"Update: {message}");
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                }
            }
        }
    }
}
